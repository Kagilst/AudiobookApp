using System;
using System.Linq;
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
        private readonly Func<Book?, string?>? _seriesLabelProvider;

        private bool _isDraggingSlider;
        private bool _updatingFromTimer;
        private bool _updatingFromService;
        private int _currentChapterIndex = -1;

        public event Action? ShrinkRequested;
        public event Action? ShrinkToMiniRequested;

        public PlayerPage(PlaybackService playback, Func<Book?, string?>? seriesLabelProvider = null)
        {
            _playback = playback;
            _seriesLabelProvider = seriesLabelProvider;
            _updatingFromService = true;
            InitializeComponent();

            SpeedSlider.Value = _playback.Speed;
            VolumeSlider.Value = _playback.VolumePercent;
            UpdateControlLabels();
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

        public void SetSeriesContext(bool isInSeries, string? label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                SeriesText.Visibility = Visibility.Collapsed;
                SeriesText.Text = string.Empty;
            }
            else
            {
                SeriesText.Text = label;
                SeriesText.Visibility = Visibility.Visible;
            }

            AutoplayButton.Visibility = isInSeries ? Visibility.Visible : Visibility.Collapsed;
            AutoplayButton.IsChecked = _playback.SeriesAutoplayEnabled;
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
            PlayPauseIcon.Glyph = _playback.IsPlaying ? "\uE769" : "\uE768";

            if (Math.Abs(SpeedSlider.Value - _playback.Speed) > 0.01)
                SpeedSlider.Value = _playback.Speed;

            if (Math.Abs(VolumeSlider.Value - _playback.VolumePercent) > 0.1)
                VolumeSlider.Value = _playback.VolumePercent;

            UpdateControlLabels();

            var seriesLabel = _seriesLabelProvider?.Invoke(book);
            SetSeriesContext(!string.IsNullOrWhiteSpace(seriesLabel), seriesLabel);

            if (_playback.HasChapters)
            {
                ChapterText.Visibility = Visibility.Visible;
                var index = _playback.FindChapterIndex(_playback.GetAbsolutePosition());
                UpdateChapterUi(index, force: true);
            }
            else
            {
                ChapterText.Visibility = Visibility.Collapsed;
            }

            _updatingFromService = false;
        }

        private void UpdateControlLabels()
        {
            SpeedFlyoutValueText.Text = $"{_playback.Speed:0.##}x";
            VolumeFlyoutValueText.Text = $"{(int)Math.Round(_playback.VolumePercent)}%";
            ToolTipService.SetToolTip(SpeedButton, $"Speed {_playback.Speed:0.##}x");
            ToolTipService.SetToolTip(VolumeButton, $"Volume {(int)Math.Round(_playback.VolumePercent)}%");
            ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
            VolumeIcon.Glyph = _playback.VolumePercent <= 0.5 ? "\uE74F" : "\uE767";
            AutoplayButton.IsChecked = _playback.SeriesAutoplayEnabled;
        }

        private async System.Threading.Tasks.Task LoadCoverAsync()
        {
            var book = _playback.CurrentBook;
            if (book == null || string.IsNullOrWhiteSpace(book.EffectiveCoverPath))
                return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(book.EffectiveCoverPath);
                var bitmap = new BitmapImage();

                // Custom image files decode directly; audio files use the shell thumbnail (embedded art).
                if (!string.IsNullOrWhiteSpace(book.CoverImagePath))
                {
                    using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
                    await bitmap.SetSourceAsync(stream);
                }
                else
                {
                    var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 400);
                    if (thumbnail == null)
                        return;

                    await bitmap.SetSourceAsync(thumbnail);
                }

                CoverImage.Source = bitmap;
                CoverPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Keep placeholder.
            }
        }

        private void ApplyInitialTimeDisplay() => UpdateTimeDisplay(forceChapter: true);

        private void Timer_Tick(object? sender, object e)
        {
            _playback.CheckForBookEnd();
            UpdateTimeDisplay();
            if (_playback.IsSleepTimerActive)
                ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private void UpdateTimeDisplay(bool forceChapter = false)
        {
            var book = _playback.CurrentBook;
            if (book == null)
                return;

            var absolute = _isDraggingSlider
                ? GetAbsolutePositionFromSlider()
                : _playback.GetAbsolutePosition();

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

            var total = _playback.TotalDuration > TimeSpan.Zero
                ? _playback.TotalDuration
                : book.Duration;

            if (total <= TimeSpan.Zero)
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

        private void SpeedButton_Click(object sender, RoutedEventArgs e)
        {
            _updatingFromService = true;
            SpeedSlider.Value = _playback.Speed;
            SpeedFlyoutValueText.Text = $"{_playback.Speed:0.##}x";
            _updatingFromService = false;
        }

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            _updatingFromService = true;
            VolumeSlider.Value = _playback.VolumePercent;
            VolumeFlyoutValueText.Text = $"{(int)Math.Round(_playback.VolumePercent)}%";
            _updatingFromService = false;
        }

        private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (SpeedFlyoutValueText != null)
                SpeedFlyoutValueText.Text = $"{e.NewValue:0.##}x";

            if (_updatingFromService || _playback == null)
                return;

            _playback.SetSpeed(e.NewValue);
            ToolTipService.SetToolTip(SpeedButton, $"Speed {_playback.Speed:0.##}x");
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (VolumeFlyoutValueText != null)
                VolumeFlyoutValueText.Text = $"{(int)Math.Round(e.NewValue)}%";

            if (_updatingFromService || _playback == null)
                return;

            _playback.VolumePercent = e.NewValue;
            ToolTipService.SetToolTip(VolumeButton, $"Volume {(int)Math.Round(e.NewValue)}%");
            VolumeIcon.Glyph = e.NewValue <= 0.5 ? "\uE74F" : "\uE767";
        }

        private void SleepButton_Click(object sender, RoutedEventArgs e)
        {
            ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private void SleepPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem { Tag: string tag } || !int.TryParse(tag, out var minutes))
                return;

            _playback.StartSleepTimerMinutes(minutes);
            ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private void SleepEndChapter_Click(object sender, RoutedEventArgs e)
        {
            _playback.StartSleepTimerEndOfChapter();
            ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private async void SleepCustom_Click(object sender, RoutedEventArgs e)
        {
            var minutesBox = CreateDigitsOnlyBox("Minutes", "20");
            var chaptersBox = CreateDigitsOnlyBox("Chapters", "1");
            chaptersBox.Visibility = Visibility.Collapsed;

            var modeCombo = new ComboBox
            {
                Header = "Custom type",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            modeCombo.Items.Add("Minutes");
            modeCombo.Items.Add("Chapters");
            modeCombo.SelectedIndex = 0;
            modeCombo.SelectionChanged += (_, _) =>
            {
                var useChapters = modeCombo.SelectedIndex == 1;
                minutesBox.Visibility = useChapters ? Visibility.Collapsed : Visibility.Visible;
                chaptersBox.Visibility = useChapters ? Visibility.Visible : Visibility.Collapsed;
            };

            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(modeCombo);
            panel.Children.Add(minutesBox);
            panel.Children.Add(chaptersBox);

            var dialog = new ContentDialog
            {
                Title = "Custom sleep timer",
                PrimaryButtonText = "Start",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = panel,
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            if (modeCombo.SelectedIndex == 1)
            {
                if (!TryParsePositiveInt(chaptersBox.Text, out var chapters))
                    return;

                _playback.StartSleepTimerChapters(Math.Clamp(chapters, 1, 500));
            }
            else
            {
                if (!TryParsePositiveInt(minutesBox.Text, out var minutes))
                    return;

                _playback.StartSleepTimerMinutes(Math.Clamp(minutes, 1, 24 * 60));
            }

            ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private static TextBox CreateDigitsOnlyBox(string header, string initial)
        {
            var box = new TextBox
            {
                Header = header,
                Text = initial,
                InputScope = new InputScope
                {
                    Names = { new InputScopeName(InputScopeNameValue.Number) }
                }
            };

            box.BeforeTextChanging += (_, args) =>
            {
                args.Cancel = args.NewText.Length > 0 && !args.NewText.All(char.IsDigit);
            };

            return box;
        }

        private static bool TryParsePositiveInt(string? text, out int value)
        {
            value = 0;
            return !string.IsNullOrWhiteSpace(text) && int.TryParse(text.Trim(), out value) && value > 0;
        }

        private void SleepCancel_Click(object sender, RoutedEventArgs e)
        {
            _playback.CancelSleepTimer();
            ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private void AutoplayButton_Click(object sender, RoutedEventArgs e)
        {
            _playback.SeriesAutoplayEnabled = AutoplayButton.IsChecked == true;
        }

        private void ShrinkButton_Click(object sender, RoutedEventArgs e) =>
            ShrinkRequested?.Invoke();

        private void ShrinkToMiniButton_Click(object sender, RoutedEventArgs e) =>
            ShrinkToMiniRequested?.Invoke();

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
