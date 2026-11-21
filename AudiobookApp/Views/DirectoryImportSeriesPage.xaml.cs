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
    public sealed partial class DirectoryImportSeriesPage : Page
    {
        private const string UngroupedKey = "Not in a series";

        private readonly HashSet<Guid> _importedIds;
        private readonly Dictionary<Guid, Book> _libraryById;
        private readonly ObservableCollection<BookRow> _allRows = new();
        private readonly ObservableCollection<Section> _sections = new();
        private readonly Dictionary<string, bool> _expandedState = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<Series> _series;
        private bool _hasSearchFilter;

        public event Action<Series, IReadOnlyList<Book>>? AddToSeriesRequested;
        public event Action<string, IReadOnlyList<Book>>? CreateSeriesRequested;
        public event Action<Series, IReadOnlyList<Guid>>? SeriesOrderChangedRequested;
        public event Action<Book>? RemoveBookFromSeriesRequested;
        public event Action? DoneRequested;
        public event Action? CancelRequested;

        public DirectoryImportSeriesPage(
            IEnumerable<Book> importedBooks,
            IEnumerable<Book> libraryBooks,
            IEnumerable<Series> series)
        {
            InitializeComponent();
            _series = series.ToList();
            _importedIds = importedBooks.Select(b => b.Id).ToHashSet();
            _libraryById = libraryBooks.ToDictionary(b => b.Id);
            SectionsList.ItemsSource = _sections;

            foreach (var book in importedBooks.OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase))
            {
                var row = CreateRow(book, FindSeriesName(book));
                _allRows.Add(row);
            }

            foreach (var seriesItem in _series)
            {
                foreach (var bookId in seriesItem.BookIds)
                {
                    if (_allRows.Any(r => r.Book.Id == bookId))
                        continue;

                    if (!_libraryById.TryGetValue(bookId, out var book))
                        continue;

                    _allRows.Add(CreateRow(book, seriesItem.Name));
                }
            }

            _expandedState[UngroupedKey] = true;
            RebuildSections();
            _ = LoadCoversAsync();
        }

        public void RefreshSeries(IEnumerable<Series> series, IEnumerable<Book> libraryBooks)
        {
            _series = series.ToList();
            foreach (var book in libraryBooks)
                _libraryById[book.Id] = book;

            for (var i = _allRows.Count - 1; i >= 0; i--)
            {
                if (!_importedIds.Contains(_allRows[i].Book.Id))
                    _allRows.RemoveAt(i);
            }

            foreach (var row in _allRows)
            {
                row.SeriesLabel = FindSeriesName(row.Book);
                if (!string.IsNullOrWhiteSpace(row.SeriesLabel))
                    row.IsChecked = false;
            }

            foreach (var seriesItem in _series)
            {
                if (!seriesItem.BookIds.Any(id => _importedIds.Contains(id)))
                    continue;

                foreach (var bookId in seriesItem.BookIds)
                {
                    if (_allRows.Any(r => r.Book.Id == bookId))
                        continue;

                    if (!_libraryById.TryGetValue(bookId, out var book))
                        continue;

                    _allRows.Add(CreateRow(book, seriesItem.Name));
                }
            }

            RebuildSections();
            _ = LoadCoversAsync();
            UpdateStatus();
        }

        private BookRow CreateRow(Book book, string seriesLabel)
        {
            var row = new BookRow(book, seriesLabel, _importedIds.Contains(book.Id));
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BookRow.IsChecked))
                    UpdateStatus();
            };
            return row;
        }

        private string FindSeriesName(Book book)
        {
            var series = _series.FirstOrDefault(s => s.BookIds.Contains(book.Id));
            return series == null ? "" : series.Name;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildSections();

        private BookRow GetOrCreateRow(Book book, string seriesName)
        {
            var existing = _allRows.FirstOrDefault(r => r.Book.Id == book.Id);
            if (existing != null)
            {
                existing.SeriesLabel = seriesName;
                return existing;
            }

            var row = CreateRow(book, seriesName);
            _allRows.Add(row);
            return row;
        }

        private void RebuildSections()
        {
            foreach (var section in _sections)
                _expandedState[section.Title] = section.IsExpanded;

            var query = SearchBox.Text?.Trim() ?? "";
            _hasSearchFilter = !string.IsNullOrWhiteSpace(query);

            bool Matches(BookRow row) =>
                !_hasSearchFilter ||
                row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.SeriesLabel.Contains(query, StringComparison.OrdinalIgnoreCase);

            _sections.Clear();

            var ungrouped = _allRows
                .Where(r => string.IsNullOrWhiteSpace(r.SeriesLabel))
                .Where(Matches)
                .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ungrouped.Count > 0)
            {
                var section = new Section(UngroupedKey, isSeries: false, series: null, canReorder: false);
                section.IsExpanded = _expandedState.GetValueOrDefault(UngroupedKey, true);
                foreach (var row in ungrouped)
                {
                    row.IndexDisplay = "";
                    section.Members.Add(row);
                }

                section.NotifyCounts();
                _sections.Add(section);
            }

            foreach (var seriesItem in _series.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!seriesItem.BookIds.Any(id => _importedIds.Contains(id)))
                    continue;

                var members = new List<BookRow>();
                foreach (var bookId in seriesItem.BookIds)
                {
                    Book? book = _allRows.FirstOrDefault(r => r.Book.Id == bookId)?.Book;
                    if (book == null)
                        _libraryById.TryGetValue(bookId, out book);
                    if (book == null)
                        continue;

                    var row = GetOrCreateRow(book, seriesItem.Name);
                    row.IsChecked = false;
                    if (Matches(row))
                        members.Add(row);
                }

                if (members.Count == 0)
                    continue;

                // Only allow drag-reorder when the full series is visible (no search filter).
                var canReorder = !_hasSearchFilter;
                var section = new Section(seriesItem.Name, isSeries: true, seriesItem, canReorder);
                section.IsExpanded = _expandedState.GetValueOrDefault(seriesItem.Name, true);
                foreach (var row in members)
                    section.Members.Add(row);
                section.RefreshIndexes();
                section.NotifyCounts();
                _sections.Add(section);
            }

            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var selectable = _allRows.Count(r => r.CheckboxVisibility == Visibility.Visible);
            var checkedCount = _allRows.Count(r => r.IsChecked && r.CheckboxVisibility == Visibility.Visible);
            var shown = _sections.Sum(s => s.Members.Count);
            SelectionStatusText.Text = $"{checkedCount} selected · {selectable} available · {shown} shown";
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var section in _sections)
            {
                foreach (var row in section.Members.Where(r => r.CheckboxVisibility == Visibility.Visible))
                    row.IsChecked = true;
            }

            UpdateStatus();
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _allRows)
                row.IsChecked = false;

            UpdateStatus();
        }

        private List<Book> GetCheckedBooks() =>
            _allRows
                .Where(r => r.IsChecked && r.CheckboxVisibility == Visibility.Visible)
                .Select(r => r.Book)
                .ToList();

        private async void AddToSeries_Click(object sender, RoutedEventArgs e)
        {
            var checkedBooks = GetCheckedBooks();
            if (checkedBooks.Count == 0)
            {
                await new ContentDialog
                {
                    Title = "No books selected",
                    Content = "Check one or more ungrouped books first.",
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                }.ShowAsync();
                return;
            }

            if (_series.Count == 0)
            {
                await new ContentDialog
                {
                    Title = "No series yet",
                    Content = "Create a new series first.",
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                }.ShowAsync();
                return;
            }

            var combo = new ComboBox
            {
                Header = "Series",
                Width = 320,
                ItemsSource = _series.Select(s => s.Name).ToList(),
                SelectedIndex = 0
            };

            var dialog = new ContentDialog
            {
                Title = "Add to series",
                Content = combo,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (combo.SelectedItem is not string name)
                return;

            var series = _series.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (series == null)
                return;

            AddToSeriesRequested?.Invoke(series, checkedBooks);
        }

        private async void CreateSeries_Click(object sender, RoutedEventArgs e)
        {
            var checkedBooks = GetCheckedBooks();
            if (checkedBooks.Count == 0)
            {
                await new ContentDialog
                {
                    Title = "No books selected",
                    Content = "Check one or more ungrouped books first.",
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                }.ShowAsync();
                return;
            }

            var nameBox = new TextBox
            {
                Header = "Series name",
                PlaceholderText = "e.g. Classroom of the Elite",
                Text = checkedBooks[0].Title
            };

            var dialog = new ContentDialog
            {
                Title = "Create new series",
                Content = nameBox,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            var name = nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            CreateSeriesRequested?.Invoke(name, checkedBooks);
        }

        private void SeriesList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            if (sender.DataContext is not Section section || section.Series == null || !section.CanReorder)
                return;

            section.RefreshIndexes();
            SeriesOrderChangedRequested?.Invoke(
                section.Series,
                section.Members.Select(m => m.Book.Id).ToList());
        }

        private void RemoveFromSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not BookRow row)
                return;

            if (string.IsNullOrWhiteSpace(row.SeriesLabel))
                return;

            RemoveBookFromSeriesRequested?.Invoke(row.Book);
        }

        private void Done_Click(object sender, RoutedEventArgs e) => DoneRequested?.Invoke();

        private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();

        private async Task LoadCoversAsync()
        {
            foreach (var row in _allRows.Where(r => r.CoverImage == null))
                await LoadCoverAsync(row);
        }

        private static async Task LoadCoverAsync(BookRow row)
        {
            var path = row.Book.EffectiveCoverPath;
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
                row.CoverImage = bitmap;
            }
            catch
            {
            }
        }

        private sealed class Section : INotifyPropertyChanged
        {
            private bool _isExpanded = true;

            public Section(string title, bool isSeries, Series? series, bool canReorder)
            {
                Title = title;
                IsSeries = isSeries;
                Series = series;
                CanReorder = canReorder;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string Title { get; }
            public bool IsSeries { get; }
            public Series? Series { get; }
            public bool CanReorder { get; }
            public ObservableCollection<BookRow> Members { get; } = new();

            public string CountLabel => $"({Members.Count})";

            public string ReorderHint =>
                IsSeries && CanReorder ? "· drag to reorder" : "";

            public bool IsExpanded
            {
                get => _isExpanded;
                set
                {
                    if (_isExpanded == value)
                        return;
                    _isExpanded = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                }
            }

            public void NotifyCounts()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountLabel)));
            }

            public void RefreshIndexes()
            {
                for (var i = 0; i < Members.Count; i++)
                    Members[i].IndexDisplay = $"{i + 1}.";
            }
        }

        private sealed class BookRow : INotifyPropertyChanged
        {
            private bool _isChecked;
            private string _seriesLabel;
            private string _indexDisplay = "";
            private ImageSource? _coverImage;

            public BookRow(Book book, string seriesLabel, bool isFromImport)
            {
                Book = book;
                _seriesLabel = seriesLabel;
                IsFromImport = isFromImport;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public Book Book { get; }
            public bool IsFromImport { get; }
            public string Title => Book.Title;
            public string Author => Book.Author;

            public string LibraryBadge => IsFromImport ? "" : "In library";

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
                    if (_indexDisplay == value)
                        return;
                    _indexDisplay = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndexDisplay)));
                }
            }

            public string SeriesLabel
            {
                get => _seriesLabel;
                set
                {
                    if (_seriesLabel == value)
                        return;
                    _seriesLabel = value;
                    if (!string.IsNullOrWhiteSpace(_seriesLabel))
                        IsChecked = false;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeriesLabel)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemoveFromSeriesVisibility)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckboxVisibility)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrderNumberVisibility)));
                }
            }

            public Visibility RemoveFromSeriesVisibility =>
                string.IsNullOrWhiteSpace(_seriesLabel) ? Visibility.Collapsed : Visibility.Visible;

            public Visibility CheckboxVisibility =>
                string.IsNullOrWhiteSpace(_seriesLabel) ? Visibility.Visible : Visibility.Collapsed;

            public Visibility OrderNumberVisibility =>
                string.IsNullOrWhiteSpace(_seriesLabel) ? Visibility.Collapsed : Visibility.Visible;

            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value)
                        return;
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }
    }
}
