using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using AudiobookApp.Models;
using AudiobookApp.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AudiobookApp.Views
{
    public sealed partial class PlayerPage : Page
    {
        private readonly PlaybackService _playback;
        private readonly DispatcherTimer _timer = new();

        private bool _isDraggingSlider;
        private bool _updatingFromTimer;
        private bool _updatingFromService;
        private int _currentChapterIndex = -1;

        public PlayerPage(PlaybackService playback)
        {
            // Must assign before InitializeComponent: volume/speed controls fire
            // ValueChanged/SelectionChanged while XAML loads.
            _playback = playback;
            _updatingFromService = true;
            InitializeComponent();

            PlaybackSpeedBox.SelectedIndex = _playback.SpeedIndex;
            VolumeSlider.Value = _playback.VolumePercent;
            VolumeValueText.Text = ((int)_playback.VolumePercent).ToString();
            _updatingFromService = false;

            Loaded += async (_, _) =>
            {
                await LoadCoverAsync();
                SyncFromService();
                ApplyInitialTimeDisplay();
            };

            Unloaded += (_, _) =>
            {
                _timer.Stop();
                _playback.StateChanged -= Playback_StateChanged;
                _playback.PersistPlaybackState();
            };

            _playback.StateChanged += Playback_StateChanged;

            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Playback_StateChanged()
        {
            DispatcherQueue.TryEnqueue(SyncFromService);
        }

        private void SyncFromService()
        {
            var book = _playback.CurrentBook;
            if (book == null)
                return;

            _updatingFromService = true;

            TitleText.Text = book.Title;
            AuthorText.Text = book.Author;
            PlayPauseIcon.Symbol = _playback.IsPlaying ? Symbol.Pause : Symbol.Play;

            if (PlaybackSpeedBox.SelectedIndex != _playback.SpeedIndex)
                PlaybackSpeedBox.SelectedIndex = _playback.SpeedIndex;

            if (Math.Abs(VolumeSlider.Value - _playback.VolumePercent) > 0.1)
                VolumeSlider.Value = _playback.VolumePercent;

            VolumeValueText.Text = ((int)_playback.VolumePercent).ToString();

            if (_playback.HasChapters)
            {
                ChapterText.Visibility = Visibility.Visible;
                var index = _playback.FindChapterIndex(_playback.Player.PlaybackSession.Position);
                UpdateChapterUi(index, force: true);
            }
            else
            {
                ChapterText.Visibility = Visibility.Collapsed;
            }

            _updatingFromService = false;
        }

        private async System.Threading.Tasks.Task LoadCoverAsync()
        {
            var book = _playback.CurrentBook;
            if (book == null || string.IsNullOrWhiteSpace(book.FilePath))
                return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(book.FilePath);
                var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 300);
                if (thumbnail == null)
                    return;

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                CoverImage.Source = bitmap;
                CoverPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Keep placeholder.
            }
        }

        private void ApplyInitialTimeDisplay() => UpdateTimeDisplay(forceChapter: true);

        private void Timer_Tick(object? sender, object e) => UpdateTimeDisplay();

        private void UpdateTimeDisplay(bool forceChapter = false)
        {
            var book = _playback.CurrentBook;
            if (book == null)
                return;

            var session = _playback.Player.PlaybackSession;
            var absolute = _isDraggingSlider
                ? GetAbsolutePositionFromSlider()
                : session.Position;

            if (_playback.HasChapters)
            {
                var chapterIndex = _playback.FindChapterIndex(absolute);
                UpdateChapterUi(chapterIndex, forceChapter);

                var chapter = _playback.Chapters[chapterIndex];
                var chapterPosition = absolute - chapter.Start;
                if (chapterPosition < TimeSpan.Zero)
                    chapterPosition = TimeSpan.Zero;

                _updatingFromTimer = true;
                ProgressSlider.Maximum = Math.Max(chapter.Duration.TotalSeconds, 0.1);
                if (!_isDraggingSlider)
                {
                    ProgressSlider.Value = Math.Clamp(
                        chapterPosition.TotalSeconds,
                        0,
                        ProgressSlider.Maximum);
                }
                _updatingFromTimer = false;

                CurrentTimeText.Text = FormatTime(_isDraggingSlider
                    ? TimeSpan.FromSeconds(ProgressSlider.Value)
                    : chapterPosition);
                TotalTimeText.Text = FormatTime(chapter.Duration);
                return;
            }

            var total = session.NaturalDuration > TimeSpan.Zero
                ? session.NaturalDuration
                : book.Duration;

            if (total <= TimeSpan.Zero && session.NaturalDuration <= TimeSpan.Zero)
                return;

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

        private void UpdateChapterUi(int chapterIndex, bool force = false)
        {
            if (!_playback.HasChapters || chapterIndex < 0 || chapterIndex >= _playback.Chapters.Count)
                return;

            if (!force && chapterIndex == _currentChapterIndex)
                return;

            _currentChapterIndex = chapterIndex;
            var chapter = _playback.Chapters[chapterIndex];
            ChapterText.Text = $"{chapter.Title}  ({chapterIndex + 1}/{_playback.Chapters.Count})";
            ChapterText.Visibility = Visibility.Visible;
        }

        private TimeSpan GetAbsolutePositionFromSlider()
        {
            if (_playback.HasChapters)
            {
                var index = _currentChapterIndex >= 0 ? _currentChapterIndex : 0;
                return _playback.Chapters[index].Start + TimeSpan.FromSeconds(ProgressSlider.Value);
            }

            return TimeSpan.FromSeconds(ProgressSlider.Value);
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _playback.TogglePlayPause();

        private void RewindButton_Click(object sender, RoutedEventArgs e) => _playback.SeekBySeconds(-10);

        private void FastForwardButton_Click(object sender, RoutedEventArgs e) => _playback.SeekBySeconds(10);

        private void PreviousButton_Click(object sender, RoutedEventArgs e) => _playback.PreviousChapter();

        private void NextButton_Click(object sender, RoutedEventArgs e) => _playback.NextChapter();

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _isDraggingSlider = true;

        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingSlider = false;
            _playback.SeekTo(GetAbsolutePositionFromSlider());
        }

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (CurrentTimeText != null)
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));

            if (_isDraggingSlider || _updatingFromTimer || _updatingFromService)
                return;

            _playback.SeekTo(GetAbsolutePositionFromSlider());
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

    public class SecondsToTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            double seconds = value is double d ? d : 0;
            TimeSpan time = TimeSpan.FromSeconds(seconds);
            return time.TotalHours >= 1
                ? time.ToString(@"hh\:mm\:ss")
                : time.ToString(@"mm\:ss");
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
