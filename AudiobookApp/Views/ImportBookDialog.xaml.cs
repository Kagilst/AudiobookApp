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
        private readonly List<Series> _series;
        private readonly List<BookPickItem> _libraryPicks;

        public Book Book { get; set; }

        public event Action<Book, ImportSeriesChoice>? ImportConfirmed;
        public event Action? CancelRequested;

        public ImportBookDialog(
            Book book,
            IEnumerable<string> existingTags,
            IEnumerable<Series> series,
            IEnumerable<Book> libraryBooks)
        {
            InitializeComponent();

            Book = book;
            _series = series.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _libraryPicks = libraryBooks
                .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .Select(b => new BookPickItem(b))
                .ToList();

            TitleText.Text = book.Title;
            AuthorText.Text = book.IsMultiFileAudiobook
                ? $"{book.Author} · {book.AudioFilePaths.Count} files"
                : book.Author;
            HeaderText.Text = book.MediaType == BookMediaType.Epub
                ? "Import Epub"
                : book.IsMultiFileAudiobook
                    ? "Import Multi-file Audiobook"
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

            ExistingSeriesCombo.ItemsSource = _series.Select(s => s.Name).ToList();
            if (_series.Count > 0)
                ExistingSeriesCombo.SelectedIndex = 0;
            else
                AddToSeriesRadio.IsEnabled = false;

            LibraryBooksPicker.ItemsSource = _libraryPicks;

            Loaded += async (_, _) => await LoadCoverAsync();
        }

        private void SeriesModeButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var mode = GetSeriesMode();
            ExistingSeriesCombo.Visibility = mode == SeriesImportMode.AddToExisting
                ? Visibility.Visible
                : Visibility.Collapsed;
            CreateSeriesPanel.Visibility = mode == SeriesImportMode.CreateNew
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private SeriesImportMode GetSeriesMode()
        {
            if (SeriesModeButtons.SelectedItem is RadioButton radio &&
                radio.Tag is string tag)
            {
                return tag switch
                {
                    "Add" => SeriesImportMode.AddToExisting,
                    "Create" => SeriesImportMode.CreateNew,
                    _ => SeriesImportMode.None
                };
            }

            return SeriesImportMode.None;
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

            if (Book.Id == Guid.Empty)
                Book.Id = Guid.NewGuid();

            var choice = new ImportSeriesChoice { Mode = GetSeriesMode() };

            if (choice.Mode == SeriesImportMode.AddToExisting)
            {
                if (ExistingSeriesCombo.SelectedItem is string name)
                {
                    choice.ExistingSeries = _series.FirstOrDefault(s =>
                        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                }

                if (choice.ExistingSeries == null)
                    choice.Mode = SeriesImportMode.None;
            }
            else if (choice.Mode == SeriesImportMode.CreateNew)
            {
                choice.NewSeriesName = NewSeriesNameBox.Text?.Trim() ?? "";
                choice.AdditionalBooks = LibraryBooksPicker.SelectedItems
                    .OfType<BookPickItem>()
                    .Select(p => p.Book)
                    .ToList();

                if (string.IsNullOrWhiteSpace(choice.NewSeriesName))
                    choice.Mode = SeriesImportMode.None;
            }

            ImportConfirmed?.Invoke(Book, choice);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke();
        }

        private sealed class BookPickItem
        {
            public BookPickItem(Book book) => Book = book;
            public Book Book { get; }
            public string Title => Book.Title;
            public string Author => Book.Author;
        }
    }

    public enum SeriesImportMode
    {
        None,
        AddToExisting,
        CreateNew
    }

    public sealed class ImportSeriesChoice
    {
        public SeriesImportMode Mode { get; set; } = SeriesImportMode.None;
        public Series? ExistingSeries { get; set; }
        public string NewSeriesName { get; set; } = "";
        public List<Book> AdditionalBooks { get; set; } = new();
    }
}
