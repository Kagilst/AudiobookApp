using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using AudiobookApp.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AudiobookApp.Views
{
    public sealed partial class BooksPage : Page
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

        private readonly ObservableCollection<LibraryDisplayItem> _displayItems = new();
        private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _availableTags = new();
        private ProgressFilter _progressFilter = ProgressFilter.None;
        private bool _suppressFilterEvents;

        private IReadOnlyList<Series> _allSeries = Array.Empty<Series>();
        private IReadOnlyList<Book> _allBooks = Array.Empty<Book>();

        public BooksPage()
        {
            InitializeComponent();
            BooksList.ItemsSource = _displayItems;
            BooksGrid.ItemsSource = _displayItems;
        }

        public event EventHandler? ImportRequested;
        public event Action? FilterChanged;
        public event Action<Book>? RemoveRequested;
        public event Action<Book>? BookSelected;
        public event Action<Book>? TagsChanged;
        public event Action<Book>? RatingChanged;
        public event Action? SeriesChanged;
        public event Action<Book, Series>? AddBookToSeriesRequested;
        public event Action<Book, string, IReadOnlyList<Book>>? CreateSeriesRequested;
        public event Action<Book>? RemoveBookFromSeriesRequested;
        public event Action<Series, string>? RenameSeriesRequested;
        public event Action<Series>? DisbandSeriesRequested;

        public string SearchText => SearchBox.Text?.Trim() ?? "";

        public IReadOnlyCollection<string> SelectedTags => _selectedTags.ToList();

        public ProgressFilter ProgressFilter => _progressFilter;

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            ImportRequested?.Invoke(this, EventArgs.Empty);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterChanged?.Invoke();
        }

        private void TagFilterButton_Click(object sender, RoutedEventArgs e)
        {
        }

        private void TagFlyoutDone_Click(object sender, RoutedEventArgs e)
        {
            TagFlyout.Hide();
        }

        private void TagFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressFilterEvents)
                return;

            _selectedTags.Clear();
            foreach (var item in TagFilterList.SelectedItems)
            {
                if (item is string tag)
                    _selectedTags.Add(tag);
            }

            UpdateFilterUi();
            FilterChanged?.Invoke();
        }

        private void ProgressFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked)
                return;

            ProgressFilter selected = ProgressFilter.None;

            if (clicked == StartedFilterButton)
                selected = ProgressFilter.Started;
            else if (clicked == NotStartedFilterButton)
                selected = ProgressFilter.NotStarted;
            else if (clicked == CompletedFilterButton)
                selected = ProgressFilter.Completed;

            if (_progressFilter == selected && clicked.IsChecked == false)
                _progressFilter = ProgressFilter.None;
            else
                _progressFilter = selected;

            StartedFilterButton.IsChecked = _progressFilter == ProgressFilter.Started;
            NotStartedFilterButton.IsChecked = _progressFilter == ProgressFilter.NotStarted;
            CompletedFilterButton.IsChecked = _progressFilter == ProgressFilter.Completed;

            UpdateFilterUi();
            FilterChanged?.Invoke();
        }

        private void ClearFilters_Click(object sender, RoutedEventArgs e)
        {
            _selectedTags.Clear();
            _progressFilter = ProgressFilter.None;

            _suppressFilterEvents = true;
            TagFilterList.SelectedItems.Clear();
            _suppressFilterEvents = false;

            StartedFilterButton.IsChecked = false;
            NotStartedFilterButton.IsChecked = false;
            CompletedFilterButton.IsChecked = false;

            UpdateFilterUi();
            TagFlyout.Hide();
            FilterFlyout.Hide();
            FilterChanged?.Invoke();
        }

        public void SetAvailableTags(IEnumerable<string> tags)
        {
            var tagList = tags.ToList();
            _availableTags.Clear();
            _availableTags.AddRange(tagList);

            var previouslySelected = _selectedTags.ToList();

            _suppressFilterEvents = true;
            TagFilterList.ItemsSource = tagList;

            TagFilterList.SelectedItems.Clear();
            foreach (var tag in previouslySelected)
            {
                var match = tagList.FirstOrDefault(t =>
                    string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    TagFilterList.SelectedItems.Add(match);
            }

            _suppressFilterEvents = false;
            UpdateFilterUi();
        }

        private void UpdateFilterUi()
        {
            var activeCount = _selectedTags.Count + (_progressFilter != ProgressFilter.None ? 1 : 0);
            FilterButtonText.Text = activeCount > 0 ? $"Filter ({activeCount})" : "Filter";

            TagFilterButton.Content = _selectedTags.Count > 0
                ? $"Filter by tag ({_selectedTags.Count})"
                : "Filter by tag";

            var parts = new List<string>();

            if (_selectedTags.Count > 0)
                parts.Add("Tags: " + string.Join(", ", _selectedTags.OrderBy(t => t)));

            if (_progressFilter != ProgressFilter.None)
            {
                parts.Add(_progressFilter switch
                {
                    ProgressFilter.Started => "Started",
                    ProgressFilter.NotStarted => "Not started",
                    ProgressFilter.Completed => "Completed",
                    _ => ""
                });
            }

            if (parts.Count == 0)
            {
                ActiveFiltersText.Visibility = Visibility.Collapsed;
                ActiveFiltersText.Text = "";
            }
            else
            {
                ActiveFiltersText.Visibility = Visibility.Visible;
                ActiveFiltersText.Text = string.Join(" · ", parts);
            }
        }

        public void SetLibraryContext(IEnumerable<Book> allBooks, IEnumerable<Series> allSeries)
        {
            _allBooks = allBooks.ToList();
            _allSeries = allSeries.ToList();
        }

        public void SetLibraryItems(IEnumerable<LibraryItem> items)
        {
            _displayItems.Clear();

            foreach (var item in items)
                _displayItems.Add(new LibraryDisplayItem(item));

            _ = LoadCoversAsync(_displayItems.ToList());
        }

        private async void BooksList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LibraryDisplayItem item)
                await HandleItemClickAsync(item);
        }

        private async void BooksGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LibraryDisplayItem item)
                await HandleItemClickAsync(item);
        }

        private async Task HandleItemClickAsync(LibraryDisplayItem item)
        {
            if (item.IsSeries)
            {
                await ShowSeriesBooksDialogAsync(item);
                return;
            }

            if (item.Book != null)
                BookSelected?.Invoke(item.Book);
        }

        private async Task ShowSeriesBooksDialogAsync(LibraryDisplayItem item)
        {
            if (item.Series == null)
                return;

            var rows = new ObservableCollection<SeriesMemberRow>();
            foreach (var book in item.Members)
                rows.Add(new SeriesMemberRow(book));

            var list = new ListView
            {
                ItemsSource = rows,
                SelectionMode = ListViewSelectionMode.Single,
                IsItemClickEnabled = true,
                Height = 360,
                Width = 420
            };

            list.ItemTemplate = CreateSeriesMemberTemplate();

            var title = new TextBlock
            {
                Text = item.Series.Name,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var content = new StackPanel
            {
                Spacing = 0,
                Children = { title, list }
            };

            var dialog = new ContentDialog
            {
                Title = "",
                Content = content,
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            };

            list.ItemClick += (_, args) =>
            {
                if (args.ClickedItem is SeriesMemberRow row)
                {
                    dialog.Hide();
                    BookSelected?.Invoke(row.Book);
                }
            };

            _ = LoadSeriesMemberCoversAsync(rows);
            await dialog.ShowAsync();
        }

        private static DataTemplate CreateSeriesMemberTemplate()
        {
            // Built in code to avoid extra XAML resources for the dialog.
            var xaml =
                @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                    <Grid Padding='8' ColumnSpacing='12'>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width='48'/>
                            <ColumnDefinition Width='*'/>
                            <ColumnDefinition Width='Auto'/>
                        </Grid.ColumnDefinitions>
                        <Border Width='48' Height='48' CornerRadius='4'
                                Background='{ThemeResource CardBackgroundFillColorDefaultBrush}'>
                            <Image Source='{Binding CoverImage}' Stretch='UniformToFill'/>
                        </Border>
                        <StackPanel Grid.Column='1' VerticalAlignment='Center' Spacing='2'>
                            <TextBlock Text='{Binding Title}' TextTrimming='CharacterEllipsis'/>
                            <TextBlock Text='{Binding Author}' Opacity='0.7' TextTrimming='CharacterEllipsis'/>
                        </StackPanel>
                        <TextBlock Grid.Column='2' Text='{Binding ProgressText}'
                                   VerticalAlignment='Center' Opacity='0.7'/>
                    </Grid>
                  </DataTemplate>";

            return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
        }

        private void ItemContextFlyout_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout flyout)
                return;

            var item = (flyout.Target as FrameworkElement)?.DataContext as LibraryDisplayItem;
            if (item == null)
                return;

            foreach (var menuItem in flyout.Items.OfType<MenuFlyoutItem>())
            {
                var tag = menuItem.Tag as string ?? menuItem.Text;

                if (item.IsSeries)
                {
                    menuItem.Visibility = tag is "RenameSeries" or "DisbandSeries"
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
                else
                {
                    var inSeries = FindSeriesForBook(item.Book) != null;
                    menuItem.Visibility = tag switch
                    {
                        "RenameSeries" or "DisbandSeries" => Visibility.Collapsed,
                        "RemoveFromSeries" => inSeries ? Visibility.Visible : Visibility.Collapsed,
                        "AddToSeries" => !inSeries && _allSeries.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
                        "CreateSeries" => Visibility.Visible,
                        "RemoveBook" or "Rate" or "AddTags" or "Add tags" or "Rate" => Visibility.Visible,
                        _ => Visibility.Visible
                    };

                    // Rate / Add tags always for books
                    if (string.Equals(menuItem.Text, "Rate", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(menuItem.Text, "Add tags", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(menuItem.Text, "Remove", StringComparison.OrdinalIgnoreCase))
                    {
                        menuItem.Visibility = Visibility.Visible;
                    }
                }
            }
        }

        private Series? FindSeriesForBook(Book? book)
        {
            if (book == null)
                return null;

            return _allSeries.FirstOrDefault(s => s.BookIds.Contains(book.Id));
        }

        private void RemoveBook_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LibraryDisplayItem item &&
                item.Book != null)
            {
                RemoveRequested?.Invoke(item.Book);
            }
        }

        private async void AddTags_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryDisplayItem item ||
                item.Book == null)
                return;

            await ShowAddTagsDialogAsync(item);
        }

        private async void RateBook_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryDisplayItem item ||
                item.Book == null)
                return;

            await ShowRateBookDialogAsync(item);
        }

        private async void AddToSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryDisplayItem item ||
                item.Book == null)
                return;

            if (_allSeries.Count == 0)
                return;

            var combo = new ComboBox
            {
                Header = "Series",
                Width = 320,
                ItemsSource = _allSeries.Select(s => s.Name).ToList(),
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

            var series = _allSeries.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (series == null)
                return;

            AddBookToSeriesRequested?.Invoke(item.Book, series);
        }

        private async void CreateSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryDisplayItem item ||
                item.Book == null)
                return;

            var nameBox = new TextBox
            {
                Header = "Series name",
                PlaceholderText = "e.g. Classroom of the Elite"
            };

            var candidates = _allBooks
                .Where(b => b.Id != item.Book.Id && FindSeriesForBook(b) == null)
                .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var picker = new ListView
            {
                Header = "Also include (optional)",
                SelectionMode = ListViewSelectionMode.Multiple,
                Height = 220,
                ItemsSource = candidates.Select(b => new BookPickRow(b)).ToList()
            };
            picker.ItemTemplate = CreateBookPickTemplate();

            var content = new StackPanel
            {
                Spacing = 12,
                Width = 360,
                Children = { nameBox, picker }
            };

            var dialog = new ContentDialog
            {
                Title = "Create series",
                Content = content,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            var name = nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            var extra = picker.SelectedItems
                .OfType<BookPickRow>()
                .Select(r => r.Book)
                .ToList();

            CreateSeriesRequested?.Invoke(item.Book, name, extra);
        }

        private void RemoveFromSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LibraryDisplayItem item &&
                item.Book != null)
            {
                RemoveBookFromSeriesRequested?.Invoke(item.Book);
            }
        }

        private async void RenameSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryDisplayItem item ||
                item.Series == null)
                return;

            var nameBox = new TextBox
            {
                Header = "Series name",
                Text = item.Series.Name,
                Width = 320
            };

            var dialog = new ContentDialog
            {
                Title = "Rename series",
                Content = nameBox,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            var name = nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            RenameSeriesRequested?.Invoke(item.Series, name);
        }

        private void DisbandSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LibraryDisplayItem item &&
                item.Series != null)
            {
                DisbandSeriesRequested?.Invoke(item.Series);
            }
        }

        private static DataTemplate CreateBookPickTemplate()
        {
            var xaml =
                @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                    <StackPanel Padding='4'>
                        <TextBlock Text='{Binding Title}' TextTrimming='CharacterEllipsis'/>
                        <TextBlock Text='{Binding Author}' Opacity='0.7' FontSize='12' TextTrimming='CharacterEllipsis'/>
                    </StackPanel>
                  </DataTemplate>";
            return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
        }

        private async Task ShowRateBookDialogAsync(LibraryDisplayItem item)
        {
            var book = item.Book!;

            var ratingBox = new NumberBox
            {
                Header = "Rating (0 – 5)",
                Minimum = 0,
                Maximum = 5,
                SmallChange = 0.1,
                LargeChange = 0.5,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Value = book.Rating ?? double.NaN,
                PlaceholderText = "Not rated"
            };

            var preview = new StarRatingControl
            {
                StarSize = 22,
                Margin = new Thickness(0, 8, 0, 0)
            };
            preview.SetRating(book.Rating);

            ratingBox.ValueChanged += (_, args) =>
            {
                if (double.IsNaN(args.NewValue))
                    preview.SetRating(null);
                else
                    preview.SetRating(Math.Clamp(args.NewValue, 0, 5));
            };

            var clearButton = new Button
            {
                Content = "Clear rating",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
            };
            clearButton.Click += (_, _) =>
            {
                ratingBox.Value = double.NaN;
                preview.SetRating(null);
            };

            var content = new StackPanel
            {
                Spacing = 8,
                Width = 280,
                Children =
                {
                    new TextBlock
                    {
                        Text = book.Title,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        Opacity = 0.8
                    },
                    ratingBox,
                    preview,
                    clearButton
                }
            };

            var dialog = new ContentDialog
            {
                Title = "Rate book",
                Content = content,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            if (double.IsNaN(ratingBox.Value))
                book.Rating = null;
            else
                book.Rating = Math.Round(Math.Clamp(ratingBox.Value, 0, 5), 2);

            item.NotifyRatingChanged();
            RatingChanged?.Invoke(book);
        }

        private async Task ShowAddTagsDialogAsync(LibraryDisplayItem item)
        {
            var book = item.Book!;
            book.Tags ??= new List<string>();

            var suggestions = DefaultSuggestedTags
                .Concat(_availableTags)
                .Concat(book.Tags)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var tagList = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                Height = 220,
                ItemsSource = suggestions
            };

            foreach (var tag in book.Tags)
            {
                var match = suggestions.FirstOrDefault(t =>
                    string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    tagList.SelectedItems.Add(match);
            }

            var newTagBox = new TextBox
            {
                PlaceholderText = "New tag (e.g. LitRPG, Fiction)"
            };

            var addTagButton = new Button { Content = "Add" };
            addTagButton.Click += (_, _) =>
            {
                var tag = newTagBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(tag))
                    return;

                var currentlySelected = tagList.SelectedItems
                    .OfType<string>()
                    .Append(tag)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!suggestions.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                {
                    suggestions.Add(tag);
                    suggestions.Sort(StringComparer.OrdinalIgnoreCase);
                    tagList.ItemsSource = null;
                    tagList.ItemsSource = suggestions;
                }

                tagList.SelectedItems.Clear();
                foreach (var selected in currentlySelected)
                {
                    var match = suggestions.FirstOrDefault(t =>
                        string.Equals(t, selected, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        tagList.SelectedItems.Add(match);
                }

                newTagBox.Text = "";
            };

            var content = new StackPanel
            {
                Spacing = 12,
                Width = 320,
                Children =
                {
                    new TextBlock
                    {
                        Text = book.Title,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        Opacity = 0.8
                    },
                    new TextBlock { Text = "Select tags or type a new one." },
                    tagList,
                    new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                            new ColumnDefinition { Width = GridLength.Auto }
                        },
                        ColumnSpacing = 8,
                        Children = { newTagBox, addTagButton }
                    }
                }
            };

            Grid.SetColumn(addTagButton, 1);

            var dialog = new ContentDialog
            {
                Title = "Add tags",
                Content = content,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            book.Tags = tagList.SelectedItems
                .OfType<string>()
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            item.NotifyTagsChanged();
            TagsChanged?.Invoke(book);
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

        private static async Task LoadCoversAsync(IEnumerable<LibraryDisplayItem> items)
        {
            foreach (var item in items)
            {
                var paths = item.CoverPaths;
                if (paths.Count == 0)
                    continue;

                try
                {
                    item.CoverImage = await LoadThumbnailAsync(paths[0]);
                    if (paths.Count > 1)
                        item.CoverImage2 = await LoadThumbnailAsync(paths[1]);
                    if (paths.Count > 2)
                        item.CoverImage3 = await LoadThumbnailAsync(paths[2]);
                }
                catch
                {
                    // Keep placeholders when cover art is unavailable.
                }
            }
        }

        private static async Task LoadSeriesMemberCoversAsync(IEnumerable<SeriesMemberRow> rows)
        {
            foreach (var row in rows)
            {
                try
                {
                    row.CoverImage = await LoadThumbnailAsync(row.Book.FilePath);
                }
                catch
                {
                }
            }
        }

        private static async Task<ImageSource?> LoadThumbnailAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var file = await StorageFile.GetFileFromPathAsync(path);
            var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 300);
            if (thumbnail == null)
                return null;

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            return bitmap;
        }

        private sealed class BookPickRow
        {
            public BookPickRow(Book book) => Book = book;
            public Book Book { get; }
            public string Title => Book.Title;
            public string Author => Book.Author;
        }

        private sealed class SeriesMemberRow : INotifyPropertyChanged
        {
            private ImageSource? _coverImage;

            public SeriesMemberRow(Book book) => Book = book;

            public event PropertyChangedEventHandler? PropertyChanged;

            public Book Book { get; }
            public string Title => Book.Title;
            public string Author => Book.Author;
            public string ProgressText =>
                Book.ProgressPercent > 0 ? $"{Book.ProgressPercent:0}%" : "";

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

        private sealed class LibraryDisplayItem : INotifyPropertyChanged
        {
            private ImageSource? _coverImage;
            private ImageSource? _coverImage2;
            private ImageSource? _coverImage3;

            public LibraryDisplayItem(LibraryItem item)
            {
                Source = item;
                Book = item.Book;
                Series = item.Series;
                IsSeries = item.IsSeries;
                Members = item.Members;

                if (IsSeries && Series != null)
                {
                    Title = Series.Name;
                    Subtitle = Members.Count == 1 ? "1 book" : $"{Members.Count} books";
                    CoverPaths = Members.Take(3)
                        .Select(m => m.FilePath)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .ToList();
                }
                else if (Book != null)
                {
                    Title = Book.Title;
                    Subtitle = Book.Author;
                    CoverPaths = string.IsNullOrWhiteSpace(Book.FilePath)
                        ? new List<string>()
                        : new List<string> { Book.FilePath };
                }
                else
                {
                    Title = "";
                    Subtitle = "";
                    CoverPaths = new List<string>();
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public LibraryItem Source { get; }
            public Book? Book { get; }
            public Series? Series { get; }
            public bool IsSeries { get; }
            public IReadOnlyList<Book> Members { get; }
            public IReadOnlyList<string> CoverPaths { get; }

            public string Title { get; }
            public string Subtitle { get; }

            public double RatingValue => Book?.Rating ?? double.NaN;

            public string TagsDisplay =>
                Book?.Tags is { Count: > 0 }
                    ? string.Join(" · ", Book.Tags)
                    : "";

            public double ProgressValue => Book?.ProgressPercent ?? 0;

            public string ProgressText =>
                !IsSeries && Book != null && Book.ProgressPercent > 0
                    ? $"{Book.ProgressPercent:0}%"
                    : "";

            public Visibility ProgressVisibility =>
                !IsSeries && Book != null && Book.ProgressPercent > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            public Visibility RatingVisibility =>
                IsSeries ? Visibility.Collapsed : Visibility.Visible;

            public Visibility BookVisibility =>
                IsSeries ? Visibility.Collapsed : Visibility.Visible;

            public Visibility StackVisibility =>
                IsSeries && CoverPaths.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            public Visibility MidStackVisibility =>
                IsSeries && CoverPaths.Count > 2 ? Visibility.Visible : Visibility.Collapsed;

            public ImageSource? CoverImage
            {
                get => _coverImage;
                set
                {
                    _coverImage = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
                }
            }

            public ImageSource? CoverImage2
            {
                get => _coverImage2;
                set
                {
                    _coverImage2 = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage2)));
                }
            }

            public ImageSource? CoverImage3
            {
                get => _coverImage3;
                set
                {
                    _coverImage3 = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage3)));
                }
            }

            public void NotifyTagsChanged()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagsDisplay)));
            }

            public void NotifyRatingChanged()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RatingValue)));
            }
        }
    }
}
