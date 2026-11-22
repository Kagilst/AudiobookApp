using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ATL;
using AudiobookApp.Models;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace AudiobookApp.Services
{
    public sealed class ChapterMarker
    {
        public ChapterMarker(string title, TimeSpan start, TimeSpan end)
        {
            Title = title;
            Start = start;
            End = end;
        }

        public string Title { get; }
        public TimeSpan Start { get; }
        public TimeSpan End { get; }
        public TimeSpan Duration => End - Start;
    }

    public enum SleepTimerMode
    {
        Off,
        Minutes,
        EndOfChapter,
        Chapters
    }

    public sealed class PlaybackService
    {
        private const string AutoplaySettingKey = "SeriesAutoplayEnabled";
        private const double MinSpeed = 0.5;
        private const double MaxSpeed = 4.0;

        private static readonly double[] SpeedOptions =
        {
            0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 3.5, 4.0
        };

        private readonly List<ChapterMarker> _chapters = new();
        private readonly List<TimeSpan> _partDurations = new();
        private readonly List<TimeSpan> _partStarts = new();
        private readonly DispatcherQueueTimer _sleepTimer;

        private MediaPlayer? _player;
        private MediaPlaybackList? _playbackList;
        private bool _sourceLoaded;
        private double _volumePercent = 100;
        private double _speed = 1.0;
        private Book? _pendingOpenBook;
        private TimeSpan _totalDuration;
        private bool _seriesAutoplayEnabled;
        private bool _bookFinishedRaised;

        private SleepTimerMode _sleepMode = SleepTimerMode.Off;
        private DateTimeOffset? _sleepDeadlineUtc;
        private TimeSpan _sleepRemainingWhenPaused = TimeSpan.Zero;
        private TimeSpan? _sleepTargetPosition;
        private int _sleepChaptersRemaining;
        private int _sleepLastChapterIndex = -1;

        public PlaybackService()
        {
            _seriesAutoplayEnabled = ReadAutoplayPreference();
            _sleepTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _sleepTimer.Interval = TimeSpan.FromMilliseconds(500);
            _sleepTimer.IsRepeating = true;
            _sleepTimer.Tick += (_, _) => TickSleepTimer();
        }

        public MediaPlayer Player => EnsurePlayer();

        public Book? CurrentBook { get; private set; }

        public IReadOnlyList<ChapterMarker> Chapters => _chapters;

        public bool HasChapters => _chapters.Count > 0;

        public bool HasSession => CurrentBook != null;

        public bool IsPlaying { get; private set; }

        public TimeSpan TotalDuration => _totalDuration > TimeSpan.Zero
            ? _totalDuration
            : CurrentBook?.Duration ?? TimeSpan.Zero;

        public double VolumePercent
        {
            get => _volumePercent;
            set
            {
                var clamped = Math.Clamp(value, 0, 100);
                if (Math.Abs(clamped - _volumePercent) < 0.01 && _player != null)
                    return;

                _volumePercent = clamped;

                if (_player != null)
                {
                    _player.Volume = _volumePercent / 100.0;
                    _player.IsMuted = _volumePercent <= 0;
                }

                StateChanged?.Invoke();
            }
        }

        public int SpeedIndex
        {
            get
            {
                var best = 0;
                var bestDelta = double.MaxValue;
                for (var i = 0; i < SpeedOptions.Length; i++)
                {
                    var delta = Math.Abs(SpeedOptions[i] - _speed);
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        best = i;
                    }
                }

                return best;
            }
        }

        public double Speed => _speed;

        public bool SeriesAutoplayEnabled
        {
            get => _seriesAutoplayEnabled;
            set
            {
                if (_seriesAutoplayEnabled == value)
                    return;

                _seriesAutoplayEnabled = value;
                SaveAutoplayPreference(value);
                StateChanged?.Invoke();
            }
        }

        public SleepTimerMode SleepMode => _sleepMode;

        public bool IsSleepTimerActive => _sleepMode != SleepTimerMode.Off;

        public event Action? StateChanged;
        public event Action<Book>? PlaybackPersisted;
        public event Action<Book>? BookFinished;

        public async Task OpenBookAsync(Book book, bool autoPlay = false)
        {
            book.NormalizeFilePaths();

            var isSameBook = CurrentBook != null && CurrentBook.Id == book.Id;

            if (isSameBook && _sourceLoaded)
            {
                CurrentBook = book;
                if (autoPlay)
                    Play();
                else
                    StateChanged?.Invoke();
                return;
            }

            if (CurrentBook != null && !isSameBook)
                PersistPlaybackState();

            CancelSleepTimer(raiseState: false);
            _bookFinishedRaised = false;

            var player = EnsurePlayer();
            CurrentBook = book;
            IsPlaying = false;
            player.Pause();

            if (book.PlaybackSpeed >= MinSpeed && book.PlaybackSpeed <= MaxSpeed)
                _speed = book.PlaybackSpeed;

            await PrepareTimelineAsync(book);
            LoadChapters(book);
            await LoadSourceAsync(book);

            if (autoPlay)
                Play();
            else
                StateChanged?.Invoke();
        }

        public void Play()
        {
            if (CurrentBook == null || !_sourceLoaded)
                return;

            if (_sleepMode == SleepTimerMode.Minutes && _sleepRemainingWhenPaused > TimeSpan.Zero)
            {
                _sleepDeadlineUtc = DateTimeOffset.UtcNow + _sleepRemainingWhenPaused;
                _sleepRemainingWhenPaused = TimeSpan.Zero;
                EnsureSleepTimerRunning();
            }

            Player.Play();
            IsPlaying = true;
            StateChanged?.Invoke();
        }

        public void Pause()
        {
            if (_player == null)
                return;

            if (_sleepMode == SleepTimerMode.Minutes && _sleepDeadlineUtc is DateTimeOffset deadline)
            {
                _sleepRemainingWhenPaused = deadline - DateTimeOffset.UtcNow;
                if (_sleepRemainingWhenPaused < TimeSpan.Zero)
                    _sleepRemainingWhenPaused = TimeSpan.Zero;
                _sleepDeadlineUtc = null;
            }

            _player.Pause();
            IsPlaying = false;
            PersistPlaybackState();
            StateChanged?.Invoke();
        }

        public void TogglePlayPause()
        {
            if (IsPlaying)
                Pause();
            else
                Play();
        }

        public void SetSpeedIndex(int index)
        {
            var next = Math.Clamp(index, 0, SpeedOptions.Length - 1);
            SetSpeed(SpeedOptions[next]);
        }

        public void SetSpeed(double rate)
        {
            var next = Math.Clamp(Math.Round(rate * 20) / 20.0, MinSpeed, MaxSpeed);
            if (Math.Abs(next - _speed) < 0.001 && _player != null)
            {
                _player.PlaybackSession.PlaybackRate = _speed;
                return;
            }

            _speed = next;

            if (_player != null)
                _player.PlaybackSession.PlaybackRate = _speed;

            if (CurrentBook != null)
                CurrentBook.PlaybackSpeed = _speed;

            StateChanged?.Invoke();
        }

        public TimeSpan GetAbsolutePosition()
        {
            if (_player == null || !_sourceLoaded)
                return TimeSpan.Zero;

            if (_playbackList == null || _partStarts.Count == 0)
                return _player.PlaybackSession.Position;

            var index = (int)_playbackList.CurrentItemIndex;
            if (index < 0 || index >= _partStarts.Count)
                index = 0;

            return _partStarts[index] + _player.PlaybackSession.Position;
        }

        public void SeekTo(TimeSpan absolutePosition)
        {
            if (_player == null || !_sourceLoaded)
                return;

            if (absolutePosition < TimeSpan.Zero)
                absolutePosition = TimeSpan.Zero;

            var total = TotalDuration;
            if (total > TimeSpan.Zero && absolutePosition > total)
                absolutePosition = total;

            if (_playbackList == null || _partStarts.Count <= 1)
            {
                _player.PlaybackSession.Position = absolutePosition;
                StateChanged?.Invoke();
                return;
            }

            var partIndex = 0;
            for (int i = _partStarts.Count - 1; i >= 0; i--)
            {
                if (absolutePosition >= _partStarts[i])
                {
                    partIndex = i;
                    break;
                }
            }

            var withinPart = absolutePosition - _partStarts[partIndex];
            if (withinPart < TimeSpan.Zero)
                withinPart = TimeSpan.Zero;

            if ((int)_playbackList.CurrentItemIndex != partIndex)
            {
                void OnItemChanged(MediaPlaybackList sender, CurrentMediaPlaybackItemChangedEventArgs args)
                {
                    sender.CurrentItemChanged -= OnItemChanged;
                    if (_player != null)
                        _player.PlaybackSession.Position = withinPart;
                    StateChanged?.Invoke();
                }

                _playbackList.CurrentItemChanged += OnItemChanged;
                _playbackList.MoveTo((uint)partIndex);
                return;
            }

            _player.PlaybackSession.Position = withinPart;
            StateChanged?.Invoke();
        }

        public void SeekBySeconds(double seconds)
        {
            if (_player == null || !_sourceLoaded)
                return;

            SeekTo(GetAbsolutePosition() + TimeSpan.FromSeconds(seconds));
        }

        public void PreviousChapter()
        {
            if (!HasChapters || _player == null || !_sourceLoaded)
                return;

            var position = GetAbsolutePosition();
            var index = FindChapterIndex(position);
            var chapter = _chapters[index];

            if (position - chapter.Start > TimeSpan.FromSeconds(3) || index == 0)
            {
                SeekTo(chapter.Start);
                return;
            }

            SeekTo(_chapters[index - 1].Start);
        }

        public void NextChapter()
        {
            if (!HasChapters || _player == null || !_sourceLoaded)
                return;

            var index = FindChapterIndex(GetAbsolutePosition());
            if (index + 1 >= _chapters.Count)
                return;

            SeekTo(_chapters[index + 1].Start);
        }

        public int FindChapterIndex(TimeSpan position)
        {
            if (_chapters.Count == 0)
                return 0;

            for (int i = _chapters.Count - 1; i >= 0; i--)
            {
                if (position >= _chapters[i].Start)
                    return i;
            }

            return 0;
        }

        public void PersistPlaybackState()
        {
            if (CurrentBook == null || _player == null)
                return;

            CurrentBook.LastPositionSeconds = GetAbsolutePosition().TotalSeconds;

            if (TotalDuration > TimeSpan.Zero)
                CurrentBook.Duration = TotalDuration;

            CurrentBook.PlaybackSpeed = Speed;
            PlaybackPersisted?.Invoke(CurrentBook);
        }

        public void StartSleepTimerMinutes(int minutes)
        {
            if (minutes <= 0)
            {
                CancelSleepTimer();
                return;
            }

            var remainingBook = GetRemainingBookTime();
            var requested = TimeSpan.FromMinutes(minutes);
            if (remainingBook > TimeSpan.Zero && requested > remainingBook)
                requested = remainingBook;

            _sleepMode = SleepTimerMode.Minutes;
            _sleepTargetPosition = null;
            _sleepChaptersRemaining = 0;
            _sleepLastChapterIndex = -1;
            _sleepRemainingWhenPaused = TimeSpan.Zero;

            if (IsPlaying)
            {
                _sleepDeadlineUtc = DateTimeOffset.UtcNow + requested;
            }
            else
            {
                _sleepDeadlineUtc = null;
                _sleepRemainingWhenPaused = requested;
            }

            EnsureSleepTimerRunning();
            StateChanged?.Invoke();
        }

        public void StartSleepTimerEndOfChapter()
        {
            var position = GetAbsolutePosition();
            TimeSpan target;

            if (HasChapters)
            {
                var index = FindChapterIndex(position);
                target = _chapters[index].End;
            }
            else
            {
                target = TotalDuration > TimeSpan.Zero ? TotalDuration : position;
            }

            if (TotalDuration > TimeSpan.Zero && target > TotalDuration)
                target = TotalDuration;

            ArmPositionSleep(SleepTimerMode.EndOfChapter, target);
        }

        public void StartSleepTimerChapters(int chapterCount)
        {
            if (chapterCount <= 0)
            {
                CancelSleepTimer();
                return;
            }

            if (!HasChapters)
            {
                // No chapter markers — treat as end of book.
                ArmPositionSleep(SleepTimerMode.Chapters, TotalDuration > TimeSpan.Zero
                    ? TotalDuration
                    : GetAbsolutePosition());
                return;
            }

            var position = GetAbsolutePosition();
            var currentIndex = FindChapterIndex(position);
            var targetIndex = Math.Min(currentIndex + chapterCount - 1, _chapters.Count - 1);
            var target = _chapters[targetIndex].End;
            if (TotalDuration > TimeSpan.Zero && target > TotalDuration)
                target = TotalDuration;

            _sleepMode = SleepTimerMode.Chapters;
            _sleepDeadlineUtc = null;
            _sleepRemainingWhenPaused = TimeSpan.Zero;
            _sleepTargetPosition = target;
            _sleepChaptersRemaining = chapterCount;
            _sleepLastChapterIndex = currentIndex;
            EnsureSleepTimerRunning();
            StateChanged?.Invoke();
        }

        public void CancelSleepTimer(bool raiseState = true)
        {
            _sleepMode = SleepTimerMode.Off;
            _sleepDeadlineUtc = null;
            _sleepRemainingWhenPaused = TimeSpan.Zero;
            _sleepTargetPosition = null;
            _sleepChaptersRemaining = 0;
            _sleepLastChapterIndex = -1;
            _sleepTimer.Stop();

            if (raiseState)
                StateChanged?.Invoke();
        }

        public string GetSleepTimerButtonLabel()
        {
            if (_sleepMode == SleepTimerMode.Off)
                return "Sleep";

            if (_sleepMode == SleepTimerMode.Minutes)
            {
                var remaining = GetSleepMinutesRemaining();
                if (remaining <= TimeSpan.Zero)
                    return "Sleep";

                return remaining.TotalHours >= 1
                    ? $"Sleep {remaining:h\\:mm\\:ss}"
                    : $"Sleep {remaining:mm\\:ss}";
            }

            if (_sleepMode == SleepTimerMode.EndOfChapter)
                return "Sleep · End ch";

            if (_sleepMode == SleepTimerMode.Chapters)
                return _sleepChaptersRemaining > 0
                    ? $"Sleep · {_sleepChaptersRemaining} ch"
                    : "Sleep · Ch";

            return "Sleep";
        }

        public TimeSpan GetSleepMinutesRemaining()
        {
            if (_sleepMode != SleepTimerMode.Minutes)
                return TimeSpan.Zero;

            if (_sleepDeadlineUtc is DateTimeOffset deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }

            return _sleepRemainingWhenPaused > TimeSpan.Zero
                ? _sleepRemainingWhenPaused
                : TimeSpan.Zero;
        }

        private void ArmPositionSleep(SleepTimerMode mode, TimeSpan targetPosition)
        {
            _sleepMode = mode;
            _sleepDeadlineUtc = null;
            _sleepRemainingWhenPaused = TimeSpan.Zero;
            _sleepTargetPosition = targetPosition;
            _sleepChaptersRemaining = 0;
            _sleepLastChapterIndex = HasChapters ? FindChapterIndex(GetAbsolutePosition()) : -1;
            EnsureSleepTimerRunning();
            StateChanged?.Invoke();
        }

        private TimeSpan GetRemainingBookTime()
        {
            var total = TotalDuration;
            if (total <= TimeSpan.Zero)
                return TimeSpan.Zero;

            var remaining = total - GetAbsolutePosition();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        private void EnsureSleepTimerRunning()
        {
            if (_sleepMode == SleepTimerMode.Off)
            {
                _sleepTimer.Stop();
                return;
            }

            if (!_sleepTimer.IsRunning)
                _sleepTimer.Start();
        }

        private void TickSleepTimer()
        {
            if (_sleepMode == SleepTimerMode.Off)
            {
                _sleepTimer.Stop();
                return;
            }

            if (_sleepMode == SleepTimerMode.Minutes)
            {
                if (!IsPlaying)
                {
                    StateChanged?.Invoke();
                    return;
                }

                if (GetSleepMinutesRemaining() <= TimeSpan.Zero)
                {
                    TriggerSleepStop();
                    return;
                }

                StateChanged?.Invoke();
                return;
            }

            if (_sleepTargetPosition is not TimeSpan target)
                return;

            var position = GetAbsolutePosition();

            if (_sleepMode == SleepTimerMode.Chapters && HasChapters)
            {
                var index = FindChapterIndex(position);
                if (_sleepLastChapterIndex >= 0 && index > _sleepLastChapterIndex)
                {
                    var crossed = index - _sleepLastChapterIndex;
                    _sleepChaptersRemaining = Math.Max(0, _sleepChaptersRemaining - crossed);
                    _sleepLastChapterIndex = index;
                    StateChanged?.Invoke();
                }
            }

            if (position >= target - TimeSpan.FromMilliseconds(250)
                || (TotalDuration > TimeSpan.Zero && position >= TotalDuration - TimeSpan.FromMilliseconds(250)))
            {
                TriggerSleepStop();
            }
        }

        private void TriggerSleepStop()
        {
            CancelSleepTimer(raiseState: false);
            if (IsPlaying)
                Pause();
            else
                StateChanged?.Invoke();
        }

        private MediaPlayer EnsurePlayer()
        {
            if (_player != null)
                return _player;

            _player = new MediaPlayer
            {
                AutoPlay = false,
                Volume = _volumePercent / 100.0
            };

            _player.MediaEnded += Player_MediaEnded;
            _player.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;

            return _player;
        }

        private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            if (sender.PlaybackState == MediaPlaybackState.Paused && IsPlaying)
            {
                // External pause (e.g. end of list item) — sync flag if truly stopped at end.
            }
        }

        private void Player_MediaEnded(MediaPlayer sender, object args)
        {
            // Single-source books end here. Multi-file lists use CurrentItemChanged / last item end.
            if (_playbackList != null)
                return;

            RaiseBookFinishedIfNeeded();
        }

        private void RaiseBookFinishedIfNeeded()
        {
            if (_bookFinishedRaised || CurrentBook == null)
                return;

            var position = GetAbsolutePosition();
            var total = TotalDuration;
            if (total > TimeSpan.Zero && position < total - TimeSpan.FromSeconds(1.5))
                return;

            _bookFinishedRaised = true;
            IsPlaying = false;
            CancelSleepTimer(raiseState: false);

            if (total > TimeSpan.Zero)
                CurrentBook.LastPositionSeconds = total.TotalSeconds;

            PersistPlaybackState();
            var finished = CurrentBook;
            StateChanged?.Invoke();
            BookFinished?.Invoke(finished);
        }

        private void OnPlaybackListItemChanged(MediaPlaybackList sender, CurrentMediaPlaybackItemChangedEventArgs args)
        {
            // When moving past the last item, CurrentItem may become null / index wraps.
            if (_playbackList == null || CurrentBook == null)
                return;

            var index = (int)_playbackList.CurrentItemIndex;
            if (index < 0 || index >= _playbackList.Items.Count - 1)
            {
                // Last item finishing is handled via MediaPlayer.MediaEnded when list completes —
                // also check absolute position near end.
            }

            StateChanged?.Invoke();
        }

        private async Task PrepareTimelineAsync(Book book)
        {
            _partDurations.Clear();
            _partStarts.Clear();
            _totalDuration = TimeSpan.Zero;

            var paths = book.AudioFilePaths;
            foreach (var path in paths)
            {
                var duration = ProbeDuration(path);
                _partDurations.Add(duration);
            }

            TimeSpan cursor = TimeSpan.Zero;
            foreach (var duration in _partDurations)
            {
                _partStarts.Add(cursor);
                cursor += duration;
            }

            _totalDuration = cursor;
            if (_totalDuration > TimeSpan.Zero)
                book.Duration = _totalDuration;

            await Task.CompletedTask;
        }

        private static TimeSpan ProbeDuration(string path)
        {
            try
            {
                var track = new Track(path);
                if (track.DurationMs > 0)
                    return TimeSpan.FromMilliseconds(track.DurationMs);
            }
            catch
            {
            }

            return TimeSpan.Zero;
        }

        private async Task LoadSourceAsync(Book book)
        {
            _sourceLoaded = false;
            _pendingOpenBook = book;
            _playbackList = null;

            var paths = book.AudioFilePaths;
            if (paths.Count == 0)
                return;

            var player = EnsurePlayer();

            try
            {
                player.MediaOpened -= Player_MediaOpened;

                if (paths.Count == 1)
                {
                    player.Source = await CreateSourceAsync(paths[0]);
                }
                else
                {
                    var list = new MediaPlaybackList
                    {
                        AutoRepeatEnabled = false
                    };

                    foreach (var path in paths)
                    {
                        var source = await CreateSourceAsync(path);
                        list.Items.Add(new MediaPlaybackItem(source));
                    }

                    list.CurrentItemChanged += OnPlaybackListItemChanged;
                    _playbackList = list;
                    player.Source = list;
                }

                _sourceLoaded = true;

                player.Volume = _volumePercent / 100.0;
                player.IsMuted = _volumePercent <= 0;
                player.PlaybackSession.PlaybackRate = Speed;

                player.MediaOpened += Player_MediaOpened;
            }
            catch
            {
                _sourceLoaded = false;
                throw;
            }
        }

        private static async Task<MediaSource> CreateSourceAsync(string filePath)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                return MediaSource.CreateFromStorageFile(file);
            }
            catch
            {
                var path = filePath.Replace('\\', '/');
                if (!path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    path = "file:///" + path;
                return MediaSource.CreateFromUri(new Uri(path));
            }
        }

        private void Player_MediaOpened(MediaPlayer sender, object args)
        {
            sender.MediaOpened -= Player_MediaOpened;

            var book = _pendingOpenBook;
            if (book != null && book.LastPositionSeconds > 0)
                SeekTo(TimeSpan.FromSeconds(book.LastPositionSeconds));

            // For multi-file lists, watch the last item ending via position polling in sleep tick
            // and MediaEnded on the player when the list completes.
            sender.MediaEnded -= Player_MediaEnded;
            sender.MediaEnded += Player_MediaEnded;

            StateChanged?.Invoke();
        }

        private void LoadChapters(Book book)
        {
            _chapters.Clear();

            var paths = book.AudioFilePaths;
            if (paths.Count == 0)
                return;

            if (paths.Count > 1)
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    var start = i < _partStarts.Count ? _partStarts[i] : TimeSpan.Zero;
                    var end = i + 1 < _partStarts.Count
                        ? _partStarts[i + 1]
                        : (_totalDuration > start ? _totalDuration : start);

                    if (end <= start)
                        end = start + TimeSpan.FromSeconds(1);

                    var title = Path.GetFileNameWithoutExtension(paths[i]);
                    if (string.IsNullOrWhiteSpace(title))
                        title = $"Part {i + 1}";

                    _chapters.Add(new ChapterMarker(title, start, end));
                }

                return;
            }

            try
            {
                var track = new Track(paths[0]);
                if (track.Chapters == null || track.Chapters.Count == 0)
                    return;

                var durationMs = track.DurationMs > 0
                    ? track.DurationMs
                    : (long)book.Duration.TotalMilliseconds;

                var ordered = track.Chapters
                    .Where(c => !c.UseOffset)
                    .OrderBy(c => c.StartTime)
                    .ToList();

                for (int i = 0; i < ordered.Count; i++)
                {
                    var chapter = ordered[i];
                    var start = TimeSpan.FromMilliseconds(chapter.StartTime);

                    TimeSpan end;
                    if (chapter.EndTime > chapter.StartTime)
                        end = TimeSpan.FromMilliseconds(chapter.EndTime);
                    else if (i + 1 < ordered.Count)
                        end = TimeSpan.FromMilliseconds(ordered[i + 1].StartTime);
                    else if (durationMs > chapter.StartTime)
                        end = TimeSpan.FromMilliseconds(durationMs);
                    else
                        end = start;

                    if (end <= start)
                        continue;

                    var title = string.IsNullOrWhiteSpace(chapter.Title)
                        ? $"Chapter {i + 1}"
                        : chapter.Title.Trim();

                    _chapters.Add(new ChapterMarker(title, start, end));
                }
            }
            catch
            {
                _chapters.Clear();
            }
        }

        private static bool ReadAutoplayPreference()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                return values.TryGetValue(AutoplaySettingKey, out var value) && value is bool on && on;
            }
            catch
            {
                return false;
            }
        }

        private static void SaveAutoplayPreference(bool enabled)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[AutoplaySettingKey] = enabled;
            }
            catch
            {
            }
        }

        /// <summary>
        /// Called from UI timers to detect end-of-book for multi-file lists
        /// where MediaEnded may not fire reliably for the whole list.
        /// </summary>
        public void CheckForBookEnd()
        {
            if (!IsPlaying || CurrentBook == null || _bookFinishedRaised)
                return;

            var total = TotalDuration;
            if (total <= TimeSpan.Zero)
                return;

            if (GetAbsolutePosition() >= total - TimeSpan.FromMilliseconds(400))
                RaiseBookFinishedIfNeeded();
        }
    }
}
