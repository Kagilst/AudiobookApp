using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using AudiobookApp.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI;

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
        private enum LibraryTypeFilter { All, SeriesOnly, SinglesOnly }
        private LibraryTypeFilter _libraryTypeFilter = LibraryTypeFilter.All;
        private bool _suppressFilterEvents;

        private IReadOnlyList<Series> _allSeries = Array.Empty<Series>();
        private IReadOnlyList<Book> _allBooks = Array.Empty<Book>();

        private bool _seriesSelectMode;
        private Book? _seriesSelectSeed;
        private readonly HashSet<Guid> _seriesSelectIds = new();

        public BooksPage()
        {
            InitializeComponent();
            BooksList.ItemsSource = _displayItems;
            BooksGrid.ItemsSource = _displayItems;
            SetDisplayMode(useIconView: true);
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
        public event Action<Book, IReadOnlyList<Book>>? CreateSeriesFlowRequested;
        public event Action<Book>? RemoveBookFromSeriesRequested;
        public event Action<Series, string>? RenameSeriesRequested;
        public event Action<Series>? DisbandSeriesRequested;
        public event Action<Series, IReadOnlyList<Book>>? ReorderSeriesRequested;
        public event Action<Book>? BookEdited;

        public string SearchText => SearchBox.Text?.Trim() ?? "";

        public IReadOnlyCollection<string> SelectedTags => _selectedTags.ToList();

        public ProgressFilter ProgressFilter => _progressFilter;

        public bool SeriesOnlyFilter => _libraryTypeFilter == LibraryTypeFilter.SeriesOnly;

        public bool SinglesOnlyFilter => _libraryTypeFilter == LibraryTypeFilter.SinglesOnly;

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

        private void LibraryTypeFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked)
                return;

            if (clicked == SeriesFilterButton)
            {
                if (SeriesFilterButton.IsChecked == true)
                {
                    SinglesFilterButton.IsChecked = false;
                    _libraryTypeFilter = LibraryTypeFilter.SeriesOnly;
                }
                else
                {
                    _libraryTypeFilter = LibraryTypeFilter.All;
                }
            }
            else if (clicked == SinglesFilterButton)
            {
                if (SinglesFilterButton.IsChecked == true)
                {
                    SeriesFilterButton.IsChecked = false;
                    _libraryTypeFilter = LibraryTypeFilter.SinglesOnly;
                }
                else
                {
                    _libraryTypeFilter = LibraryTypeFilter.All;
                }
            }

            UpdateFilterUi();
            FilterChanged?.Invoke();
        }

        private void ClearFilters_Click(object sender, RoutedEventArgs e)
        {
            _selectedTags.Clear();
            _progressFilter = ProgressFilter.None;
            _libraryTypeFilter = LibraryTypeFilter.All;

            _suppressFilterEvents = true;
            TagFilterList.SelectedItems.Clear();
            _suppressFilterEvents = false;

            StartedFilterButton.IsChecked = false;
            NotStartedFilterButton.IsChecked = false;
            CompletedFilterButton.IsChecked = false;
            SeriesFilterButton.IsChecked = false;
            SinglesFilterButton.IsChecked = false;

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
            var activeCount = _selectedTags.Count
                + (_progressFilter != ProgressFilter.None ? 1 : 0)
                + (_libraryTypeFilter != LibraryTypeFilter.All ? 1 : 0);
            FilterButtonText.Text = activeCount > 0 ? $"Filter ({activeCount})" : "Filter";

            TagFilterButton.Content = _selectedTags.Count > 0
                ? $"Filter by tag ({_selectedTags.Count})"
                : "Filter by tag";

            var parts = new List<string>();

            if (_selectedTags.Count > 0)
                parts.Add("Tags: " + string.Join(", ", _selectedTags.OrderBy(t => t)));

            if (_libraryTypeFilter == LibraryTypeFilter.SeriesOnly)
                parts.Add("Series only");
            else if (_libraryTypeFilter == LibraryTypeFilter.SinglesOnly)
                parts.Add("Singles only");

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
            {
                var display = new LibraryDisplayItem(item);
                if (_seriesSelectMode &&
                    display.Book != null &&
                    _seriesSelectIds.Contains(display.Book.Id))
                {
                    display.IsSelectedForSeries = true;
                }

                _displayItems.Add(display);
            }

            _ = LoadCoversAsync(_displayItems.ToList());
            UpdateSeriesSelectUi();
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
            if (_seriesSelectMode)
            {
                ToggleSeriesSelect(item);
                return;
            }

            if (item.IsSeries)
            {
                await ShowSeriesBooksDialogAsync(item);
                return;
            }

            if (item.Book != null)
                BookSelected?.Invoke(item.Book);
        }

        private void ToggleSeriesSelect(LibraryDisplayItem item)
        {
            if (item.IsSeries || item.Book == null)
                return;

            // Keep the seed book always selected.
            if (_seriesSelectSeed != null && item.Book.Id == _seriesSelectSeed.Id)
                return;

            // Only books that are not already in a series can be added.
            if (FindSeriesForBook(item.Book) != null)
                return;

            if (_seriesSelectIds.Contains(item.Book.Id))
            {
                _seriesSelectIds.Remove(item.Book.Id);
                item.IsSelectedForSeries = false;
            }
            else
            {
                _seriesSelectIds.Add(item.Book.Id);
                item.IsSelectedForSeries = true;
            }

            UpdateSeriesSelectUi();
        }

        private void EnterSeriesSelectMode(Book seed)
        {
            _seriesSelectMode = true;
            _seriesSelectSeed = seed;
            _seriesSelectIds.Clear();
            _seriesSelectIds.Add(seed.Id);

            foreach (var item in _displayItems)
            {
                item.IsSelectedForSeries =
                    item.Book != null && _seriesSelectIds.Contains(item.Book.Id);
            }

            UpdateSeriesSelectUi();
        }

        private void ExitSeriesSelectMode()
        {
            _seriesSelectMode = false;
            _seriesSelectSeed = null;
            _seriesSelectIds.Clear();

            foreach (var item in _displayItems)
                item.IsSelectedForSeries = false;

            UpdateSeriesSelectUi();
        }

        private void UpdateSeriesSelectUi()
        {
            if (SeriesSelectBar == null)
                return;

            SeriesSelectBar.Visibility = _seriesSelectMode ? Visibility.Visible : Visibility.Collapsed;
            if (!_seriesSelectMode)
                return;

            var count = _seriesSelectIds.Count;
            SeriesSelectStatusText.Text = count < 2
                ? $"{count} book selected. Click at least one more book, then confirm."
                : $"{count} books selected. Click books to add or remove them, then confirm.";
            ConfirmSeriesSelectButton.IsEnabled = count >= 2;
        }

        private void CancelSeriesSelect_Click(object sender, RoutedEventArgs e)
        {
            ExitSeriesSelectMode();
        }

        private void ConfirmSeriesSelect_Click(object sender, RoutedEventArgs e)
        {
            if (_seriesSelectSeed == null || _seriesSelectIds.Count < 2)
                return;

            var seed = _seriesSelectSeed;
            var extra = _allBooks
                .Where(b => _seriesSelectIds.Contains(b.Id) && b.Id != seed.Id)
                .ToList();

            ExitSeriesSelectMode();
            CreateSeriesFlowRequested?.Invoke(seed, extra);
        }

        private ContentDialog? _activeSeriesDialog;

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
                Width = 520,
                ItemTemplate = (DataTemplate)Resources["SeriesMemberTemplate"]
            };

            var title = new TextBlock
            {
                Text = item.Series.Name,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var seriesRating = new StarRatingControl
            {
                StarSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            seriesRating.SetRating(GetSeriesAverageRating(item.Members));

            var content = new StackPanel
            {
                Spacing = 0,
                Children = { title, seriesRating, list }
            };

            var dialog = new ContentDialog
            {
                Title = "",
                Content = content,
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            };
            _activeSeriesDialog = dialog;

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
            _activeSeriesDialog = null;
        }

        private static double? GetSeriesAverageRating(IEnumerable<Book> members)
        {
            var rated = members
                .Where(b => b.Rating.HasValue)
                .Select(b => b.Rating!.Value)
                .ToList();

            if (rated.Count == 0)
                return null;

            return rated.Average();
        }

        private async void SeriesMemberRate_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SeriesMemberRow row)
                return;

            var book = row.Book;
            var series = FindSeriesForBook(book);
            _activeSeriesDialog?.Hide();

            await ShowRateBookDialogAsync(book, () => RatingChanged?.Invoke(book));

            if (series != null)
                await ReopenSeriesDialogAsync(series);
        }

        private async void SeriesMemberAddTags_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SeriesMemberRow row)
                return;

            var book = row.Book;
            var series = FindSeriesForBook(book);
            _activeSeriesDialog?.Hide();

            await ShowAddTagsDialogAsync(book, () => TagsChanged?.Invoke(book));

            if (series != null)
                await ReopenSeriesDialogAsync(series);
        }

        private async void SeriesMemberEdit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SeriesMemberRow row)
                return;

            var book = row.Book;
            var series = FindSeriesForBook(book);
            _activeSeriesDialog?.Hide();

            await ShowEditBookDialogAsync(book);

            if (series != null)
                await ReopenSeriesDialogAsync(series);
        }

        private async void SeriesMemberRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SeriesMemberRow row)
                return;

            var book = row.Book;
            var series = FindSeriesForBook(book);
            _activeSeriesDialog?.Hide();

            var choice = await new ContentDialog
            {
                Title = "Remove book",
                Content = $"How do you want to remove \"{book.Title}\"?",
                PrimaryButtonText = "Remove from series",
                SecondaryButtonText = "Remove from library",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            }.ShowAsync();

            if (choice == ContentDialogResult.Primary)
            {
                RemoveBookFromSeriesRequested?.Invoke(book);
                if (series != null)
                    await ReopenSeriesDialogAsync(series);
            }
            else if (choice == ContentDialogResult.Secondary)
            {
                RemoveRequested?.Invoke(book);
            }
            else if (series != null)
            {
                await ReopenSeriesDialogAsync(series);
            }
        }

        private async Task ReopenSeriesDialogAsync(Series series)
        {
            // Let library refresh (from rating/tags save) finish first.
            await Task.Yield();

            var item = _displayItems.FirstOrDefault(i =>
                i.IsSeries && i.Series != null && i.Series.Id == series.Id);

            if (item == null)
            {
                var members = series.BookIds
                    .Select(id => _allBooks.FirstOrDefault(b => b.Id == id))
                    .Where(b => b != null)
                    .Cast<Book>()
                    .ToList();

                if (members.Count == 0)
                    return;

                item = new LibraryDisplayItem(new LibraryItem
                {
                    IsSeries = true,
                    Series = series,
                    Members = members
                });
            }

            await ShowSeriesBooksDialogAsync(item);
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
                    menuItem.Visibility = tag is "RenameSeries" or "DisbandSeries" or "ReorderSeries"
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
                else
                {
                    var inSeries = FindSeriesForBook(item.Book) != null;
                    menuItem.Visibility = tag switch
                    {
                        "RenameSeries" or "DisbandSeries" or "ReorderSeries" => Visibility.Collapsed,
                        "RemoveFromSeries" => inSeries ? Visibility.Visible : Visibility.Collapsed,
                        "AddToSeries" => !inSeries && _allSeries.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
                        "CreateSeries" => Visibility.Visible,
                        "EditBook" or "RemoveBook" or "Rate" or "AddTags" => Visibility.Visible,
                        _ => Visibility.Visible
                    };

                    if (string.Equals(menuItem.Text, "Rate", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(menuItem.Text, "Add tags", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(menuItem.Text, "Edit", StringComparison.OrdinalIgnoreCase) ||
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

        private async void EditBook_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryDisplayItem item ||
                item.Book == null)
                return;

            await ShowEditBookDialogAsync(item.Book);
        }

        private async Task ShowEditBookDialogAsync(Book book)
        {
            if (book.Id == Guid.Empty)
                book.Id = Guid.NewGuid();

            var title = book.Title;
            var author = book.Author;

            while (true)
            {
                var titleBox = new TextBox
                {
                    Header = "Title",
                    Text = title
                };

                var authorBox = new TextBox
                {
                    Header = "Author",
                    Text = author
                };

                var coverImage = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Width = 140,
                    Height = 140
                };

                var coverPlaceholder = new FontIcon
                {
                    Glyph = "\uE8D6",
                    FontSize = 36,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var coverGrid = new Grid
                {
                    Width = 140,
                    Height = 140,
                    Children = { coverPlaceholder, coverImage }
                };

                var coverBorder = new Border
                {
                    Width = 140,
                    Height = 140,
                    CornerRadius = new CornerRadius(8),
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = coverGrid
                };

                async Task RefreshCoverPreviewAsync()
                {
                    try
                    {
                        var source = await LoadThumbnailAsync(book.EffectiveCoverPath);
                        coverImage.Source = source;
                        coverPlaceholder.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
                    }
                    catch
                    {
                        coverImage.Source = null;
                        coverPlaceholder.Visibility = Visibility.Visible;
                    }
                }

                await RefreshCoverPreviewAsync();

                var changeCoverRequested = false;
                ContentDialog? dialog = null;

                var changeCoverButton = new Button
                {
                    Content = "Change cover image",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0)
                };

                changeCoverButton.Click += (_, _) =>
                {
                    changeCoverRequested = true;
                    dialog?.Hide();
                };

                var content = new StackPanel
                {
                    Spacing = 12,
                    Width = 320,
                    Children =
                    {
                        coverBorder,
                        changeCoverButton,
                        titleBox,
                        authorBox
                    }
                };

                dialog = new ContentDialog
                {
                    Title = "Edit book",
                    Content = content,
                    PrimaryButtonText = "Save",
                    CloseButtonText = "Cancel",
                    XamlRoot = XamlRoot
                };

                var result = await dialog.ShowAsync();
                title = titleBox.Text ?? title;
                author = authorBox.Text ?? author;

                if (changeCoverRequested)
                {
                    if (AudiobookApp.App.MainWindow != null)
                    {
                        var path = await Services.CoverImageService.PickAndStoreCoverAsync(
                            AudiobookApp.App.MainWindow,
                            book.Id);
                        if (!string.IsNullOrWhiteSpace(path))
                            book.CoverImagePath = path;
                    }

                    continue;
                }

                if (result != ContentDialogResult.Primary)
                    return;

                book.Title = string.IsNullOrWhiteSpace(title) ? book.Title : title.Trim();
                book.Author = string.IsNullOrWhiteSpace(author) ? book.Author : author.Trim();
                BookEdited?.Invoke(book);
                return;
            }
        }

        private async void AddTags_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryDisplayItem item ||
                item.Book == null)
                return;

            await ShowAddTagsDialogAsync(item.Book, () =>
            {
                item.NotifyTagsChanged();
                TagsChanged?.Invoke(item.Book);
            });
        }

        private async void RateBook_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryDisplayItem item ||
                item.Book == null)
                return;

            await ShowRateBookDialogAsync(item.Book, () =>
            {
                item.NotifyRatingChanged();
                RatingChanged?.Invoke(item.Book);
            });
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

        private void CreateSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryDisplayItem item ||
                item.Book == null)
                return;

            EnterSeriesSelectMode(item.Book);
        }

        private void RemoveFromSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is LibraryDisplayItem item &&
                item.Book != null)
            {
                RemoveBookFromSeriesRequested?.Invoke(item.Book);
            }
        }

        private void ReorderSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LibraryDisplayItem item ||
                item.Series == null ||
                item.Members.Count == 0)
                return;

            ReorderSeriesRequested?.Invoke(item.Series, item.Members.ToList());
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

        private async Task ShowRateBookDialogAsync(Book book, Action onSaved)
        {
            var ratingBox = new NumberBox
            {
                Header = "Rating (0.0 – 5.0)",
                Minimum = 0,
                Maximum = 5,
                SmallChange = 0.1,
                LargeChange = 0.5,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
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

            onSaved();
        }

        private async Task ShowAddTagsDialogAsync(Book book, Action onSaved)
        {
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

            onSaved();
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
                    row.CoverImage = await LoadThumbnailAsync(row.Book.EffectiveCoverPath);
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

            public double RatingValue => Book.Rating ?? double.NaN;

            public ImageSource? CoverImage
            {
                get => _coverImage;
                set
                {
                    _coverImage = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
                }
            }

            public void NotifyRatingChanged()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RatingValue)));
            }
        }

        private sealed class LibraryDisplayItem : INotifyPropertyChanged
        {
            private static readonly SolidColorBrush SelectedBorderBrush =
                new(Color.FromArgb(255, 0, 120, 212));
            private static readonly SolidColorBrush SelectedBackgroundBrush =
                new(Color.FromArgb(40, 0, 120, 212));
            private static readonly SolidColorBrush TransparentBrush =
                new(Colors.Transparent);

            private ImageSource? _coverImage;
            private ImageSource? _coverImage2;
            private ImageSource? _coverImage3;
            private bool _isSelectedForSeries;

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
                        .Select(m => m.EffectiveCoverPath)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .ToList();
                }
                else if (Book != null)
                {
                    Title = Book.Title;
                    Subtitle = Book.Author;
                    CoverPaths = string.IsNullOrWhiteSpace(Book.EffectiveCoverPath)
                        ? new List<string>()
                        : new List<string> { Book.EffectiveCoverPath };
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

            public bool IsSelectedForSeries
            {
                get => _isSelectedForSeries;
                set
                {
                    if (_isSelectedForSeries == value)
                        return;

                    _isSelectedForSeries = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedForSeries)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeriesSelectBorderThickness)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeriesSelectBorderBrush)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeriesSelectBackground)));
                }
            }

            public Thickness SeriesSelectBorderThickness =>
                IsSelectedForSeries ? new Thickness(3) : new Thickness(0);

            public Brush SeriesSelectBorderBrush =>
                IsSelectedForSeries ? SelectedBorderBrush : TransparentBrush;

            public Brush SeriesSelectBackground =>
                IsSelectedForSeries ? SelectedBackgroundBrush : TransparentBrush;

            public double RatingValue
            {
                get
                {
                    if (IsSeries)
                    {
                        var rated = Members
                            .Where(m => m.Rating.HasValue)
                            .Select(m => m.Rating!.Value)
                            .ToList();
                        return rated.Count == 0 ? double.NaN : rated.Average();
                    }

                    return Book?.Rating ?? double.NaN;
                }
            }

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

            public Visibility RatingVisibility => Visibility.Visible;

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
