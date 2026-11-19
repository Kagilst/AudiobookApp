using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ATL;
using AudiobookApp.Models;
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

    public sealed class PlaybackService
    {
        private static readonly double[] SpeedOptions =
        {
            0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 3.5, 4.0
        };

        private readonly List<ChapterMarker> _chapters = new();
        private readonly List<TimeSpan> _partDurations = new();
        private readonly List<TimeSpan> _partStarts = new();
        private MediaPlayer? _player;
        private MediaPlaybackList? _playbackList;
        private bool _sourceLoaded;
        private double _volumePercent = 100;
        private Book? _pendingOpenBook;
        private TimeSpan _totalDuration;

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

        public int SpeedIndex { get; private set; } = 2;

        public double Speed => SpeedOptions[Math.Clamp(SpeedIndex, 0, SpeedOptions.Length - 1)];

        public event Action? StateChanged;
        public event Action<Book>? PlaybackPersisted;

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

            var player = EnsurePlayer();
            CurrentBook = book;
            IsPlaying = false;
            player.Pause();

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

            Player.Play();
            IsPlaying = true;
            StateChanged?.Invoke();
        }

        public void Pause()
        {
            if (_player == null)
                return;

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
            if (next == SpeedIndex && _player != null)
            {
                _player.PlaybackSession.PlaybackRate = Speed;
                return;
            }

            SpeedIndex = next;

            if (_player != null)
                _player.PlaybackSession.PlaybackRate = Speed;

            if (CurrentBook != null)
                CurrentBook.PlaybackSpeed = Speed;

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

        private MediaPlayer EnsurePlayer()
        {
            if (_player != null)
                return _player;

            _player = new MediaPlayer
            {
                AutoPlay = false,
                Volume = _volumePercent / 100.0
            };

            return _player;
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

                    // If duration probe failed, give each unknown part a tiny span so navigation still works.
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
    }
}
