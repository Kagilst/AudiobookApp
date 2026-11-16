using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using AudiobookApp.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AudiobookApp.Views
{
    public sealed partial class MiniPlayerBar : UserControl
    {
        private PlaybackService? _playback;
        private readonly DispatcherTimer _timer = new();
        private bool _isDraggingSlider;
        private bool _updatingFromTimer;
        private bool _updatingFromService;
        private int _currentChapterIndex = -1;

        public event Action? ExpandRequested;

        public MiniPlayerBar()
        {
            InitializeComponent();
            PlaybackSpeedBox.SelectedIndex = 2;

            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            Unloaded += (_, _) =>
            {
                if (_playback != null)
                    _playback.StateChanged -= Playback_StateChanged;
            };
        }

        public void Attach(PlaybackService playback)
        {
            if (_playback != null)
                _playback.StateChanged -= Playback_StateChanged;

            _playback = playback;
            _playback.StateChanged += Playback_StateChanged;
            SyncFromService();
            _ = LoadCoverAsync();
        }

        private void Playback_StateChanged()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SyncFromService();
                _ = LoadCoverAsync();
            });
        }

        private void SyncFromService()
        {
            if (_playback?.CurrentBook == null)
                return;

            _updatingFromService = true;

            TitleText.Text = _playback.CurrentBook.Title;
            PlayPauseIcon.Symbol = _playback.IsPlaying ? Symbol.Pause : Symbol.Play;

            if (PlaybackSpeedBox.SelectedIndex != _playback.SpeedIndex)
                PlaybackSpeedBox.SelectedIndex = _playback.SpeedIndex;

            if (Math.Abs(VolumeSlider.Value - _playback.VolumePercent) > 0.1)
                VolumeSlider.Value = _playback.VolumePercent;

            VolumeValueText.Text = ((int)_playback.VolumePercent).ToString();

            _updatingFromService = false;
            UpdateTimeDisplay();
        }

        private async System.Threading.Tasks.Task LoadCoverAsync()
        {
            var book = _playback?.CurrentBook;
            if (book == null || string.IsNullOrWhiteSpace(book.FilePath))
                return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(book.FilePath);
                var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 120);
                if (thumbnail == null)
                    return;

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                CoverImage.Source = bitmap;
                CoverPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch
            {
                CoverImage.Source = null;
                CoverPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private void Timer_Tick(object? sender, object e) => UpdateTimeDisplay();

        private void UpdateTimeDisplay()
        {
            if (_playback?.CurrentBook == null || ProgressSlider == null)
                return;

            var session = _playback.Player.PlaybackSession;
            var absolute = _isDraggingSlider
                ? GetAbsoluteFromSlider()
                : session.Position;

            if (session.NaturalDuration <= TimeSpan.Zero &&
                _playback.CurrentBook.Duration <= TimeSpan.Zero &&
                !_playback.HasChapters)
            {
                return;
            }

            if (_playback.HasChapters)
            {
                var index = _playback.FindChapterIndex(absolute);
                if (index != _currentChapterIndex)
                {
                    _currentChapterIndex = index;
                    var chapter = _playback.Chapters[index];
                    ChapterText.Text = $"{chapter.Title} ({index + 1}/{_playback.Chapters.Count})";
                    ChapterText.Visibility = Visibility.Visible;
                }

                var chapterInfo = _playback.Chapters[_currentChapterIndex < 0 ? 0 : _currentChapterIndex];
                var chapterPos = absolute - chapterInfo.Start;
                if (chapterPos < TimeSpan.Zero)
                    chapterPos = TimeSpan.Zero;

                _updatingFromTimer = true;
                ProgressSlider.Maximum = Math.Max(chapterInfo.Duration.TotalSeconds, 0.1);
                if (!_isDraggingSlider)
                {
                    ProgressSlider.Value = Math.Clamp(
                        chapterPos.TotalSeconds,
                        0,
                        ProgressSlider.Maximum);
                }
                _updatingFromTimer = false;

                CurrentTimeText.Text = FormatTime(_isDraggingSlider
                    ? TimeSpan.FromSeconds(ProgressSlider.Value)
                    : chapterPos);
                TotalTimeText.Text = FormatTime(chapterInfo.Duration);
            }
            else
            {
                ChapterText.Visibility = Visibility.Collapsed;
                var total = session.NaturalDuration > TimeSpan.Zero
                    ? session.NaturalDuration
                    : _playback.CurrentBook.Duration;

                _updatingFromTimer = true;
                ProgressSlider.Maximum = total > TimeSpan.Zero ? total.TotalSeconds : 1;
                if (!_isDraggingSlider)
                {
                    ProgressSlider.Value = Math.Clamp(absolute.TotalSeconds, 0, ProgressSlider.Maximum);
                }
                _updatingFromTimer = false;

                CurrentTimeText.Text = FormatTime(_isDraggingSlider
                    ? TimeSpan.FromSeconds(ProgressSlider.Value)
                    : absolute);
                TotalTimeText.Text = total > TimeSpan.Zero ? FormatTime(total) : "--:--";
            }
        }

        private TimeSpan GetAbsoluteFromSlider()
        {
            if (_playback == null)
                return TimeSpan.Zero;

            if (_playback.HasChapters)
            {
                var index = _currentChapterIndex >= 0 ? _currentChapterIndex : 0;
                return _playback.Chapters[index].Start + TimeSpan.FromSeconds(ProgressSlider.Value);
            }

            return TimeSpan.FromSeconds(ProgressSlider.Value);
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _playback?.TogglePlayPause();

        private void RewindButton_Click(object sender, RoutedEventArgs e) => _playback?.SeekBySeconds(-10);

        private void FastForwardButton_Click(object sender, RoutedEventArgs e) => _playback?.SeekBySeconds(10);

        private void PreviousButton_Click(object sender, RoutedEventArgs e) => _playback?.PreviousChapter();

        private void NextButton_Click(object sender, RoutedEventArgs e) => _playback?.NextChapter();

        private void ExpandButton_Click(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke();

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _isDraggingSlider = true;

        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingSlider = false;
            _playback?.SeekTo(GetAbsoluteFromSlider());
        }

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (CurrentTimeText != null)
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));

            if (_isDraggingSlider || _updatingFromTimer || _updatingFromService)
                return;

            _playback?.SeekTo(GetAbsoluteFromSlider());
        }

        private void PlaybackSpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingFromService || _playback == null)
                return;

            _playback.SetSpeedIndex(PlaybackSpeedBox.SelectedIndex);
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (VolumeValueText != null)
                VolumeValueText.Text = ((int)Math.Round(e.NewValue)).ToString();

            if (_updatingFromService || _playback == null)
                return;

            _playback.VolumePercent = e.NewValue;
        }

        private static string FormatTime(TimeSpan time)
        {
            return time.TotalHours >= 1
                ? time.ToString(@"hh\:mm\:ss")
                : time.ToString(@"mm\:ss");
        }
    }
}
