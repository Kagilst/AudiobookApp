using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AudiobookApp.ViewModels;
using AudiobookApp.Models;
using AudiobookApp.Views;
using AudiobookApp.Services;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace AudiobookApp
{
    public sealed partial class MainWindow : Window
    {
        private enum LibraryTab
        {
            FullLibrary,
            Audiobooks,
            Epubs
        }

        public MainViewModel VM { get; } = new();

        private readonly BooksPage _booksPage = new BooksPage();
        private readonly PlaybackService _playback = new PlaybackService();
        private LibraryTab _currentTab = LibraryTab.FullLibrary;
        private bool _showingFullPlayer;

        private readonly string booksPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudiobookApp",
            "books.json");

        private readonly string seriesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudiobookApp",
            "series.json");

        public MainWindow()
        {
            InitializeComponent();

            LoadBooks();
            LoadSeries();

            MiniPlayer.Attach(_playback);
            MiniPlayer.ExpandRequested += ShowFullPlayer;

            _playback.PlaybackPersisted += _ => SaveBooks();
            _playback.StateChanged += () =>
            {
                DispatcherQueue.TryEnqueue(UpdateMiniPlayerVisibility);
            };

            _booksPage.ImportRequested += async (_, _) => await ImportBookAsync();
            _booksPage.RemoveRequested += RemoveBook;
            _booksPage.BookSelected += OpenBook;
            _booksPage.FilterChanged += RefreshCurrentView;
            _booksPage.TagsChanged += _ =>
            {
                SaveBooks();
                RefreshCurrentView();
            };

            _booksPage.RatingChanged += _ =>
            {
                SaveBooks();
                RefreshCurrentView();
            };

            _booksPage.AddBookToSeriesRequested += AddBookToSeries;
            _booksPage.CreateSeriesRequested += CreateSeriesFromBook;
            _booksPage.RemoveBookFromSeriesRequested += RemoveBookFromSeries;
            _booksPage.RenameSeriesRequested += RenameSeries;
            _booksPage.DisbandSeriesRequested += DisbandSeries;

            ShowTab(LibraryTab.FullLibrary);
        }

        private void FullLibrary_Click(object sender, RoutedEventArgs e) => ShowTab(LibraryTab.FullLibrary);

        private void Audiobooks_Click(object sender, RoutedEventArgs e) => ShowTab(LibraryTab.Audiobooks);

        private void Epubs_Click(object sender, RoutedEventArgs e) => ShowTab(LibraryTab.Epubs);

        private void ShowTab(LibraryTab tab)
        {
            _currentTab = tab;
            _showingFullPlayer = false;
            UpdateSidebarHighlight();
            RefreshCurrentView();
            UpdateMiniPlayerVisibility();
        }

        private void UpdateSidebarHighlight()
        {
            SetSidebarButtonState(FullLibraryButton, _currentTab == LibraryTab.FullLibrary);
            SetSidebarButtonState(AudiobooksButton, _currentTab == LibraryTab.Audiobooks);
            SetSidebarButtonState(EpubsButton, _currentTab == LibraryTab.Epubs);
        }

        private static void SetSidebarButtonState(Button button, bool selected)
        {
            button.FontWeight = selected
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            button.Opacity = selected ? 1.0 : 0.85;
        }

        private void RefreshCurrentView()
        {
            var booksById = VM.Books.ToDictionary(b => b.Id);

            bool MatchesTab(Book b) => _currentTab switch
            {
                LibraryTab.Audiobooks => b.MediaType == BookMediaType.Audiobook,
                LibraryTab.Epubs => b.MediaType == BookMediaType.Epub,
                _ => true
            };

            bool MatchesSearch(Book b)
            {
                var search = _booksPage.SearchText;
                if (string.IsNullOrWhiteSpace(search))
                    return true;

                return b.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                       b.Author.Contains(search, StringComparison.OrdinalIgnoreCase);
            }

            bool MatchesTags(Book b)
            {
                var selectedTags = _booksPage.SelectedTags;
                if (selectedTags.Count == 0)
                    return true;

                return selectedTags.All(tag =>
                    b.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
            }

            bool MatchesProgress(Book b) => _booksPage.ProgressFilter switch
            {
                ProgressFilter.Started => b.IsStarted,
                ProgressFilter.NotStarted => b.IsNotStarted,
                ProgressFilter.Completed => b.IsCompleted,
                _ => true
            };

            bool MatchesBookFilters(Book b) =>
                MatchesTab(b) && MatchesSearch(b) && MatchesTags(b) && MatchesProgress(b);

            var items = new List<LibraryItem>();
            var includedBookIds = new HashSet<Guid>();

            foreach (var series in VM.Series.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var members = series.BookIds
                    .Where(id => booksById.ContainsKey(id))
                    .Select(id => booksById[id])
                    .ToList();

                if (members.Count == 0)
                    continue;

                var visibleMembers = members.Where(MatchesBookFilters).ToList();

                var search = _booksPage.SearchText;
                var seriesNameMatches = !string.IsNullOrWhiteSpace(search) &&
                    series.Name.Contains(search, StringComparison.OrdinalIgnoreCase);

                if (visibleMembers.Count == 0 && !seriesNameMatches)
                    continue;

                // If only the series name matched search, still show members that pass tab/tag/progress.
                if (visibleMembers.Count == 0 && seriesNameMatches)
                {
                    visibleMembers = members
                        .Where(b => MatchesTab(b) && MatchesTags(b) && MatchesProgress(b))
                        .ToList();
                }

                if (visibleMembers.Count == 0)
                    continue;

                // Keep series order for covers/detail, but only include filter-matching members in the popup.
                var orderedVisible = series.BookIds
                    .Where(id => visibleMembers.Any(m => m.Id == id))
                    .Select(id => booksById[id])
                    .ToList();

                items.Add(new LibraryItem
                {
                    IsSeries = true,
                    Series = series,
                    Members = orderedVisible
                });

                foreach (var member in members)
                    includedBookIds.Add(member.Id);
            }

            foreach (var book in VM.Books
                         .Where(b => !includedBookIds.Contains(b.Id))
                         .Where(MatchesBookFilters)
                         .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new LibraryItem
                {
                    IsSeries = false,
                    Book = book,
                    Members = new[] { book }
                });
            }

            _booksPage.SetAvailableTags(GetAllTags());
            _booksPage.SetLibraryContext(VM.Books, VM.Series);
            _booksPage.SetLibraryItems(items);
            ContentFrame.Content = _booksPage;
            _showingFullPlayer = false;
            UpdateMiniPlayerVisibility();
        }

        private void UpdateMiniPlayerVisibility()
        {
            MiniPlayer.Visibility = _playback.HasSession && !_showingFullPlayer
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private IEnumerable<string> GetAllTags()
        {
            return VM.Books
                .SelectMany(b => b.Tags)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
        }

        private void RemoveBook(Book book)
        {
            if (_playback.CurrentBook != null &&
                string.Equals(_playback.CurrentBook.FilePath, book.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _playback.Pause();
            }

            RemoveBookFromAllSeries(book.Id);
            VM.Books.Remove(book);
            SaveBooks();
            SaveSeries();
            RefreshCurrentView();
        }

        private void AddBookToSeries(Book book, Series series)
        {
            RemoveBookFromAllSeries(book.Id);

            var target = VM.Series.FirstOrDefault(s => s.Id == series.Id);
            if (target == null)
                return;

            if (!target.BookIds.Contains(book.Id))
                target.BookIds.Add(book.Id);

            SaveSeries();
            RefreshCurrentView();
        }

        private void CreateSeriesFromBook(Book book, string name, IReadOnlyList<Book> additional)
        {
            RemoveBookFromAllSeries(book.Id);
            foreach (var extra in additional)
                RemoveBookFromAllSeries(extra.Id);

            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = name.Trim()
            };

            series.BookIds.Add(book.Id);
            foreach (var extra in additional)
            {
                if (!series.BookIds.Contains(extra.Id))
                    series.BookIds.Add(extra.Id);
            }

            VM.Series.Add(series);
            SaveSeries();
            RefreshCurrentView();
        }

        private void RemoveBookFromSeries(Book book)
        {
            RemoveBookFromAllSeries(book.Id);
            SaveSeries();
            RefreshCurrentView();
        }

        private void RenameSeries(Series series, string name)
        {
            var target = VM.Series.FirstOrDefault(s => s.Id == series.Id);
            if (target == null)
                return;

            target.Name = name.Trim();
            SaveSeries();
            RefreshCurrentView();
        }

        private void DisbandSeries(Series series)
        {
            var target = VM.Series.FirstOrDefault(s => s.Id == series.Id);
            if (target == null)
                return;

            VM.Series.Remove(target);
            SaveSeries();
            RefreshCurrentView();
        }

        private void RemoveBookFromAllSeries(Guid bookId)
        {
            foreach (var series in VM.Series.ToList())
            {
                series.BookIds.RemoveAll(id => id == bookId);
                if (series.BookIds.Count == 0)
                    VM.Series.Remove(series);
            }
        }

        private async Task ImportBookAsync()
        {
            var modeDialog = new ContentDialog
            {
                Title = "Import book",
                Content = "Import one file, or combine multiple audio files into a single book.",
                PrimaryButtonText = "Single file",
                SecondaryButtonText = "Multiple audio files",
                CloseButtonText = "Cancel",
                XamlRoot = Content.XamlRoot
            };

            var mode = await modeDialog.ShowAsync();
            if (mode == ContentDialogResult.None)
                return;

            if (mode == ContentDialogResult.Secondary)
            {
                await ImportMultiFileAudiobookAsync();
                return;
            }

            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4b");
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".epub");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null)
                return;

            if (IsDuplicatePath(file.Path))
            {
                await ShowDuplicateDialogAsync();
                return;
            }

            var extension = Path.GetExtension(file.Path);
            var isEpub = extension.Equals(".epub", StringComparison.OrdinalIgnoreCase);

            string title;
            string author;

            if (isEpub)
            {
                title = Path.GetFileNameWithoutExtension(file.Name);
                author = "Unknown Author";
            }
            else
            {
                var properties = await file.Properties.GetMusicPropertiesAsync();
                title = string.IsNullOrWhiteSpace(properties.Title)
                    ? Path.GetFileNameWithoutExtension(file.Name)
                    : properties.Title;
                author = string.IsNullOrWhiteSpace(properties.Artist)
                    ? "Unknown Author"
                    : properties.Artist;
            }

            var book = new Book
            {
                Id = Guid.NewGuid(),
                Title = title,
                Author = author,
                FilePath = file.Path,
                FilePaths = new List<string> { file.Path },
                MediaType = isEpub ? BookMediaType.Epub : BookMediaType.Audiobook
            };

            await ShowImportDialogAsync(book);
        }

        private async Task ImportMultiFileAudiobookAsync()
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4b");
            picker.FileTypeFilter.Add(".mp4");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0)
                return;

            var initialPaths = files
                .OrderBy(f => f.Name, Comparer<string>.Create(CompareNatural))
                .Select(f => f.Path)
                .ToList();

            if (initialPaths.Any(IsDuplicatePath))
            {
                await ShowDuplicateDialogAsync();
                return;
            }

            var sortPage = new MultiFileSortPage(initialPaths);
            sortPage.CancelRequested += () => ShowTab(LibraryTab.FullLibrary);
            sortPage.OrderConfirmed += async paths =>
            {
                if (paths.Count == 0)
                {
                    ShowTab(LibraryTab.FullLibrary);
                    return;
                }

                if (paths.Any(IsDuplicatePath))
                {
                    await ShowDuplicateDialogAsync();
                    return;
                }

                await FinishMultiFileImportAsync(paths);
            };

            _showingFullPlayer = false;
            ContentFrame.Content = sortPage;
            UpdateMiniPlayerVisibility();
        }

        private async Task FinishMultiFileImportAsync(IReadOnlyList<string> paths)
        {
            var firstPath = paths[0];
            Windows.Storage.StorageFile first;
            try
            {
                first = await Windows.Storage.StorageFile.GetFileFromPathAsync(firstPath);
            }
            catch
            {
                await new ContentDialog
                {
                    Title = "Unable to import",
                    Content = "Could not open the first audio file.",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();
                return;
            }

            var properties = await first.Properties.GetMusicPropertiesAsync();

            var folderName = Path.GetFileName(
                Path.GetDirectoryName(firstPath) ?? string.Empty);

            var title = !string.IsNullOrWhiteSpace(properties.Album)
                ? properties.Album
                : !string.IsNullOrWhiteSpace(folderName)
                    ? folderName
                    : Path.GetFileNameWithoutExtension(first.Name);

            var author = !string.IsNullOrWhiteSpace(properties.AlbumArtist)
                ? properties.AlbumArtist
                : !string.IsNullOrWhiteSpace(properties.Artist)
                    ? properties.Artist
                    : "Unknown Author";

            var totalDuration = TimeSpan.Zero;
            foreach (var path in paths)
            {
                try
                {
                    var track = new ATL.Track(path);
                    if (track.DurationMs > 0)
                        totalDuration += TimeSpan.FromMilliseconds(track.DurationMs);
                }
                catch
                {
                }
            }

            var book = new Book
            {
                Id = Guid.NewGuid(),
                Title = title,
                Author = author,
                FilePath = paths[0],
                FilePaths = paths.ToList(),
                MediaType = BookMediaType.Audiobook,
                Duration = totalDuration
            };

            await ShowImportDialogAsync(book);
        }

        private async Task ShowImportDialogAsync(Book book)
        {
            var booksNotInSeries = GetBooksNotInAnySeries().ToList();
            var dialog = new ImportBookDialog(book, GetAllTags(), VM.Series, booksNotInSeries);

            dialog.CancelRequested += () => ShowTab(LibraryTab.FullLibrary);

            dialog.ImportConfirmed += async (imported, seriesChoice) =>
            {
                imported.NormalizeFilePaths();

                if (imported.AudioFilePaths.Any(IsDuplicatePath))
                {
                    await ShowDuplicateDialogAsync();
                    return;
                }

                if (imported.Id == Guid.Empty)
                    imported.Id = Guid.NewGuid();

                VM.Books.Add(imported);
                ApplyImportSeriesChoice(imported, seriesChoice);
                SaveBooks();
                SaveSeries();
                ShowTab(LibraryTab.FullLibrary);
            };

            _showingFullPlayer = false;
            ContentFrame.Content = dialog;
            UpdateMiniPlayerVisibility();
            await Task.CompletedTask;
        }

        private static int CompareNatural(string? a, string? b)
        {
            a ??= "";
            b ??= "";

            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    long numA = 0;
                    while (i < a.Length && char.IsDigit(a[i]))
                    {
                        numA = numA * 10 + (a[i] - '0');
                        i++;
                    }

                    long numB = 0;
                    while (j < b.Length && char.IsDigit(b[j]))
                    {
                        numB = numB * 10 + (b[j] - '0');
                        j++;
                    }

                    if (numA != numB)
                        return numA.CompareTo(numB);
                }
                else
                {
                    var cmp = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (cmp != 0)
                        return cmp;
                    i++;
                    j++;
                }
            }

            return a.Length.CompareTo(b.Length);
        }

        private void ApplyImportSeriesChoice(Book imported, ImportSeriesChoice choice)
        {
            if (choice.Mode == SeriesImportMode.AddToExisting && choice.ExistingSeries != null)
            {
                var target = VM.Series.FirstOrDefault(s => s.Id == choice.ExistingSeries.Id);
                if (target == null)
                    return;

                if (!target.BookIds.Contains(imported.Id))
                    target.BookIds.Add(imported.Id);
                return;
            }

            if (choice.Mode == SeriesImportMode.CreateNew)
            {
                var series = new Series
                {
                    Id = Guid.NewGuid(),
                    Name = choice.NewSeriesName.Trim()
                };

                foreach (var extra in choice.AdditionalBooks)
                {
                    RemoveBookFromAllSeries(extra.Id);
                    if (!series.BookIds.Contains(extra.Id))
                        series.BookIds.Add(extra.Id);
                }

                if (!series.BookIds.Contains(imported.Id))
                    series.BookIds.Add(imported.Id);

                VM.Series.Add(series);
            }
        }

        private IEnumerable<Book> GetBooksNotInAnySeries()
        {
            var inSeries = VM.Series.SelectMany(s => s.BookIds).ToHashSet();
            return VM.Books.Where(b => !inSeries.Contains(b.Id));
        }

        private bool IsDuplicatePath(string path)
        {
            return VM.Books.Any(x =>
                x.AudioFilePaths.Any(p =>
                    string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        }

        private async Task ShowDuplicateDialogAsync()
        {
            await new ContentDialog
            {
                Title = "Duplicate Book",
                Content = "This book already exists in your library.",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            }.ShowAsync();
        }

        private void SaveBooks()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(booksPath)!);
            File.WriteAllText(booksPath, JsonSerializer.Serialize(VM.Books));
        }

        private void SaveSeries()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(seriesPath)!);
            File.WriteAllText(seriesPath, JsonSerializer.Serialize(VM.Series));
        }

        private void LoadBooks()
        {
            if (!File.Exists(booksPath))
                return;

            var json = File.ReadAllText(booksPath);
            var books = JsonSerializer.Deserialize<List<Book>>(json);
            if (books == null)
                return;

            VM.Books.Clear();
            var changed = false;
            foreach (var book in books)
            {
                book.Tags ??= new List<string>();
                book.FilePaths ??= new List<string>();

                if (book.Id == Guid.Empty)
                {
                    book.Id = Guid.NewGuid();
                    changed = true;
                }

                var beforeNormalize = book.FilePaths.Count;
                book.NormalizeFilePaths();
                if (book.FilePaths.Count != beforeNormalize && beforeNormalize == 0)
                    changed = true;

                if (book.MediaType == default &&
                    Path.GetExtension(book.FilePath).Equals(".epub", StringComparison.OrdinalIgnoreCase))
                {
                    book.MediaType = BookMediaType.Epub;
                }

                VM.Books.Add(book);
            }

            if (changed)
                SaveBooks();
        }

        private void LoadSeries()
        {
            if (!File.Exists(seriesPath))
                return;

            var json = File.ReadAllText(seriesPath);
            var seriesList = JsonSerializer.Deserialize<List<Series>>(json);
            if (seriesList == null)
                return;

            var validIds = VM.Books.Select(b => b.Id).ToHashSet();

            VM.Series.Clear();
            foreach (var series in seriesList)
            {
                series.BookIds ??= new List<Guid>();
                series.BookIds = series.BookIds.Where(validIds.Contains).ToList();
                if (series.Id == Guid.Empty)
                    series.Id = Guid.NewGuid();
                if (series.BookIds.Count == 0)
                    continue;

                VM.Series.Add(series);
            }
        }

        private async void OpenBook(Book book)
        {
            if (book.MediaType == BookMediaType.Epub)
            {
                ShowEpubReader(book);
                return;
            }

            try
            {
                var isSameBook = _playback.CurrentBook != null &&
                    _playback.CurrentBook.Id == book.Id;

                if (!isSameBook)
                    await _playback.OpenBookAsync(book, autoPlay: false);

                ShowFullPlayer();
            }
            catch (Exception ex)
            {
                await new ContentDialog
                {
                    Title = "Unable to open audiobook",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();
            }
        }

        private void ShowEpubReader(Book book)
        {
            try
            {
                _showingFullPlayer = true;
                var reader = new EpubReaderPage(book);
                reader.ProgressChanged += _ => SaveBooks();
                ContentFrame.Content = reader;
                UpdateMiniPlayerVisibility();
            }
            catch (Exception ex)
            {
                _showingFullPlayer = false;
                UpdateMiniPlayerVisibility();
                _ = new ContentDialog
                {
                    Title = "Unable to open EPUB",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();
            }
        }

        private void ShowFullPlayer()
        {
            if (_playback.CurrentBook == null)
                return;

            try
            {
                _showingFullPlayer = true;
                ContentFrame.Content = new PlayerPage(_playback);
                UpdateMiniPlayerVisibility();
            }
            catch (Exception ex)
            {
                _showingFullPlayer = false;
                UpdateMiniPlayerVisibility();
                _ = new ContentDialog
                {
                    Title = "Player error",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();
            }
        }
    }
}
