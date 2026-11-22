using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using AudiobookApp.Models;
using AudiobookApp.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AudiobookApp.Views
{
    public sealed partial class MiniPlayerBar : UserControl
    {
        private PlaybackService? _playback;
        private Func<Book?, string?>? _seriesLabelProvider;
        private readonly DispatcherTimer _timer = new();
        private bool _isDraggingSlider;
        private bool _updatingFromTimer;
        private bool _updatingFromService;
        private int _currentChapterIndex = -1;

        public event Action? ExpandRequested;
        public event Action? CollapseRequested;
        public event Action? CloseRequested;

        public MiniPlayerBar()
        {
            InitializeComponent();

            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            Unloaded += (_, _) =>
            {
                if (_playback != null)
                    _playback.StateChanged -= Playback_StateChanged;
            };
        }

        public void Attach(PlaybackService playback, Func<Book?, string?>? seriesLabelProvider = null)
        {
            if (_playback != null)
                _playback.StateChanged -= Playback_StateChanged;

            _playback = playback;
            _seriesLabelProvider = seriesLabelProvider;
            _playback.StateChanged += Playback_StateChanged;
            SyncFromService();
            _ = LoadCoverAsync();
        }

        public void RefreshSeriesLabel()
        {
            if (_playback?.CurrentBook == null)
                return;

            var label = _seriesLabelProvider?.Invoke(_playback.CurrentBook);
            SetSeriesContext(!string.IsNullOrWhiteSpace(label), label);
        }

        private void SetSeriesContext(bool isInSeries, string? label)
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
            if (_playback != null)
                AutoplayButton.IsChecked = _playback.SeriesAutoplayEnabled;
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
            PlayPauseIcon.Glyph = _playback.IsPlaying ? "\uE769" : "\uE768";

            if (Math.Abs(SpeedSlider.Value - _playback.Speed) > 0.01)
                SpeedSlider.Value = _playback.Speed;

            if (Math.Abs(VolumeSlider.Value - _playback.VolumePercent) > 0.1)
                VolumeSlider.Value = _playback.VolumePercent;

            UpdateControlLabels();

            var seriesLabel = _seriesLabelProvider?.Invoke(_playback.CurrentBook);
            SetSeriesContext(!string.IsNullOrWhiteSpace(seriesLabel), seriesLabel);

            _updatingFromService = false;
            UpdateTimeDisplay();
        }

        private void UpdateControlLabels()
        {
            if (_playback == null)
                return;

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
            var book = _playback?.CurrentBook;
            if (book == null || string.IsNullOrWhiteSpace(book.EffectiveCoverPath))
                return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(book.EffectiveCoverPath);
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

        private void Timer_Tick(object? sender, object e)
        {
            _playback?.CheckForBookEnd();
            UpdateTimeDisplay();
            if (_playback?.IsSleepTimerActive == true)
                ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private void UpdateTimeDisplay()
        {
            if (_playback?.CurrentBook == null || ProgressSlider == null)
                return;

            var absolute = _isDraggingSlider
                ? GetAbsoluteFromSlider()
                : _playback.GetAbsolutePosition();

            if (_playback.TotalDuration <= TimeSpan.Zero &&
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
                var total = _playback.TotalDuration > TimeSpan.Zero
                    ? _playback.TotalDuration
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

        private void CollapseButton_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

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

        private void SpeedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playback == null)
                return;

            _updatingFromService = true;
            SpeedSlider.Value = _playback.Speed;
            SpeedFlyoutValueText.Text = $"{_playback.Speed:0.##}x";
            _updatingFromService = false;
        }

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playback == null)
                return;

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
            if (_playback != null)
                ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private void SleepPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_playback == null)
                return;

            if (sender is not MenuFlyoutItem { Tag: string tag } || !int.TryParse(tag, out var minutes))
                return;

            _playback.StartSleepTimerMinutes(minutes);
            ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private void SleepEndChapter_Click(object sender, RoutedEventArgs e)
        {
            if (_playback == null)
                return;

            _playback.StartSleepTimerEndOfChapter();
            ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private async void SleepCustom_Click(object sender, RoutedEventArgs e)
        {
            if (_playback == null || XamlRoot == null)
                return;

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
            if (_playback == null)
                return;

            _playback.CancelSleepTimer();
            ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private void AutoplayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playback == null)
                return;

            _playback.SeriesAutoplayEnabled = AutoplayButton.IsChecked == true;
        }

        private static string FormatTime(TimeSpan time)
        {
            return time.TotalHours >= 1
                ? time.ToString(@"hh\:mm\:ss")
                : time.ToString(@"mm\:ss");
        }
    }
}
