using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AudiobookApp.Models;

namespace AudiobookApp.Views
{
    public sealed partial class DirectorySeriesReviewPage : Page
    {
        private readonly ObservableCollection<SeriesRow> _seriesRows = new();
        private readonly List<ImportBookCandidate> _allBooks;

        public event Action<IReadOnlyList<ImportSeriesCandidate>>? Confirmed;
        public event Action? Skipped;
        public event Action? CancelRequested;

        public DirectorySeriesReviewPage(
            IEnumerable<ImportSeriesCandidate> proposedSeries,
            IEnumerable<ImportBookCandidate> allBooks)
        {
            InitializeComponent();
            _allBooks = allBooks.ToList();

            foreach (var series in proposedSeries)
                _seriesRows.Add(new SeriesRow(series));

            SeriesList.ItemsSource = _seriesRows;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            var selected = _seriesRows.Count(s => s.IsSelected && s.BookRows.Count > 0);
            SummaryText.Text =
                $"{_seriesRows.Count} proposed series · {selected} selected. Check books to remove them, or remove a whole series.";
        }

        private void RemoveSelectedBooks_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not SeriesRow seriesRow)
                return;

            var toRemove = seriesRow.BookRows.Where(b => b.IsChecked).ToList();
            if (toRemove.Count == 0)
                return;

            foreach (var book in toRemove)
                seriesRow.BookRows.Remove(book);

            UpdateSummary();
        }

        private void RemoveSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not SeriesRow seriesRow)
                return;

            _seriesRows.Remove(seriesRow);
            UpdateSummary();
        }

        private async void AddBookToSeries_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not SeriesRow seriesRow)
                return;

            var assignedElsewhere = _seriesRows
                .Where(s => !ReferenceEquals(s, seriesRow))
                .SelectMany(s => s.BookRows.Select(b => b.Candidate))
                .ToHashSet();

            var available = _allBooks
                .Where(b => !assignedElsewhere.Contains(b))
                .Where(b => !seriesRow.BookRows.Any(r => ReferenceEquals(r.Candidate, b)))
                .Select(b => new BookPick(b))
                .ToList();

            if (available.Count == 0)
            {
                await new ContentDialog
                {
                    Title = "No books available",
                    Content = "All imported books are already assigned to a series, or none remain to add.",
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                }.ShowAsync();
                return;
            }

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                ItemsSource = available,
                DisplayMemberPath = nameof(BookPick.Display),
                MaxHeight = 360
            };

            var dialog = new ContentDialog
            {
                Title = $"Add books to {seriesRow.Name}",
                Content = list,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            foreach (var item in list.SelectedItems.OfType<BookPick>())
                seriesRow.BookRows.Add(new BookRow(item.Candidate));

            UpdateSummary();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            var confirmed = _seriesRows
                .Where(s => s.IsSelected && s.BookRows.Count > 0)
                .Select(s =>
                {
                    s.Candidate.Name = s.Name;
                    s.Candidate.IsSelected = true;
                    s.Candidate.Books = s.BookRows.Select(b => b.Candidate).ToList();
                    return s.Candidate;
                })
                .ToList();

            Confirmed?.Invoke(confirmed);
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Skipped?.Invoke();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke();
        }

        private sealed class SeriesRow : INotifyPropertyChanged
        {
            private bool _isSelected;
            private string _name;

            public SeriesRow(ImportSeriesCandidate candidate)
            {
                Candidate = candidate;
                _isSelected = candidate.IsSelected;
                _name = candidate.Name;
                BookRows = new ObservableCollection<BookRow>(
                    candidate.Books.Select(b => new BookRow(b)));
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            public ImportSeriesCandidate Candidate { get; }
            public ObservableCollection<BookRow> BookRows { get; }
            public string ReasonLabel => Candidate.ReasonLabel;

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    Candidate.IsSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public string Name
            {
                get => _name;
                set
                {
                    if (_name == value) return;
                    _name = value;
                    Candidate.Name = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
                }
            }
        }

        private sealed class BookRow : INotifyPropertyChanged
        {
            private bool _isChecked;

            public BookRow(ImportBookCandidate candidate)
            {
                Candidate = candidate;
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            public ImportBookCandidate Candidate { get; }
            public string Title => Candidate.Title;
            public string Author => Candidate.Author;

            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value) return;
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }

        private sealed class BookPick
        {
            public BookPick(ImportBookCandidate candidate)
            {
                Candidate = candidate;
            }

            public ImportBookCandidate Candidate { get; }
            public string Display => $"{Candidate.Title} — {Candidate.Author}";
        }
    }
}
