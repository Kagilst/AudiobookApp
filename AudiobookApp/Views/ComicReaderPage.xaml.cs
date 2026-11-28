using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using AudiobookApp.Models;
using AudiobookApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AudiobookApp.Views
{
    public sealed partial class ComicReaderPage : Page
    {
        private const double MinZoom = 0.5;
        private const double MaxZoom = 3.0;
        private const double ZoomStep = 0.1;

        private readonly Book _book;
        private readonly CbzComicService _comic = new();
        private int _pageIndex;
        private bool _suppressModeCombo;
        private bool _suppressWidthCombo;
        private bool _stripBuilt;
        private readonly List<Image> _stripImages = new();
        private string _pageWidthKey = "normal";
        private double _zoom = 1.0;

        public event Action<Book>? ProgressChanged;

        public ComicReaderPage(Book book)
        {
            InitializeComponent();
            _book = book;
            LoadDisplaySettings();
            BookTitleText.Text = book.Title;
            Loaded += async (_, _) => await LoadAsync();
            SizeChanged += (_, _) => ApplyLayoutToImages();
            Unloaded += (_, _) =>
            {
                PersistProgress();
                _comic.Dispose();
            };
        }

        private void LoadDisplaySettings()
        {
            var settings = ComicReaderSettings.Load();
            _pageWidthKey = settings.PageWidth is "thin" or "normal" or "wide"
                ? settings.PageWidth
                : "normal";
            _zoom = Math.Clamp(settings.Zoom, MinZoom, MaxZoom);
        }

        private void SaveDisplaySettings()
        {
            new ComicReaderSettings
            {
                PageWidth = _pageWidthKey,
                Zoom = _zoom
            }.Save();
        }

        private async Task LoadAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
                _comic.Open(_book.FilePath);
                _pageIndex = Math.Clamp(_book.LastComicPageIndex, 0, Math.Max(0, _comic.PageCount - 1));
                SyncReadingModeCombo();
                SyncPageWidthCombo();
                UpdateZoomLabel();
                await ApplyReadingModeAsync(rebuildStrip: true);
            }
            catch (Exception ex)
            {
                PageStatusText.Text = "Unable to open comic.";
                FooterStatusText.Text = ex.Message;
                PreviousPageButton.IsEnabled = false;
                NextPageButton.IsEnabled = false;
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        private void SyncReadingModeCombo()
        {
            _suppressModeCombo = true;
            SelectComboByTag(ReadingModeCombo, _book.ComicReadingMode.ToString());
            if (ReadingModeCombo.SelectedItem == null && ReadingModeCombo.Items.Count > 0)
                ReadingModeCombo.SelectedIndex = 0;
            _suppressModeCombo = false;
        }

        private void SyncPageWidthCombo()
        {
            _suppressWidthCombo = true;
            SelectComboByTag(PageWidthCombo, _pageWidthKey);
            if (PageWidthCombo.SelectedItem == null && PageWidthCombo.Items.Count > 0)
                PageWidthCombo.SelectedIndex = 1;
            _suppressWidthCombo = false;
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag is string t && t.Equals(tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private async void ReadingModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressModeCombo || ReadingModeCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
                return;

            if (!Enum.TryParse<ComicReadingMode>(tag, out var mode))
                return;

            if (_book.ComicReadingMode == mode)
                return;

            _book.ComicReadingMode = mode;
            PersistProgress();
            ProgressChanged?.Invoke(_book);
            await ApplyReadingModeAsync(rebuildStrip: true);
        }

        private void PageWidthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressWidthCombo || PageWidthCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
                return;

            if (tag == _pageWidthKey)
                return;

            _pageWidthKey = tag;
            SaveDisplaySettings();
            ApplyLayoutToImages();
        }

        private void ZoomMinusButton_Click(object sender, RoutedEventArgs e)
        {
            SetZoom(_zoom - ZoomStep);
        }

        private void ZoomPlusButton_Click(object sender, RoutedEventArgs e)
        {
            SetZoom(_zoom + ZoomStep);
        }

        private void SetZoom(double value)
        {
            var next = Math.Clamp(Math.Round(value, 1), MinZoom, MaxZoom);
            if (Math.Abs(next - _zoom) < 0.001)
                return;

            _zoom = next;
            SaveDisplaySettings();
            UpdateZoomLabel();
            ApplyLayoutToImages();
        }

        private void UpdateZoomLabel()
        {
            ZoomLabel.Text = $"{(int)Math.Round(_zoom * 100)}%";
            ZoomMinusButton.IsEnabled = _zoom > MinZoom + 0.001;
            ZoomPlusButton.IsEnabled = _zoom < MaxZoom - 0.001;
        }

        private async Task ApplyReadingModeAsync(bool rebuildStrip)
        {
            var mode = _book.ComicReadingMode;
            var isStrip = mode == ComicReadingMode.VerticalStrip;

            PageModeHost.Visibility = isStrip ? Visibility.Collapsed : Visibility.Visible;
            StripScroller.Visibility = isStrip ? Visibility.Visible : Visibility.Collapsed;
            PreviousPageButton.Visibility = isStrip ? Visibility.Collapsed : Visibility.Visible;
            NextPageButton.Visibility = isStrip ? Visibility.Collapsed : Visibility.Visible;

            if (isStrip)
            {
                if (rebuildStrip || !_stripBuilt)
                    await BuildStripAsync();
                else
                    ApplyLayoutToImages();
                ScrollStripToPage(_pageIndex);
            }
            else
            {
                await ShowPageAsync(_pageIndex);
                ApplyLayoutToImages();
            }

            UpdateStatus();
        }

        private double GetAvailableWidth()
        {
            var hostWidth = ReaderHost.ActualWidth;
            if (hostWidth <= 0)
                hostWidth = Math.Max(ActualWidth - 48, 400);
            return Math.Max(hostWidth, 200);
        }

        private double GetContentMaxWidth()
        {
            var available = GetAvailableWidth();
            var fraction = _pageWidthKey switch
            {
                "thin" => 0.45,
                "wide" => 0.95,
                _ => 0.70
            };
            return Math.Max(available * fraction, 160) * _zoom;
        }

        private void ApplyLayoutToImages()
        {
            var maxWidth = GetContentMaxWidth();
            PageImage.MaxWidth = maxWidth;
            foreach (var image in _stripImages)
                image.MaxWidth = maxWidth;
        }

        private async Task BuildStripAsync()
        {
            StripPanel.Children.Clear();
            _stripImages.Clear();
            _stripBuilt = false;

            var maxWidth = GetContentMaxWidth();
            for (var i = 0; i < _comic.PageCount; i++)
            {
                var image = new Image
                {
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MaxWidth = maxWidth,
                    Margin = new Thickness(0),
                    Tag = i
                };
                image.Source = await CreateBitmapAsync(_comic.GetPageBytes(i));
                _stripImages.Add(image);
                StripPanel.Children.Add(image);
            }

            _stripBuilt = true;
            StripPanel.UpdateLayout();
            StripScroller.UpdateLayout();
        }

        private void ScrollStripToPage(int index)
        {
            if (index < 0 || index >= _stripImages.Count)
                return;

            var target = _stripImages[index];
            target.UpdateLayout();
            var transform = target.TransformToVisual(StripPanel);
            var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            StripScroller.ChangeView(null, point.Y, null, disableAnimation: true);
        }

        private void StripScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!_stripBuilt || _stripImages.Count == 0 || e.IsIntermediate)
                return;

            var offset = StripScroller.VerticalOffset + 8;
            var best = 0;
            for (var i = 0; i < _stripImages.Count; i++)
            {
                var img = _stripImages[i];
                var transform = img.TransformToVisual(StripPanel);
                var y = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                if (y <= offset)
                    best = i;
                else
                    break;
            }

            if (best != _pageIndex)
            {
                _pageIndex = best;
                PersistProgress();
                ProgressChanged?.Invoke(_book);
                UpdateStatus();
            }
        }

        private async Task ShowPageAsync(int index)
        {
            if (_comic.PageCount == 0)
                return;

            _pageIndex = Math.Clamp(index, 0, _comic.PageCount - 1);
            PageImage.Source = await CreateBitmapAsync(_comic.GetPageBytes(_pageIndex));
            PersistProgress();
            ProgressChanged?.Invoke(_book);
            UpdateStatus();
        }

        private async void PreviousPageButton_Click(object sender, RoutedEventArgs e)
        {
            var delta = _book.ComicReadingMode == ComicReadingMode.RightToLeft ? 1 : -1;
            await ShowPageAsync(_pageIndex + delta);
        }

        private async void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            var delta = _book.ComicReadingMode == ComicReadingMode.RightToLeft ? -1 : 1;
            await ShowPageAsync(_pageIndex + delta);
        }

        private void UpdateStatus()
        {
            if (_comic.PageCount == 0)
            {
                PageStatusText.Text = "";
                FooterStatusText.Text = "";
                PreviousPageButton.IsEnabled = false;
                NextPageButton.IsEnabled = false;
                return;
            }

            var display = _pageIndex + 1;
            PageStatusText.Text = $"Page {display} of {_comic.PageCount}";
            FooterStatusText.Text = _book.ComicReadingMode switch
            {
                ComicReadingMode.VerticalStrip => "Scroll vertically · strip mode",
                ComicReadingMode.RightToLeft => "Right to left · use Previous/Next",
                _ => "Left to right · use Previous/Next"
            };

            if (_book.ComicReadingMode == ComicReadingMode.RightToLeft)
            {
                // RTL: Next on the left (advances), Previous on the right (goes back).
                PreviousPageButton.Content = "Next";
                NextPageButton.Content = "Previous";
                PreviousPageButton.IsEnabled = _pageIndex < _comic.PageCount - 1;
                NextPageButton.IsEnabled = _pageIndex > 0;
            }
            else if (_book.ComicReadingMode == ComicReadingMode.LeftToRight)
            {
                PreviousPageButton.Content = "Previous";
                NextPageButton.Content = "Next";
                PreviousPageButton.IsEnabled = _pageIndex > 0;
                NextPageButton.IsEnabled = _pageIndex < _comic.PageCount - 1;
            }
            else
            {
                PreviousPageButton.Content = "Previous";
                NextPageButton.Content = "Next";
                PreviousPageButton.IsEnabled = false;
                NextPageButton.IsEnabled = false;
            }
        }

        private void PersistProgress()
        {
            _book.LastComicPageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, _comic.PageCount - 1));
        }

        private static async Task<BitmapImage> CreateBitmapAsync(byte[] bytes)
        {
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
    }
}
