using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using AudiobookApp.Models;
using AudiobookApp.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AudiobookApp.Views
{
    public sealed partial class DirectoryImportReviewPage : Page
    {
        private readonly ObservableCollection<CandidateRow> _allRows = new();
        private readonly ObservableCollection<CandidateRow> _visibleRows = new();
        private enum ReviewFilter { All, MergedOnly, SingularOnly, EpubOnly }
        private ReviewFilter _filter = ReviewFilter.All;

        public event Action<IReadOnlyList<ImportBookCandidate>>? ImportConfirmed;
        public event Action? CancelRequested;

        public DirectoryImportReviewPage(IEnumerable<ImportBookCandidate> candidates)
        {
            InitializeComponent();

            foreach (var candidate in candidates)
            {
                var row = new CandidateRow(candidate);
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(CandidateRow.IsSelected))
                        UpdateSummary();
                };
                _allRows.Add(row);
            }

            CandidatesList.ItemsSource = _visibleRows;
            ApplyFilter();
            _ = LoadCoversAsync();
        }

        private void ApplyFilter()
        {
            _visibleRows.Clear();
            foreach (var row in _allRows.Where(MatchesFilter))
                _visibleRows.Add(row);
            UpdateSummary();
        }

        private bool MatchesFilter(CandidateRow row)
        {
            return _filter switch
            {
                ReviewFilter.EpubOnly => row.Candidate.MediaType == BookMediaType.Epub,
                ReviewFilter.MergedOnly =>
                    row.Candidate.MediaType == BookMediaType.Audiobook &&
                    row.Candidate.FilePaths.Count > 1,
                ReviewFilter.SingularOnly =>
                    row.Candidate.MediaType == BookMediaType.Audiobook &&
                    row.Candidate.FilePaths.Count == 1,
                _ => true
            };
        }

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked)
                return;

            if (clicked == MergedFilterButton)
            {
                if (MergedFilterButton.IsChecked == true)
                {
                    SingularFilterButton.IsChecked = false;
                    EpubFilterButton.IsChecked = false;
                    _filter = ReviewFilter.MergedOnly;
                }
                else
                {
                    _filter = ReviewFilter.All;
                }
            }
            else if (clicked == SingularFilterButton)
            {
                if (SingularFilterButton.IsChecked == true)
                {
                    MergedFilterButton.IsChecked = false;
                    EpubFilterButton.IsChecked = false;
                    _filter = ReviewFilter.SingularOnly;
                }
                else
                {
                    _filter = ReviewFilter.All;
                }
            }
            else if (clicked == EpubFilterButton)
            {
                if (EpubFilterButton.IsChecked == true)
                {
                    MergedFilterButton.IsChecked = false;
                    SingularFilterButton.IsChecked = false;
                    _filter = ReviewFilter.EpubOnly;
                }
                else
                {
                    _filter = ReviewFilter.All;
                }
            }

            ApplyFilter();
        }

        private void UpdateSummary()
        {
            var total = _allRows.Count;
            var selected = _allRows.Count(r => r.IsSelected);
            var multi = _allRows.Count(r => r.Candidate.FilePaths.Count > 1);
            SummaryText.Text =
                $"{total} books detected ({multi} multi-file). Select what to import, edit titles, merge or split groups.";
            SelectionStatusText.Text = $"{selected} selected · {_visibleRows.Count} shown";
            ImportButton.IsEnabled = selected > 0;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _visibleRows)
                row.IsSelected = true;
            UpdateSummary();
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _visibleRows)
                row.IsSelected = false;
            UpdateSummary();
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var toRemove = _visibleRows.Where(r => r.IsSelected).ToList();
            if (toRemove.Count == 0)
                return;

            foreach (var row in toRemove)
                _allRows.Remove(row);

            ApplyFilter();
        }

        private async void ViewFiles_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not CandidateRow row)
                return;

            var names = string.Join(
                Environment.NewLine,
                row.Candidate.FilePaths.Select(p => Path.GetFileName(p)));

            await new ContentDialog
            {
                Title = row.Title,
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = names,
                        TextWrapping = TextWrapping.WrapWholeWords
                    },
                    MaxHeight = 360
                },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            }.ShowAsync();
        }

        private void RemoveCandidate_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not CandidateRow row)
                return;

            _allRows.Remove(row);
            ApplyFilter();
        }

        private void MergeSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _visibleRows.Where(r => r.IsSelected).ToList();
            if (selected.Count < 2)
                return;

            if (selected.Any(r => r.Candidate.MediaType == BookMediaType.Epub) &&
                selected.Any(r => r.Candidate.MediaType == BookMediaType.Audiobook))
            {
                _ = new ContentDialog
                {
                    Title = "Cannot merge",
                    Content = "Epubs and audiobooks cannot be merged into one book.",
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                }.ShowAsync();
                return;
            }

            var paths = selected
                .SelectMany(r => r.Candidate.FilePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => Path.GetFileName(p), Comparer<string>.Create(DirectoryBookImporter.CompareNatural))
                .ToList();

            var first = selected[0];
            var merged = new ImportBookCandidate
            {
                Title = first.Title,
                Author = first.Author,
                MediaType = first.Candidate.MediaType,
                FilePaths = paths,
                Reason = paths.Count > 1 ? ImportGroupReason.Folder : ImportGroupReason.SingleFile,
                IsSelected = true
            };

            var insertAt = _allRows.IndexOf(first);
            foreach (var row in selected)
                _allRows.Remove(row);

            var mergedRow = new CandidateRow(merged);
            mergedRow.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CandidateRow.IsSelected))
                    UpdateSummary();
            };

            if (insertAt < 0 || insertAt > _allRows.Count)
                _allRows.Add(mergedRow);
            else
                _allRows.Insert(insertAt, mergedRow);

            ApplyFilter();
            _ = LoadCoverAsync(mergedRow);
        }

        private void SplitSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _visibleRows
                .Where(r => r.IsSelected && r.Candidate.FilePaths.Count > 1)
                .ToList();

            if (selected.Count == 0)
                return;

            foreach (var row in selected.ToList())
            {
                var index = _allRows.IndexOf(row);
                _allRows.Remove(row);

                var inserts = row.Candidate.FilePaths
                    .OrderBy(p => Path.GetFileName(p), Comparer<string>.Create(DirectoryBookImporter.CompareNatural))
                    .Select(path =>
                    {
                        var created = new CandidateRow(new ImportBookCandidate
                        {
                            Title = Path.GetFileNameWithoutExtension(path),
                            Author = row.Author,
                            MediaType = row.Candidate.MediaType,
                            FilePaths = new List<string> { path },
                            Reason = ImportGroupReason.SingleFile,
                            IsSelected = row.IsSelected
                        });
                        created.PropertyChanged += (_, args) =>
                        {
                            if (args.PropertyName == nameof(CandidateRow.IsSelected))
                                UpdateSummary();
                        };
                        return created;
                    })
                    .ToList();

                for (int i = 0; i < inserts.Count; i++)
                {
                    if (index < 0)
                        _allRows.Add(inserts[i]);
                    else
                        _allRows.Insert(index + i, inserts[i]);
                }
            }

            ApplyFilter();
            _ = LoadCoversAsync();
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var selected = _allRows
                .Where(r => r.IsSelected)
                .Select(r =>
                {
                    r.Candidate.Title = r.Title;
                    r.Candidate.Author = r.Author;
                    r.Candidate.IsSelected = true;
                    return r.Candidate;
                })
                .ToList();

            ImportConfirmed?.Invoke(selected);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke();
        }

        private async Task LoadCoversAsync()
        {
            foreach (var row in _allRows)
                await LoadCoverAsync(row);
        }

        private static async Task LoadCoverAsync(CandidateRow row)
        {
            var path = row.Candidate.FilePaths.FirstOrDefault();
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

        private sealed class CandidateRow : INotifyPropertyChanged
        {
            private bool _isSelected;
            private string _title;
            private string _author;
            private ImageSource? _coverImage;

            public CandidateRow(ImportBookCandidate candidate)
            {
                Candidate = candidate;
                _isSelected = candidate.IsSelected;
                _title = candidate.Title;
                _author = candidate.Author;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public ImportBookCandidate Candidate { get; }

            public string FileSummary => Candidate.FileSummary;
            public string ReasonLabel => Candidate.ReasonLabel;

            public ImageSource? CoverImage
            {
                get => _coverImage;
                set
                {
                    _coverImage = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
                }
            }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                        return;
                    _isSelected = value;
                    Candidate.IsSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public string Title
            {
                get => _title;
                set
                {
                    if (_title == value)
                        return;
                    _title = value;
                    Candidate.Title = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
                }
            }

            public string Author
            {
                get => _author;
                set
                {
                    if (_author == value)
                        return;
                    _author = value;
                    Candidate.Author = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Author)));
                }
            }
        }
    }
}
