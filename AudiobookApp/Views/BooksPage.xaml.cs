using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using AudiobookApp.Models;
using WinRT.Interop;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AudiobookApp.Views
{
    public sealed partial class BooksPage : Page
    {
        private readonly ObservableCollection<BookDisplayItem> _displayBooks = new();

        public BooksPage()
        {
            InitializeComponent();
            BooksList.ItemsSource = _displayBooks;
            BooksGrid.ItemsSource = _displayBooks;
        }

        public event EventHandler? ImportRequested;

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            ImportRequested?.Invoke(this, EventArgs.Empty);
        }

        public event Action<Book>? RemoveRequested;

        public event Action<Book>? BookSelected;

        private void BooksList_ItemClick(
        object sender,
        ItemClickEventArgs e)
        {
            if (e.ClickedItem is BookDisplayItem item)
            {
                BookSelected?.Invoke(item.Book);
            }
        }

        private void BooksGrid_ItemClick(
            object sender,
            ItemClickEventArgs e)
        {
            if (e.ClickedItem is BookDisplayItem item)
            {
                BookSelected?.Invoke(item.Book);
            }
        }

        private void RemoveBook_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is BookDisplayItem item)
            {
                RemoveRequested?.Invoke(item.Book);
            }
        }

        public void SetBooks(IEnumerable<Book> books)
        {
            _displayBooks.Clear();

            foreach (var book in books)
            {
                _displayBooks.Add(new BookDisplayItem(book));
            }

            _ = LoadCoversAsync(_displayBooks.ToList());
        }

        private void ListViewButton_Click(object sender, RoutedEventArgs e)
        {
            SetDisplayMode(useIconView: false);
        }

        private void IconViewButton_Click(object sender, RoutedEventArgs e)
        {
            SetDisplayMode(useIconView: true);
        }

        private void SetDisplayMode(bool useIconView)
        {
            BooksList.Visibility = useIconView ? Visibility.Collapsed : Visibility.Visible;
            BooksGrid.Visibility = useIconView ? Visibility.Visible : Visibility.Collapsed;

            ListViewButton.IsChecked = !useIconView;
            IconViewButton.IsChecked = useIconView;
        }

        private static async Task LoadCoversAsync(IEnumerable<BookDisplayItem> items)
        {
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Book.FilePath))
                    continue;

                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(item.Book.FilePath);
                    var thumbnail = await file.GetThumbnailAsync(
                        ThumbnailMode.SingleItem,
                        300);

                    if (thumbnail != null)
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(thumbnail);
                        item.CoverImage = bitmap;
                    }
                }
                catch
                {
                    // Keep the placeholder when cover art is unavailable.
                }
            }
        }

        private sealed class BookDisplayItem : INotifyPropertyChanged
        {
            private ImageSource? _coverImage;

            public BookDisplayItem(Book book)
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
        }
    }
}
