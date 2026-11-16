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

        public MainWindow()
        {
            InitializeComponent();

            LoadBooks();

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
            IEnumerable<Book> books = VM.Books;

            books = _currentTab switch
            {
                LibraryTab.Audiobooks => books.Where(b => b.MediaType == BookMediaType.Audiobook),
                LibraryTab.Epubs => books.Where(b => b.MediaType == BookMediaType.Epub),
                _ => books
            };

            var search = _booksPage.SearchText;
            if (!string.IsNullOrWhiteSpace(search))
            {
                books = books.Where(b =>
                    b.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    b.Author.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var selectedTags = _booksPage.SelectedTags;
            if (selectedTags.Count > 0)
            {
                books = books.Where(b =>
                    selectedTags.All(tag =>
                        b.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))));
            }

            books = _booksPage.ProgressFilter switch
            {
                ProgressFilter.Started => books.Where(b => b.IsStarted),
                ProgressFilter.NotStarted => books.Where(b => b.IsNotStarted),
                ProgressFilter.Completed => books.Where(b => b.IsCompleted),
                _ => books
            };

            _booksPage.SetAvailableTags(GetAllTags());
            _booksPage.SetBooks(books);
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
                // Clear session by opening nothing - simplest: pause and hide via nulling isn't available.
                // Persist then leave mini player until another book opens; remove from UI session:
            }

            VM.Books.Remove(book);
            SaveBooks();
            RefreshCurrentView();
        }

        private async Task ImportBookAsync()
        {
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
                Title = title,
                Author = author,
                FilePath = file.Path,
                MediaType = isEpub ? BookMediaType.Epub : BookMediaType.Audiobook
            };

            var dialog = new ImportBookDialog(book, GetAllTags());

            dialog.CancelRequested += () => ShowTab(LibraryTab.FullLibrary);

            dialog.ImportConfirmed += async (imported) =>
            {
                if (IsDuplicatePath(imported.FilePath))
                {
                    await ShowDuplicateDialogAsync();
                    return;
                }

                VM.Books.Add(imported);
                SaveBooks();
                ShowTab(LibraryTab.FullLibrary);
            };

            _showingFullPlayer = false;
            ContentFrame.Content = dialog;
            UpdateMiniPlayerVisibility();
        }

        private bool IsDuplicatePath(string path)
        {
            return VM.Books.Any(x =>
                string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase));
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

        private void LoadBooks()
        {
            if (!File.Exists(booksPath))
                return;

            var json = File.ReadAllText(booksPath);
            var books = JsonSerializer.Deserialize<List<Book>>(json);
            if (books == null)
                return;

            VM.Books.Clear();
            foreach (var book in books)
            {
                book.Tags ??= new List<string>();

                if (book.MediaType == default &&
                    Path.GetExtension(book.FilePath).Equals(".epub", StringComparison.OrdinalIgnoreCase))
                {
                    book.MediaType = BookMediaType.Epub;
                }

                VM.Books.Add(book);
            }
        }

        private async void OpenBook(Book book)
        {
            if (book.MediaType == BookMediaType.Epub)
            {
                // Epubs are not played by the audio player yet.
                return;
            }

            try
            {
                var isSameBook = _playback.CurrentBook != null &&
                    string.Equals(_playback.CurrentBook.FilePath, book.FilePath, StringComparison.OrdinalIgnoreCase);

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
