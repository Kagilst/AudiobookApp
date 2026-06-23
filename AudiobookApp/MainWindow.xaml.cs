using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AudiobookApp.ViewModels;
using AudiobookApp.Models;
using System.Linq;
using AudiobookApp.Views;
using WinRT.Interop;
using Windows.Storage.Pickers;
using Windows.Storage.FileProperties;

namespace AudiobookApp
{
    public sealed partial class MainWindow : Window
    {
        public MainViewModel VM { get; } = new();

        private readonly BooksPage _booksPage = new BooksPage();

        private SidebarItem? _selectedSidebarItem;

        private string categoriesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudiobookApp",
            "categories.json"
        );

        private string booksPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudiobookApp",
            "books.json"
        );

        public MainWindow()
        {
            this.InitializeComponent();

            LoadCategories();
            LoadBooks();

            _booksPage.ImportRequested += async (_, _) =>
            {
                await ImportBookAsync();
            };

            _booksPage.RemoveRequested += (book) =>
            {
                VM.Books.Remove(book);
                SaveBooks();
                RefreshCurrentView();
            };

            _booksPage.BookSelected += OpenBook;

            LoadList();
            BuildSidebar();

            RefreshBooksView();
        }

        // ---------------- SIDEBAR SETUP ----------------

        private void LoadList()
        {
            CategoryList.ItemsSource = VM.SidebarItems;

            CategoryList.Items.VectorChanged += (s, e) =>
            {
                SyncOrderFromUI();
            };
        }

        private void BuildSidebar()
        {
            VM.SidebarItems.Clear();

            foreach (var cat in VM.Categories.OrderBy(c => c.Order))
            {
                VM.SidebarItems.Add(new SidebarItem
                {
                    Name = cat.Name,
                    Type = SidebarItemType.Category,
                    Category = cat
                });
            }
        }

        // ---------------- NAVIGATION ----------------

        private void RefreshBooksView(string? categoryName = null)
        {
            IEnumerable<Book> books = VM.Books;

            if (!string.IsNullOrWhiteSpace(categoryName))
            {
                books = VM.Books.Where(b =>
                    string.Equals(
                        b.CategoryName,
                        categoryName,
                        StringComparison.OrdinalIgnoreCase));
            }

            _booksPage.SetBooks(books);
            ContentFrame.Content = _booksPage;
        }

        private void RefreshCurrentView()
        {
            RefreshBooksView(_selectedSidebarItem?.Category?.Name);
        }

        private void NavigateTo(SidebarItem item)
        {
            _selectedSidebarItem = item;

            if (item?.Category == null)
            {
                RefreshBooksView(); // All Books
                return;
            }

            RefreshBooksView(item.Category.Name);
        }

        // ---------------- SIDEBAR CLICK ----------------

        private void AllBooks_Click(object sender, RoutedEventArgs e)
        {
            _selectedSidebarItem = null;
            CategoryList.SelectedItem = null;
            RefreshBooksView();
        }

        private void CategoryList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SidebarItem item)
            {
                NavigateTo(item);
            }
        }

        // ---------------- ADD CATEGORY ----------------

        private async void AddCategory_Click(object sender, RoutedEventArgs e)
        {
            await ShowAddCategoryDialog();
        }

        private async Task ShowAddCategoryDialog()
        {
            var input = new TextBox
            {
                PlaceholderText = "Enter category name"
            };

            var dialog = new ContentDialog
            {
                Title = "Add Category",
                Content = input,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                var name = input.Text?.Trim();

                if (!string.IsNullOrEmpty(name))
                {
                    VM.Categories.Add(new LibraryCategory
                    {
                        Name = name
                    });

                    SaveCategories();
                    BuildSidebar();
                }
            }
        }

        // ---------------- IMPORT BOOKS ----------------

        private async Task ImportBookAsync()
        {
            var picker = new FileOpenPicker();

            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4b");
            picker.FileTypeFilter.Add(".mp4");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();

            if (file == null)
                return;

            var properties = await file.Properties.GetMusicPropertiesAsync();

            var book = new Book
            {
                Title = string.IsNullOrWhiteSpace(properties.Title)
                    ? Path.GetFileNameWithoutExtension(file.Name)
                    : properties.Title,

                Author = string.IsNullOrWhiteSpace(properties.Artist)
                    ? "Unknown Author"
                    : properties.Artist,

                FilePath = file.Path
            };

            var dialog = new ImportBookDialog(book, VM.Categories);

            dialog.CancelRequested += () =>
            {
                _selectedSidebarItem = null;
                CategoryList.SelectedItem = null;
                RefreshBooksView();
            };

            dialog.ImportConfirmed += (b) =>
            {
                if (VM.Books.Any(x => x.FilePath == b.FilePath))
                {
                    _ = new ContentDialog
                    {
                        Title = "Duplicate Book",
                        Content = "This book already exists in your library.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();

                    return;
                }

                VM.Books.Add(b);
                SaveBooks();

                RefreshCurrentView();
            };

            ContentFrame.Content = dialog;
        }

        // ---------------- EDIT CATEGORY ----------------

        private async void RenameCategory_Click(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as SidebarItem;
            if (item?.Category == null) return;

            var input = new TextBox { Text = item.Category.Name };

            var dialog = new ContentDialog
            {
                Title = "Rename Category",
                Content = input,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                var oldName = item.Category.Name;
                item.Category.Name = input.Text.Trim();

                foreach (var book in VM.Books.Where(b =>
                    string.Equals(b.CategoryName, oldName, StringComparison.OrdinalIgnoreCase)))
                {
                    book.CategoryName = item.Category.Name;
                }

                SaveCategories();
                SaveBooks();
                BuildSidebar();
                RefreshCurrentView();
            }
        }

        private void DeleteCategory_Click(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as SidebarItem;
            if (item?.Category == null) return;

            VM.Categories.Remove(item.Category);

            SaveCategories();
            BuildSidebar();
        }

        // ---------------- PERSISTENCE ----------------

        private void SyncOrderFromUI()
        {
            for (int i = 0; i < VM.SidebarItems.Count; i++)
            {
                var item = VM.SidebarItems[i];

                if (item.Type == SidebarItemType.Category && item.Category != null)
                {
                    item.Category.Order = i;
                }
            }

            SaveCategories();
        }

        private void SaveCategories()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(categoriesPath)!);
            File.WriteAllText(categoriesPath, JsonSerializer.Serialize(VM.Categories));
        }

        private void SaveBooks()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(booksPath)!);
            File.WriteAllText(booksPath, JsonSerializer.Serialize(VM.Books));
        }

        private void LoadCategories()
        {
            if (!File.Exists(categoriesPath))
                return;

            var json = File.ReadAllText(categoriesPath);

            var list = JsonSerializer.Deserialize<List<LibraryCategory>>(json);

            if (list == null)
                return;

            VM.Categories.Clear();

            foreach (var item in list.OrderBy(c => c.Order))
            {
                VM.Categories.Add(item);
            }
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
                VM.Books.Add(book);
            }
        }

        //--------- Player --------
        private void OpenBook(Book book)
        {
            var playerPage = new PlayerPage(book);

            ContentFrame.Content = playerPage;
        }
    }
}
