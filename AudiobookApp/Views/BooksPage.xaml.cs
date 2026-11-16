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

        private readonly ObservableCollection<BookDisplayItem> _displayBooks = new();
        private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _availableTags = new();
        private ProgressFilter _progressFilter = ProgressFilter.None;
        private bool _suppressFilterEvents;

        public BooksPage()
        {
            InitializeComponent();
            BooksList.ItemsSource = _displayBooks;
            BooksGrid.ItemsSource = _displayBooks;
        }

        public event EventHandler? ImportRequested;
        public event Action? FilterChanged;
        public event Action<Book>? RemoveRequested;
        public event Action<Book>? BookSelected;
        public event Action<Book>? TagsChanged;
        public event Action<Book>? RatingChanged;

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
            // Nested flyout opens via Button.Flyout.
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

            // Clicking the active filter again clears it.
            if (_progressFilter == selected && clicked.IsChecked == false)
            {
                _progressFilter = ProgressFilter.None;
            }
            else
            {
                _progressFilter = selected;
            }

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

        private void BooksList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BookDisplayItem item)
                BookSelected?.Invoke(item.Book);
        }

        private void BooksGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BookDisplayItem item)
                BookSelected?.Invoke(item.Book);
        }

        private void RemoveBook_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is BookDisplayItem item)
                RemoveRequested?.Invoke(item.Book);
        }

        private async void AddTags_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not BookDisplayItem item)
                return;

            await ShowAddTagsDialogAsync(item);
        }

        private async void RateBook_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not BookDisplayItem item)
                return;

            await ShowRateBookDialogAsync(item);
        }

        private async Task ShowRateBookDialogAsync(BookDisplayItem item)
        {
            var book = item.Book;

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

        private async Task ShowAddTagsDialogAsync(BookDisplayItem item)
        {
            var book = item.Book;
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

        public void SetBooks(IEnumerable<Book> books)
        {
            _displayBooks.Clear();

            foreach (var book in books)
                _displayBooks.Add(new BookDisplayItem(book));

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
                    var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 300);

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

            /// <summary>
            /// NaN means unrated so WinUI can bind to a non-nullable double DP.
            /// </summary>
            public double RatingValue => Book.Rating ?? double.NaN;

            public string TagsDisplay =>
                Book.Tags is { Count: > 0 }
                    ? string.Join(" · ", Book.Tags)
                    : "";

            public ImageSource? CoverImage
            {
                get => _coverImage;
                set
                {
                    _coverImage = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
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
