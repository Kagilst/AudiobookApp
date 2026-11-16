using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        private static readonly string[] DefaultSuggestedTags =
        {
            "Fiction",
            "Non-Fiction",
            "LitRPG",
            "Fantasy",
            "Sci-Fi",
            "Mystery",
            "Romance"
        };

        private readonly ObservableCollection<string> _selectedTags = new();

        public Book Book { get; set; }

        public event Action<Book>? ImportConfirmed;
        public event Action? CancelRequested;

        public ImportBookDialog(Book book, IEnumerable<string> existingTags)
        {
            InitializeComponent();

            Book = book;
            TitleText.Text = book.Title;
            AuthorText.Text = book.Author;
            HeaderText.Text = book.MediaType == BookMediaType.Epub
                ? "Import Epub"
                : "Import Audiobook";

            var suggestions = DefaultSuggestedTags
                .Concat(existingTags)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            SuggestedTagsList.ItemsSource = suggestions;
            SuggestedTagsList.SelectionChanged += SuggestedTagsList_SelectionChanged;
            SelectedTagsPanel.ItemsSource = _selectedTags;

            Loaded += async (_, _) => await LoadCoverAsync();
        }

        private void SuggestedTagsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (var item in e.AddedItems.OfType<string>())
                AddSelectedTag(item);

            foreach (var item in e.RemovedItems.OfType<string>())
            {
                var match = _selectedTags.FirstOrDefault(t =>
                    string.Equals(t, item, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    _selectedTags.Remove(match);
            }
        }

        private void AddTag_Click(object sender, RoutedEventArgs e)
        {
            var tag = NewTagBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(tag))
                return;

            AddSelectedTag(tag);
            NewTagBox.Text = "";

            if (SuggestedTagsList.ItemsSource is List<string> items &&
                !items.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(tag);
                items.Sort(StringComparer.OrdinalIgnoreCase);
                SuggestedTagsList.ItemsSource = null;
                SuggestedTagsList.ItemsSource = items;

                var match = items.First(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                SuggestedTagsList.SelectedItems.Add(match);
            }
            else if (SuggestedTagsList.ItemsSource is IEnumerable<string> existing)
            {
                var match = existing.FirstOrDefault(t =>
                    string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                if (match != null && !SuggestedTagsList.SelectedItems.Contains(match))
                    SuggestedTagsList.SelectedItems.Add(match);
            }
        }

        private void AddSelectedTag(string tag)
        {
            if (_selectedTags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                return;

            _selectedTags.Add(tag);
        }

        private async System.Threading.Tasks.Task LoadCoverAsync()
        {
            if (string.IsNullOrWhiteSpace(Book.FilePath))
                return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(Book.FilePath);
                var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 240);

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
            Book.Tags = _selectedTags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            ImportConfirmed?.Invoke(Book);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke();
        }
    }
}
