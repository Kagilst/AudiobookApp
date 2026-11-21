using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using AudiobookApp.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AudiobookApp.Views
{
    public sealed partial class SeriesReorderPage : Page
    {
        private readonly ObservableCollection<BookSortItem> _items = new();
        private readonly Series _series;
        private bool _isContinueMode = true;

        public event Action<Series, IReadOnlyList<Guid>>? OrderConfirmed;
        public event Action? CancelRequested;

        public SeriesReorderPage(Series series, IEnumerable<Book> booksInOrder)
        {
            InitializeComponent();
            _series = series;
            TitleText.Text = string.IsNullOrWhiteSpace(series.Name)
                ? "Reorder series"
                : $"Reorder · {series.Name}";

            foreach (var book in booksInOrder)
                _items.Add(new BookSortItem(book));

            RefreshIndexes();
            BooksList.ItemsSource = _items;
            UpdateMoveButtons();
            _ = LoadCoversAsync();
        }

        public void SetContinueMode(bool isContinue)
        {
            _isContinueMode = isContinue;
            SaveButton.Content = isContinue ? "Continue" : "Save order";
            SubtitleText.Text = isContinue
                ? "Arrange the books in series order. Drag to reorder, or use Move up / Move down, then continue to name the series."
                : "Books appear in this order in the series. Drag to reorder, remove books, or use Move up / Move down, then save.";
        }

        private void BooksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMoveButtons();
        }

        private void BooksList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            RefreshIndexes();
            UpdateMoveButtons();
        }

        private void UpdateMoveButtons()
        {
            var index = BooksList.SelectedIndex;
            MoveUpButton.IsEnabled = index > 0;
            MoveDownButton.IsEnabled = index >= 0 && index < _items.Count - 1;
            RemoveButton.IsEnabled = index >= 0 && _items.Count > 0;
            SaveButton.IsEnabled = _isContinueMode ? _items.Count >= 2 : _items.Count >= 1;
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            var index = BooksList.SelectedIndex;
            if (index <= 0)
                return;

            _items.Move(index, index - 1);
            RefreshIndexes();
            BooksList.SelectedIndex = index - 1;
            UpdateMoveButtons();
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            var index = BooksList.SelectedIndex;
            if (index < 0 || index >= _items.Count - 1)
                return;

            _items.Move(index, index + 1);
            RefreshIndexes();
            BooksList.SelectedIndex = index + 1;
            UpdateMoveButtons();
        }

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var index = BooksList.SelectedIndex;
            if (index < 0 || index >= _items.Count)
                return;

            var item = _items[index];
            var confirm = await new ContentDialog
            {
                Title = "Remove from series",
                Content = $"Remove \"{item.Title}\" from this series?",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            }.ShowAsync();

            if (confirm != ContentDialogResult.Primary)
                return;

            _items.RemoveAt(index);
            RefreshIndexes();

            if (_items.Count == 0)
            {
                BooksList.SelectedIndex = -1;
            }
            else
            {
                BooksList.SelectedIndex = Math.Min(index, _items.Count - 1);
            }

            UpdateMoveButtons();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_isContinueMode && _items.Count < 2)
                return;

            OrderConfirmed?.Invoke(_series, _items.Select(i => i.Book.Id).ToList());
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke();
        }

        private void RefreshIndexes()
        {
            for (var i = 0; i < _items.Count; i++)
                _items[i].IndexDisplay = $"{i + 1}.";
        }

        private async Task LoadCoversAsync()
        {
            foreach (var item in _items)
                await LoadCoverAsync(item);
        }

        private static async Task LoadCoverAsync(BookSortItem item)
        {
            var path = item.Book.EffectiveCoverPath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 120);
                if (thumbnail == null)
                    return;

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                item.CoverImage = bitmap;
            }
            catch
            {
            }
        }

        private sealed class BookSortItem : INotifyPropertyChanged
        {
            private string _indexDisplay = "";
            private ImageSource? _coverImage;

            public BookSortItem(Book book)
            {
                Book = book;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public Book Book { get; }
            public string Title => Book.Title;
            public string Author => Book.Author;

            public ImageSource? CoverImage
            {
                get => _coverImage;
                set
                {
                    _coverImage = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
                }
            }

            public string IndexDisplay
            {
                get => _indexDisplay;
                set
                {
                    _indexDisplay = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndexDisplay)));
                }
            }
        }
    }
}
