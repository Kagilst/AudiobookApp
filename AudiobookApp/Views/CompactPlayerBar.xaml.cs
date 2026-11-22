using System;
using System.Linq;
using AudiobookApp.Models;
using AudiobookApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AudiobookApp.Views
{
    public sealed partial class CompactPlayerBar : UserControl
    {
        private PlaybackService? _playback;
        private Func<Book?, (string? SeriesName, string? BookCount)?>? _seriesInfoProvider;
        private readonly DispatcherTimer _timer = new();
        private bool _pinned;
        private bool _updatingFromService;
        private bool _isDraggingSeek;
        private int _currentChapterIndex = -1;
        private double _seekMaximumSeconds = 1;

        public event Action? ExpandRequested;
        public event Action<bool>? PinChanged;

        public UIElement DragHandle => DragRegion;

        /// <summary>Measured content size after layout (DIP).</summary>
        public Windows.Foundation.Size GetContentSize()
        {
            // Clear any previously locked size so measure reflects true content height.
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            UpdateLayout();

            RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = RootGrid.DesiredSize;
            RootGrid.Arrange(new Windows.Foundation.Rect(0, 0, desired.Width, desired.Height));
            UpdateLayout();

            // Prefer the bottom of the seek track so leftover stretch height is discarded.
            double width = desired.Width;
            double height = desired.Height;
            if (SeekTrack.ActualHeight > 0)
            {
                var point = SeekTrack.TransformToVisual(RootGrid)
                    .TransformPoint(new Windows.Foundation.Point(0, SeekTrack.ActualHeight));
                // Include border + bottom padding; BorderThickness is outside the padding box.
                height = point.Y + RootGrid.Padding.Bottom + RootGrid.BorderThickness.Bottom;
                width = Math.Max(desired.Width, RootGrid.ActualWidth);
            }

            return new Windows.Foundation.Size(
                Math.Ceiling(Math.Max(width, 1)),
                Math.Ceiling(Math.Max(height, 1)));
        }

        public bool IsPinned
        {
            get => _pinned;
            set
            {
                if (_pinned == value)
                    return;

                _pinned = value;
                PinButton.IsChecked = value;
                PinIcon.Glyph = value ? "\uE77A" : "\uE718";
                // Disable the drag handle while pinned without calling SetTitleBar(null).
                DragRegion.IsHitTestVisible = !value;
            }
        }

        public CompactPlayerBar()
        {
            InitializeComponent();

            _timer.Interval = TimeSpan.FromMilliseconds(400);
            _timer.Tick += Timer_Tick;

            Loaded += (_, _) =>
            {
                SeekTrack.SizeChanged += (_, _) => UpdateProgressSlider();
                UpdateProgressSlider();
            };

            Unloaded += (_, _) =>
            {
                _timer.Stop();
                if (_playback != null)
                    _playback.StateChanged -= Playback_StateChanged;
            };
        }

        public void Attach(
            PlaybackService playback,
            Func<Book?, (string? SeriesName, string? BookCount)?>? seriesInfoProvider = null)
        {
            if (_playback != null)
                _playback.StateChanged -= Playback_StateChanged;

            _playback = playback;
            _seriesInfoProvider = seriesInfoProvider;
            _playback.StateChanged += Playback_StateChanged;
            _timer.Start();
            SyncFromService();
            _ = LoadCoverAsync();
        }

        public void RefreshSeriesLabel()
        {
            if (_playback?.CurrentBook == null)
                return;

            UpdateMetadataTexts();
        }

        private void Playback_StateChanged()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SyncFromService();
                _ = LoadCoverAsync();
            });
        }

        private void Timer_Tick(object? sender, object e)
        {
            UpdateChapterText();
            UpdateProgressSlider();
            if (_playback?.IsSleepTimerActive == true)
                UpdateSleepTooltip();
        }

        private void SyncFromService()
        {
            if (_playback?.CurrentBook == null)
            {
                SeriesLineText.Text = string.Empty;
                SeriesLineText.Visibility = Visibility.Collapsed;
                TitleText.Text = "No book";
                AuthorText.Text = string.Empty;
                ChapterText.Text = string.Empty;
                return;
            }

            _updatingFromService = true;

            PlayPauseIcon.Glyph = _playback.IsPlaying ? "\uE769" : "\uE768"; // Pause / Play

            if (Math.Abs(SpeedSlider.Value - _playback.Speed) > 0.01)
                SpeedSlider.Value = _playback.Speed;

            if (Math.Abs(VolumeSlider.Value - _playback.VolumePercent) > 0.1)
                VolumeSlider.Value = _playback.VolumePercent;

            UpdateControlLabels();
            UpdateMetadataTexts();
            UpdateChapterText();
            UpdateProgressSlider();

            _updatingFromService = false;
        }

        private void UpdateMetadataTexts()
        {
            var book = _playback?.CurrentBook;
            if (book == null)
                return;

            var seriesInfo = _seriesInfoProvider?.Invoke(book);
            var inSeries = seriesInfo != null &&
                           (!string.IsNullOrWhiteSpace(seriesInfo.Value.SeriesName) ||
                            !string.IsNullOrWhiteSpace(seriesInfo.Value.BookCount));

            if (inSeries)
            {
                var seriesName = seriesInfo!.Value.SeriesName?.Trim();
                var bookCount = seriesInfo.Value.BookCount?.Trim();
                SeriesLineText.Text = !string.IsNullOrWhiteSpace(seriesName) && !string.IsNullOrWhiteSpace(bookCount)
                    ? $"{seriesName} · {bookCount}"
                    : seriesName ?? bookCount ?? string.Empty;
            }
            else
            {
                SeriesLineText.Text = string.Empty;
            }

            SeriesLineText.Visibility = string.IsNullOrWhiteSpace(SeriesLineText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            TitleText.Text = book.Title;
            AuthorText.Text = string.IsNullOrWhiteSpace(book.Author) ? "Unknown Author" : book.Author;
            AutoplayButton.Visibility = inSeries ? Visibility.Visible : Visibility.Collapsed;
            if (_playback != null)
                AutoplayButton.IsChecked = _playback.SeriesAutoplayEnabled;
        }

        private void UpdateChapterText()
        {
            if (_playback?.CurrentBook == null)
                return;

            if (!_playback.HasChapters)
            {
                ChapterText.Text = string.Empty;
                ChapterText.Visibility = Visibility.Collapsed;
                _currentChapterIndex = -1;
                return;
            }

            var index = _playback.FindChapterIndex(_playback.GetAbsolutePosition());
            if (index < 0 || index >= _playback.Chapters.Count)
            {
                ChapterText.Text = string.Empty;
                ChapterText.Visibility = Visibility.Collapsed;
                return;
            }

            _currentChapterIndex = index;
            var count = _playback.Chapters.Count;
            ChapterText.Text = $"Chapter ({index + 1}/{count})";
            ChapterText.Visibility = Visibility.Visible;
        }

        private void UpdateProgressSlider()
        {
            if (_playback?.CurrentBook == null || SeekFill == null || SeekTrack == null || _isDraggingSeek)
                return;

            var absolute = _playback.GetAbsolutePosition();
            double positionSeconds;

            if (_playback.HasChapters)
            {
                var index = _playback.FindChapterIndex(absolute);
                if (index < 0 || index >= _playback.Chapters.Count)
                    return;

                var chapter = _playback.Chapters[index];
                var chapterPos = absolute - chapter.Start;
                if (chapterPos < TimeSpan.Zero)
                    chapterPos = TimeSpan.Zero;

                _seekMaximumSeconds = Math.Max(chapter.Duration.TotalSeconds, 0.1);
                positionSeconds = chapterPos.TotalSeconds;
            }
            else
            {
                var total = _playback.TotalDuration > TimeSpan.Zero
                    ? _playback.TotalDuration
                    : _playback.CurrentBook.Duration;
                _seekMaximumSeconds = total > TimeSpan.Zero ? total.TotalSeconds : 1;
                positionSeconds = absolute.TotalSeconds;
            }

            SetSeekFillRatio(positionSeconds / _seekMaximumSeconds);
        }

        private void SetSeekFillRatio(double ratio)
        {
            ratio = Math.Clamp(ratio, 0, 1);
            var trackWidth = SeekTrack.ActualWidth;
            if (trackWidth <= 0)
                return;

            SeekFill.Width = trackWidth * ratio;
        }

        private TimeSpan GetAbsoluteFromSeekRatio(double ratio)
        {
            ratio = Math.Clamp(ratio, 0, 1);
            var seconds = _seekMaximumSeconds * ratio;

            if (_playback == null)
                return TimeSpan.Zero;

            if (_playback.HasChapters)
            {
                var index = _currentChapterIndex >= 0
                    ? _currentChapterIndex
                    : _playback.FindChapterIndex(_playback.GetAbsolutePosition());
                if (index < 0 || index >= _playback.Chapters.Count)
                    return TimeSpan.FromSeconds(seconds);

                return _playback.Chapters[index].Start + TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.FromSeconds(seconds);
        }

        private double GetSeekRatioFromPointer(PointerRoutedEventArgs e)
        {
            var x = e.GetCurrentPoint(SeekTrack).Position.X;
            var width = Math.Max(SeekTrack.ActualWidth, 1);
            return Math.Clamp(x / width, 0, 1);
        }

        private void UpdateControlLabels()
        {
            if (_playback == null)
                return;

            var speed = $"{_playback.Speed:0.##}x";
            var volume = $"Volume {(int)Math.Round(_playback.VolumePercent)}%";
            SpeedFlyoutValueText.Text = speed;
            VolumeFlyoutValueText.Text = $"{(int)Math.Round(_playback.VolumePercent)}%";
            ToolTipService.SetToolTip(SpeedButton, $"Speed {speed}");
            ToolTipService.SetToolTip(VolumeButton, volume);
            UpdateSleepTooltip();
            UpdateVolumeIcon();
            AutoplayButton.IsChecked = _playback.SeriesAutoplayEnabled;
        }

        private void UpdateSleepTooltip()
        {
            if (_playback == null)
                return;

            ToolTipService.SetToolTip(SleepButton, _playback.GetSleepTimerButtonLabel());
        }

        private void UpdateVolumeIcon()
        {
            if (_playback == null)
                return;

            VolumeIcon.Glyph = _playback.VolumePercent <= 0.5 ? "\uE74F" : "\uE767"; // Mute / Volume
        }

        private async System.Threading.Tasks.Task LoadCoverAsync()
        {
            var book = _playback?.CurrentBook;
            if (book == null || string.IsNullOrWhiteSpace(book.EffectiveCoverPath))
                return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(book.EffectiveCoverPath);
                var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 100);
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

        private void SeekTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingSeek = true;
            SeekTrack.CapturePointer(e.Pointer);
            var ratio = GetSeekRatioFromPointer(e);
            SetSeekFillRatio(ratio);
            _playback?.SeekTo(GetAbsoluteFromSeekRatio(ratio));
        }

        private void SeekTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingSeek)
                return;

            var ratio = GetSeekRatioFromPointer(e);
            SetSeekFillRatio(ratio);
            _playback?.SeekTo(GetAbsoluteFromSeekRatio(ratio));
        }

        private void SeekTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingSeek)
                return;

            _isDraggingSeek = false;
            try
            {
                SeekTrack.ReleasePointerCapture(e.Pointer);
            }
            catch
            {
            }

            var ratio = GetSeekRatioFromPointer(e);
            SetSeekFillRatio(ratio);
            _playback?.SeekTo(GetAbsoluteFromSeekRatio(ratio));
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) =>
            _playback?.TogglePlayPause();

        private void RewindButton_Click(object sender, RoutedEventArgs e) =>
            _playback?.SeekBySeconds(-10);

        private void FastForwardButton_Click(object sender, RoutedEventArgs e) =>
            _playback?.SeekBySeconds(10);

        private void PreviousButton_Click(object sender, RoutedEventArgs e) =>
            _playback?.PreviousChapter();

        private void NextButton_Click(object sender, RoutedEventArgs e) =>
            _playback?.NextChapter();

        private void ExpandButton_Click(object sender, RoutedEventArgs e) =>
            ExpandRequested?.Invoke();

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            IsPinned = PinButton.IsChecked == true;
            PinChanged?.Invoke(IsPinned);
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
            UpdateVolumeIcon();
        }

        private void SleepButton_Click(object sender, RoutedEventArgs e) => UpdateSleepTooltip();

        private void SleepPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_playback == null)
                return;

            if (sender is not MenuFlyoutItem { Tag: string tag } || !int.TryParse(tag, out var minutes))
                return;

            _playback.StartSleepTimerMinutes(minutes);
            UpdateSleepTooltip();
        }

        private void SleepEndChapter_Click(object sender, RoutedEventArgs e)
        {
            if (_playback == null)
                return;

            _playback.StartSleepTimerEndOfChapter();
            UpdateSleepTooltip();
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

            UpdateSleepTooltip();
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
            UpdateSleepTooltip();
        }

        private void AutoplayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playback == null)
                return;

            _playback.SeriesAutoplayEnabled = AutoplayButton.IsChecked == true;
        }
    }
}
