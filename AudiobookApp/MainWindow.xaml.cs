using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AudiobookApp.ViewModels;
using AudiobookApp.Models;
using AudiobookApp.Views;
using AudiobookApp.Services;
using Windows.Graphics;
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
        private bool _miniPlayerDismissed;
        private bool _compactMode;
        private AppWindow? _appWindow;
        private OverlappedPresenter? _presenter;
        private SizeInt32 _restoreSize;
        private PointInt32 _restorePosition;
        private bool _hadRestoreBounds;

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
            AppThemeService.Initialize(this);

            LoadBooks();
            LoadSeries();

            Activated += MainWindow_Activated;

            MiniPlayer.Attach(_playback, GetSeriesLabelForBook);
            MiniPlayer.ExpandRequested += ShowFullPlayer;
            MiniPlayer.CollapseRequested += EnterCompactMode;
            MiniPlayer.CloseRequested += () =>
            {
                _miniPlayerDismissed = true;
                _playback.Pause();
                UpdateMiniPlayerVisibility();
            };

            CompactPlayer.Attach(_playback, GetSeriesInfoForBook);
            CompactPlayer.ExpandRequested += ExitCompactMode;
            CompactPlayer.PinChanged += OnCompactPinChanged;

            _playback.PlaybackPersisted += _ => SaveBooks();
            _playback.BookFinished += book =>
            {
                DispatcherQueue.TryEnqueue(() => _ = OnBookFinishedAsync(book));
            };
            _playback.StateChanged += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateMiniPlayerVisibility();
                    MiniPlayer.RefreshSeriesLabel();
                    CompactPlayer.RefreshSeriesLabel();
                });
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
            _booksPage.CreateSeriesFlowRequested += StartCreateSeriesFlow;
            _booksPage.RemoveBookFromSeriesRequested += RemoveBookFromSeries;
            _booksPage.RenameSeriesRequested += RenameSeries;
            _booksPage.DisbandSeriesRequested += DisbandSeries;
            _booksPage.ReorderSeriesRequested += ShowReorderSeries;
            _booksPage.BookEdited += _ =>
            {
                SaveBooks();
                RefreshCurrentView();
            };

            ShowTab(LibraryTab.FullLibrary);
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) =>
            EnsureAppWindow();

        private void FullLibrary_Click(object sender, RoutedEventArgs e) => ShowTab(LibraryTab.FullLibrary);

        private void Audiobooks_Click(object sender, RoutedEventArgs e) => ShowTab(LibraryTab.Audiobooks);

        private void Epubs_Click(object sender, RoutedEventArgs e) => ShowTab(LibraryTab.Epubs);

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            _showingFullPlayer = false;
            ContentFrame.Content = new HelpPage();
            UpdateMiniPlayerVisibility();
        }

        private async void ClearLibrary_Click(object sender, RoutedEventArgs e)
        {
            var confirm = await new ContentDialog
            {
                Title = "Clear library?",
                Content = "This removes all books and series from your library.",
                PrimaryButtonText = "Clear all",
                CloseButtonText = "Cancel",
                XamlRoot = Content.XamlRoot
            }.ShowAsync();

            if (confirm != ContentDialogResult.Primary)
                return;

            if (_playback.HasSession)
                _playback.Pause();

            VM.Books.Clear();
            VM.Series.Clear();
            SaveBooks();
            SaveSeries();
            ShowTab(LibraryTab.FullLibrary);
        }

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
            var seriesMemberIds = new HashSet<Guid>(VM.Series.SelectMany(s => s.BookIds));

            if (!_booksPage.SinglesOnlyFilter)
            {
                foreach (var series in VM.Series)
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
                }
            }

            if (!_booksPage.SeriesOnlyFilter)
            {
                // Singles = books that are not members of any series.
                foreach (var book in VM.Books
                             .Where(b => !seriesMemberIds.Contains(b.Id))
                             .Where(MatchesBookFilters))
                {
                    items.Add(new LibraryItem
                    {
                        IsSeries = false,
                        Book = book,
                        Members = new[] { book }
                    });
                }
            }

            // Sort series and singles together by display name.
            items = items
                .OrderBy(
                    i => i.IsSeries
                        ? (i.Series?.Name ?? "")
                        : (i.Book?.Title ?? ""),
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            _booksPage.SetAvailableTags(GetAllTags());
            _booksPage.SetLibraryContext(VM.Books, VM.Series);
            _booksPage.SetLibraryItems(items);
            ContentFrame.Content = _booksPage;
            _showingFullPlayer = false;
            UpdateMiniPlayerVisibility();
        }

        private void UpdateMiniPlayerVisibility()
        {
            MiniPlayer.Visibility = _playback.HasSession
                && !_showingFullPlayer
                && !_miniPlayerDismissed
                && !_compactMode
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void EnterCompactMode()
        {
            if (!_playback.HasSession || _compactMode)
                return;

            EnsureAppWindow();
            if (_appWindow == null)
                return;

            _compactMode = true;

            // Capture pre-compact bounds only when the window still looks like a normal app frame.
            var current = _appWindow.Size;
            if (!_hadRestoreBounds || current.Width >= 900 && current.Height >= 600)
            {
                _restoreSize = current;
                _restorePosition = _appWindow.Position;
                _hadRestoreBounds = true;
            }

            NormalShell.Visibility = Visibility.Collapsed;
            CompactPlayer.Visibility = Visibility.Visible;
            CompactPlayer.IsPinned = false;
            UpdateMiniPlayerVisibility();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(CompactPlayer.DragHandle);

            if (_appWindow?.TitleBar != null)
            {
                // Remove the reserved title-bar band that was leaving empty space under the slider.
                _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            }

            if (_presenter != null)
            {
                _presenter.IsResizable = false;
                _presenter.IsMaximizable = false;
                _presenter.IsMinimizable = false;
                // Borderless + tiny preferred mins so Windows doesn't force empty height under the seek bar.
                _presenter.PreferredMinimumWidth = 1;
                _presenter.PreferredMinimumHeight = 1;
                _presenter.SetBorderAndTitleBar(false, false);
                _presenter.IsAlwaysOnTop = false;
            }

            CompactPlayer.UpdateLayout();
            ResizeCompactToContent();
            PositionCompactNearBottomRight();

            // Re-measure after title-bar collapse + first layout pass.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_compactMode)
                    return;

                CompactPlayer.UpdateLayout();
                ResizeCompactToContent();
            });
        }

        private void ResizeCompactToContent()
        {
            var size = CompactPlayer.GetContentSize();
            var width = size.Width;
            var height = size.Height;
            if (width < 100)
                width = 560;
            if (height < 40)
                height = 56;

            width = Math.Ceiling(width);
            height = Math.Ceiling(height);

            CompactPlayer.Width = width;
            CompactPlayer.Height = height;
            ResizeClientToDip(width, height);
        }

        private void ExitCompactMode()
        {
            if (!_compactMode)
                return;

            _compactMode = false;
            CompactPlayer.IsPinned = false;

            // Restore a full-size window first so the shell isn't laid out in the tiny compact frame.
            EnsureAppWindow();
            RestoreFullWindowBounds();

            try
            {
                // Clear custom title bar while ExtendsContentIntoTitleBar is still true.
                if (ExtendsContentIntoTitleBar)
                    SetTitleBar(RootGrid);
                ExtendsContentIntoTitleBar = false;
            }
            catch
            {
                ExtendsContentIntoTitleBar = false;
            }

            try
            {
                if (_appWindow?.TitleBar != null)
                    _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            }
            catch
            {
            }

            try
            {
                if (_presenter != null)
                {
                    _presenter.IsAlwaysOnTop = false;
                    _presenter.IsResizable = true;
                    _presenter.IsMaximizable = true;
                    _presenter.IsMinimizable = true;
                    _presenter.PreferredMinimumWidth = null;
                    _presenter.PreferredMinimumHeight = null;
                    _presenter.SetBorderAndTitleBar(true, true);
                }
            }
            catch
            {
            }

            CompactPlayer.ClearValue(FrameworkElement.WidthProperty);
            CompactPlayer.ClearValue(FrameworkElement.HeightProperty);
            CompactPlayer.Visibility = Visibility.Collapsed;
            NormalShell.Visibility = Visibility.Visible;

            // Chrome changes can alter outer size — restore again after shell is visible.
            RestoreFullWindowBounds();
            Activate();
            UpdateMiniPlayerVisibility();

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_compactMode)
                    return;

                RestoreFullWindowBounds();
                Activate();
            });
        }

        private void RestoreFullWindowBounds()
        {
            if (_appWindow == null)
                return;

            const int minWidth = 900;
            const int minHeight = 600;

            var width = _hadRestoreBounds ? _restoreSize.Width : 0;
            var height = _hadRestoreBounds ? _restoreSize.Height : 0;

            // If we don't have a real pre-compact size (or it still looks compact), use a default.
            if (width < minWidth || height < minHeight)
            {
                var scale = GetDpiScale();
                width = (int)Math.Round(1100 * scale);
                height = (int)Math.Round(720 * scale);
            }

            var x = _hadRestoreBounds ? _restorePosition.X : _appWindow.Position.X;
            var y = _hadRestoreBounds ? _restorePosition.Y : _appWindow.Position.Y;

            try
            {
                _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
            }
            catch
            {
                try
                {
                    _appWindow.Resize(new SizeInt32(width, height));
                }
                catch
                {
                }
            }
        }

        private void OnCompactPinChanged(bool pinned)
        {
            if (_presenter != null)
                _presenter.IsAlwaysOnTop = pinned;
        }

        private void ResizeToDip(double widthDip, double heightDip)
        {
            if (_appWindow == null)
                return;

            var scale = GetDpiScale();
            _appWindow.Resize(new SizeInt32(
                (int)Math.Round(widthDip * scale),
                (int)Math.Round(heightDip * scale)));
        }

        private void ResizeClientToDip(double widthDip, double heightDip)
        {
            if (_appWindow == null)
                return;

            var scale = GetDpiScale();
            var size = new SizeInt32(
                (int)Math.Round(widthDip * scale),
                (int)Math.Round(heightDip * scale));

            try
            {
                _appWindow.ResizeClient(size);
            }
            catch
            {
                // Fallback if ResizeClient isn't available for this presenter.
                _appWindow.Resize(size);
            }
        }

        private double GetDpiScale()
        {
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            return scale > 0 ? scale : 1.0;
        }

        private void PositionCompactNearBottomRight()
        {
            if (_appWindow == null)
                return;

            try
            {
                var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
                var work = display.WorkArea;
                var size = _appWindow.Size;
                var x = work.X + work.Width - size.Width - 24;
                var y = work.Y + work.Height - size.Height - 24;
                _appWindow.Move(new PointInt32(x, y));
            }
            catch
            {
            }
        }

        private void EnsureAppWindow()
        {
            if (_appWindow != null)
                return;

            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _presenter = _appWindow.Presenter as OverlappedPresenter;
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
            var ordered = new List<Book> { book };
            ordered.AddRange(additional.Where(b => b.Id != book.Id));
            CreateSeriesFromOrderedBooks(ordered, name);
        }

        private void StartCreateSeriesFlow(Book seed, IReadOnlyList<Book> additional)
        {
            var members = new List<Book> { seed };
            members.AddRange(additional.Where(b => b.Id != seed.Id));
            if (members.Count < 2)
                return;

            ShowCreateSeriesReorder(seed, members);
        }

        private void ShowCreateSeriesReorder(Book seed, IReadOnlyList<Book> members)
        {
            var draft = new Series
            {
                Name = seed.Title,
                BookIds = members.Select(m => m.Id).ToList()
            };

            var page = new SeriesReorderPage(draft, members);
            page.SetContinueMode(true);
            page.CancelRequested += () => ShowTab(_currentTab);
            page.OrderConfirmed += (reorderedSeries, orderedIds) =>
                _ = FinishCreateSeriesAfterReorderAsync(seed, orderedIds);

            _showingFullPlayer = false;
            ContentFrame.Content = page;
            UpdateMiniPlayerVisibility();
        }

        private async Task FinishCreateSeriesAfterReorderAsync(Book seed, IReadOnlyList<Guid> orderedIds)
        {
            var orderedBooks = orderedIds
                .Select(id => VM.Books.FirstOrDefault(b => b.Id == id))
                .Where(b => b != null)
                .Cast<Book>()
                .ToList();

            if (orderedBooks.Count < 2)
            {
                ShowTab(_currentTab);
                return;
            }

            var nameBox = new TextBox
            {
                Header = "Series name",
                PlaceholderText = "e.g. Classroom of the Elite",
                Text = seed.Title
            };

            var dialog = new ContentDialog
            {
                Title = "Name series",
                Content = nameBox,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                // Return to reorder instead of abandoning series creation.
                ShowCreateSeriesReorder(seed, orderedBooks);
                return;
            }

            var name = nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowCreateSeriesReorder(seed, orderedBooks);
                return;
            }

            CreateSeriesFromOrderedBooks(orderedBooks, name);
            ShowTab(_currentTab);
        }

        private void CreateSeriesFromOrderedBooks(IReadOnlyList<Book> orderedBooks, string name)
        {
            foreach (var book in orderedBooks)
                RemoveBookFromAllSeries(book.Id);

            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = name.Trim(),
                BookIds = orderedBooks.Select(b => b.Id).Distinct().ToList()
            };

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

        private void ShowReorderSeries(Series series, IReadOnlyList<Book> members)
        {
            if (members.Count == 0)
                return;

            var page = new SeriesReorderPage(series, members);
            page.SetContinueMode(false);
            page.CancelRequested += () => ShowTab(_currentTab);
            page.OrderConfirmed += (reorderedSeries, orderedIds) =>
            {
                var target = VM.Series.FirstOrDefault(s => s.Id == reorderedSeries.Id);
                if (target == null)
                {
                    ShowTab(_currentTab);
                    return;
                }

                if (orderedIds.Count == 0)
                {
                    VM.Series.Remove(target);
                }
                else
                {
                    target.BookIds = orderedIds.ToList();
                }

                SaveSeries();
                ShowTab(_currentTab);
            };

            _showingFullPlayer = false;
            ContentFrame.Content = page;
            UpdateMiniPlayerVisibility();
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
            var modeButtons = new RadioButtons
            {
                SelectedIndex = 0
            };
            modeButtons.Items.Add("Single file");
            modeButtons.Items.Add("Multiple audio files");
            modeButtons.Items.Add("Entire directory");

            var modeDialog = new ContentDialog
            {
                Title = "Import book",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Choose how to import.",
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        modeButtons
                    }
                },
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                XamlRoot = Content.XamlRoot
            };

            var mode = await modeDialog.ShowAsync();
            if (mode != ContentDialogResult.Primary)
                return;

            if (modeButtons.SelectedIndex == 1)
            {
                await ImportMultiFileAudiobookAsync();
                return;
            }

            if (modeButtons.SelectedIndex == 2)
            {
                await ImportDirectoryAsync();
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

        private async Task ImportDirectoryAsync()
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null)
                return;

            var scanningDialog = new ContentDialog
            {
                Title = "Scanning directory",
                Content = new StackPanel
                {
                    Spacing = 16,
                    Children =
                    {
                        new ProgressRing { IsActive = true, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock
                        {
                            Text = "Looking for audiobooks and epubs. Large folders can take a while — please wait.",
                            TextWrapping = TextWrapping.WrapWholeWords
                        }
                    }
                },
                XamlRoot = Content.XamlRoot
            };

            var scanningShow = scanningDialog.ShowAsync();

            List<ImportBookCandidate> candidates;
            try
            {
                var path = folder.Path;
                candidates = await Task.Run(() => DirectoryBookImporter.ScanDirectory(path));
            }
            catch (Exception ex)
            {
                scanningDialog.Hide();
                await scanningShow;

                await new ContentDialog
                {
                    Title = "Unable to scan directory",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();
                return;
            }
            finally
            {
                scanningDialog.Hide();
            }

            await scanningShow;

            if (candidates.Count == 0)
            {
                await new ContentDialog
                {
                    Title = "No books found",
                    Content = "No audio or epub files were found in that folder.",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();
                return;
            }

            // Drop exact path duplicates already in the library.
            foreach (var candidate in candidates)
            {
                candidate.FilePaths = candidate.FilePaths
                    .Where(p => !IsDuplicatePath(p))
                    .ToList();
            }

            candidates = candidates.Where(c => c.FilePaths.Count > 0).ToList();
            if (candidates.Count == 0)
            {
                await ShowDuplicateDialogAsync();
                return;
            }

            ShowDirectoryImportReviewPage(candidates);
        }

        private void ShowDirectoryImportReviewPage(IReadOnlyList<ImportBookCandidate> candidates)
        {
            var review = new DirectoryImportReviewPage(candidates);
            review.CancelRequested += () => ShowTab(LibraryTab.FullLibrary);
            review.ImportConfirmed += (selected, allCandidates) =>
                _ = ConfirmDirectoryImportAsync(selected, allCandidates);

            _showingFullPlayer = false;
            ContentFrame.Content = review;
            UpdateMiniPlayerVisibility();
        }

        private async Task ConfirmDirectoryImportAsync(
            IReadOnlyList<ImportBookCandidate> selected,
            IReadOnlyList<ImportBookCandidate> allReviewCandidates)
        {
            // Skip books whose files are already in the library; keep importing the rest.
            var toImport = selected
                .Where(c => c.FilePaths.Count > 0)
                .Where(c => !c.FilePaths.Any(IsDuplicatePath))
                .ToList();

            if (toImport.Count == 0)
            {
                await ShowDuplicateDialogAsync();
                ShowTab(LibraryTab.FullLibrary);
                return;
            }

            var skipped = selected.Count - toImport.Count;

            // Same-title confirmation among books being imported.
            var duplicateTitles = toImport
                .GroupBy(c => (c.Title ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
                .ToList();

            if (duplicateTitles.Count > 0)
            {
                var names = string.Join(
                    Environment.NewLine,
                    duplicateTitles.Select(g => $"• \"{g.Key}\" ({g.Count()} books)"));

                var confirmDup = await new ContentDialog
                {
                    Title = "Books with the same name",
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text =
                                "These books share the same title. Import them anyway?" +
                                Environment.NewLine + Environment.NewLine + names,
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        MaxHeight = 320
                    },
                    PrimaryButtonText = "Import anyway",
                    CloseButtonText = "Cancel",
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();

                if (confirmDup != ContentDialogResult.Primary)
                    return;
            }

            await FinishDirectoryImportAsync(toImport, skipped, allReviewCandidates);
        }

        private async Task FinishDirectoryImportAsync(
            IReadOnlyList<ImportBookCandidate> toImport,
            int skippedExisting,
            IReadOnlyList<ImportBookCandidate> allReviewCandidates)
        {
            var importedBooks = new List<Book>();

            foreach (var candidate in toImport)
            {
                if (candidate.FilePaths.Count == 0)
                    continue;

                if (candidate.FilePaths.Any(IsDuplicatePath))
                    continue;

                var book = candidate.ToBook();

                if (book.MediaType == BookMediaType.Audiobook)
                {
                    var total = TimeSpan.Zero;
                    foreach (var path in book.AudioFilePaths)
                    {
                        try
                        {
                            var track = new ATL.Track(path);
                            if (track.DurationMs > 0)
                                total += TimeSpan.FromMilliseconds(track.DurationMs);
                        }
                        catch
                        {
                        }
                    }

                    book.Duration = total;
                }

                VM.Books.Add(book);
                importedBooks.Add(book);
            }

            if (importedBooks.Count > 0)
                SaveBooks();

            var message = importedBooks.Count == 1
                ? "Imported 1 book."
                : $"Imported {importedBooks.Count} books.";
            if (skippedExisting > 0)
                message += $" Skipped {skippedExisting} already in your library.";

            if (importedBooks.Count == 0)
            {
                if (skippedExisting > 0)
                    await ShowDuplicateDialogAsync();
                ShowTab(LibraryTab.FullLibrary);
                return;
            }

            var askSeries = await new ContentDialog
            {
                Title = "Import complete",
                Content = message + Environment.NewLine + Environment.NewLine +
                          "Would you like to combine any of these books into series?",
                PrimaryButtonText = "Yes",
                CloseButtonText = "No",
                XamlRoot = Content.XamlRoot
            }.ShowAsync();

            if (askSeries != ContentDialogResult.Primary)
            {
                ShowTab(LibraryTab.FullLibrary);
                return;
            }

            ShowDirectoryImportSeriesPage(importedBooks, allReviewCandidates);
        }

        private void ShowDirectoryImportSeriesPage(
            IReadOnlyList<Book> importedBooks,
            IReadOnlyList<ImportBookCandidate> reviewCandidates,
            IReadOnlyList<Series>? seriesSnapshot = null,
            HashSet<Guid>? importedIds = null)
        {
            seriesSnapshot ??= VM.Series
                .Select(s => new Series
                {
                    Id = s.Id,
                    Name = s.Name,
                    BookIds = s.BookIds.ToList()
                })
                .ToList();
            importedIds ??= importedBooks.Select(b => b.Id).ToHashSet();

            var snapshot = seriesSnapshot;
            var ids = importedIds;

            var page = new DirectoryImportSeriesPage(importedBooks, VM.Books, VM.Series);

            page.AddToSeriesRequested += (series, newBooks) =>
            {
                var target = VM.Series.FirstOrDefault(s => s.Id == series.Id);
                if (target == null || newBooks.Count == 0)
                    return;

                foreach (var book in newBooks)
                {
                    RemoveBookFromAllSeries(book.Id);
                    if (!target.BookIds.Contains(book.Id))
                        target.BookIds.Add(book.Id);
                }

                SaveSeries();
                page.RefreshSeries(VM.Series, VM.Books);
            };

            page.CreateSeriesRequested += (name, books) =>
            {
                if (books.Count == 0)
                    return;

                foreach (var book in books)
                    RemoveBookFromAllSeries(book.Id);

                VM.Series.Add(new Series
                {
                    Id = Guid.NewGuid(),
                    Name = name.Trim(),
                    BookIds = books.Select(b => b.Id).Distinct().ToList()
                });

                SaveSeries();
                page.RefreshSeries(VM.Series, VM.Books);
            };

            page.SeriesOrderChangedRequested += (series, orderedIds) =>
            {
                var target = VM.Series.FirstOrDefault(s => s.Id == series.Id);
                if (target == null)
                    return;

                target.BookIds = orderedIds.ToList();
                SaveSeries();
            };

            page.RemoveBookFromSeriesRequested += book =>
            {
                RemoveBookFromAllSeries(book.Id);
                SaveSeries();
                page.RefreshSeries(VM.Series, VM.Books);
            };

            page.DoneRequested += () => ShowTab(LibraryTab.FullLibrary);

            page.CancelRequested += () =>
            {
                foreach (var id in ids)
                {
                    var book = VM.Books.FirstOrDefault(b => b.Id == id);
                    if (book != null)
                        VM.Books.Remove(book);
                }

                VM.Series.Clear();
                foreach (var series in snapshot)
                    VM.Series.Add(series);

                SaveBooks();
                SaveSeries();
                ShowDirectoryImportReviewPage(reviewCandidates);
            };

            _showingFullPlayer = false;
            ContentFrame.Content = page;
            UpdateMiniPlayerVisibility();
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
                .OrderBy(f => f.Name, Comparer<string>.Create(DirectoryBookImporter.CompareNatural))
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
            _miniPlayerDismissed = false;

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

            if (_compactMode)
                ExitCompactMode();

            _miniPlayerDismissed = false;

            try
            {
                _showingFullPlayer = true;
                var page = new PlayerPage(_playback, GetSeriesLabelForBook);
                page.ShrinkRequested += () => ShowTab(_currentTab);
                page.ShrinkToMiniRequested += EnterCompactMode;
                ContentFrame.Content = page;
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

        private string? GetSeriesLabelForBook(Book? book)
        {
            var info = GetSeriesInfoForBook(book);
            if (info == null)
                return null;

            if (!string.IsNullOrWhiteSpace(info.Value.SeriesName) &&
                !string.IsNullOrWhiteSpace(info.Value.BookCount))
            {
                return $"{info.Value.SeriesName} · {info.Value.BookCount}";
            }

            return info.Value.SeriesName ?? info.Value.BookCount;
        }

        private (string? SeriesName, string? BookCount)? GetSeriesInfoForBook(Book? book)
        {
            if (book == null)
                return null;

            foreach (var series in VM.Series)
            {
                var index = series.BookIds.FindIndex(id => id == book.Id);
                if (index < 0)
                    continue;

                return (series.Name, $"Book ({index + 1}/{series.BookIds.Count})");
            }

            return null;
        }

        private Book? GetNextSeriesBook(Book finished)
        {
            foreach (var series in VM.Series)
            {
                var index = series.BookIds.FindIndex(id => id == finished.Id);
                if (index < 0)
                    continue;

                if (index + 1 >= series.BookIds.Count)
                    return null;

                var nextId = series.BookIds[index + 1];
                return VM.Books.FirstOrDefault(b => b.Id == nextId);
            }

            return null;
        }

        private async Task OnBookFinishedAsync(Book finished)
        {
            SaveBooks();

            if (!_playback.SeriesAutoplayEnabled)
                return;

            var next = GetNextSeriesBook(finished);
            if (next == null || next.MediaType != BookMediaType.Audiobook)
                return;

            try
            {
                await _playback.OpenBookAsync(next, autoPlay: true);
                MiniPlayer.RefreshSeriesLabel();
                CompactPlayer.RefreshSeriesLabel();

                if (_showingFullPlayer)
                    ShowFullPlayer();
                else
                    UpdateMiniPlayerVisibility();
            }
            catch (Exception ex)
            {
                await new ContentDialog
                {
                    Title = "Unable to autoplay next book",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                }.ShowAsync();
            }
        }
    }
}
