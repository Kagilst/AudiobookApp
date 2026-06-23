using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using AudiobookApp.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AudiobookApp.Views
{
    public sealed partial class ImportBookDialog : Page
    {
        public Book Book { get; set; }

        public event Action<Book>? ImportConfirmed;

        public ImportBookDialog(Book book, IEnumerable<LibraryCategory> categories)
        {
            InitializeComponent();

            Book = book;

            TitleText.Text = book.Title;
            AuthorText.Text = book.Author;

            var options = new List<CategoryOption>
            {
                new CategoryOption { Name = "None" }
            };

            options.AddRange(categories.Select(c => new CategoryOption
            {
                Name = c.Name,
                Category = c
            }));

            CategoryCombo.ItemsSource = options;
            CategoryCombo.DisplayMemberPath = "Name";
            CategoryCombo.SelectedIndex = 0;

            Loaded += async (_, _) => await LoadCoverAsync();
        }

        private async System.Threading.Tasks.Task LoadCoverAsync()
        {
            if (string.IsNullOrWhiteSpace(Book.FilePath))
                return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(Book.FilePath);
                var thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.SingleItem,
                    240);

                if (thumbnail != null)
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(thumbnail);
                    CoverImage.Source = bitmap;
                    CoverPlaceholder.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                // Keep the placeholder when cover art is unavailable.
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var book = Book;

            if (CategoryCombo.SelectedItem is CategoryOption option && option.Category != null)
            {
                book.CategoryName = option.Category.Name;
            }
            else
            {
                book.CategoryName = "";
            }

            ImportConfirmed?.Invoke(book);
        }

        private sealed class CategoryOption
        {
            public string Name { get; set; } = "";
            public LibraryCategory? Category { get; set; }
        }
    }
}
