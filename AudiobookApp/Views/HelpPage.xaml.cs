using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AudiobookApp.Views
{
    public sealed partial class HelpPage : Page
    {
        private readonly ObservableCollection<HelpSection> _sections = new();

        public HelpPage()
        {
            InitializeComponent();
            foreach (var section in BuildSections())
                _sections.Add(section);

            SectionsList.ItemsSource = _sections;
            _ = LoadImagesAsync();
        }

        private static IEnumerable<HelpSection> BuildSections() =>
            new[]
            {
                new HelpSection(
                    "Getting around",
                    "Use the left pane to switch library scope. Full Library shows audiobooks and EPUBs together. Audiobooks and Epubs narrow the list to one media type.\n\n" +
                    "Help and Clear library are at the bottom of the left pane. Clear library removes every book and series after you confirm.",
                    ("getting-around.png", "Left pane: library tabs above, Help and Clear library at the bottom.")),

                new HelpSection(
                    "Importing books",
                    "Click Import Book on the library toolbar, then choose how to bring files in.\n\n" +
                    "• Single file — pick one .mp3, .m4b, .mp4, or .epub, then set title, author, optional cover, tags, and optional series.\n" +
                    "• Multiple audio files — pick several audio parts, arrange their order, then finish the same import details.\n" +
                    "• Entire directory — scans a folder, opens a review page where you can edit titles, merge or split groups, filter by type, and import the selection.",
                    ("importing-books.png", "Import Book opens a dialog with Single file, Multiple audio files, and Entire directory.")),

                new HelpSection(
                    "Combine into series (directory import)",
                    "After a directory import finishes, you can choose to combine books into series.\n\n" +
                    "Check books under Not in a series, then use Add to series or Create new series. Books already in a series appear under that series name without checkboxes—drag them to set order (numbers show on the left). Remove from series returns a book to the ungrouped list. Done keeps the import; Cancel rolls it back and returns to the review page.",
                    ("combine-into-series.png", "Combine into series: collapsible series groups with drag reorder and order numbers.")),

                new HelpSection(
                    "Search",
                    "The search box on the library toolbar filters as you type.\n\n" +
                    "It matches book title or author. Series also appear when the series name matches, even if member titles do not.",
                    ("search.png", "Search box on the library toolbar, next to Import Book and Filter.")),

                new HelpSection(
                    "Filters",
                    "Open Filter on the toolbar to narrow the library.\n\n" +
                    "• Progress: Started, Not started, or Completed (one at a time).\n" +
                    "• Library type: Series only or Singles only (one at a time).\n" +
                    "• Clear filters removes progress and series/singles filters.\n\n" +
                    "When any filter or search is active, a summary line appears under the toolbar.",
                    ("filters.png", "Filter flyout with progress toggles, series/singles toggles, and Clear filters.")),

                new HelpSection(
                    "Tags",
                    "Right-click a book and choose Add tags to assign existing tags or create new ones. Suggested tags are offered as a starting point.\n\n" +
                    "In the Filter flyout, use Filter by tag to show only books that have every selected tag.",
                    ("tags.png", "Add tags from a book’s context menu; Filter by tag lives in the Filter flyout.")),

                new HelpSection(
                    "Series",
                    "Series group related books under one library tile with stacked covers.\n\n" +
                    "• Create series… — select two or more books, confirm, reorder, then name the series.\n" +
                    "• Add to series… — place a book into an existing series.\n" +
                    "• Click a series tile to open its members. From there you can open, rate, edit, tag, or remove a book from the series or library.\n" +
                    "• On a series tile: Reorder series…, Rename series, or Disband series (books stay in the library).",
                    ("series.png", "Series members dialog with covers and Remove on each book.")),

                new HelpSection(
                    "Ratings",
                    "Rate any book from 0 to 5 stars. Choose Rate from a book’s right-click menu, or from a book inside an open series. You can clear a rating later if you change your mind.\n\n" +
                    "Individual ratings appear under each book in the library and in the series members list.\n\n" +
                    "When you open a series, the stars under the series name are the average of every book in that series that has a rating. Unrated books are left out of the average, so a series rating only reflects books you have scored.",
                    ("ratings.png", "Open series: combined series rating at the top, individual book ratings on each row.")),

                new HelpSection(
                    "Editing books",
                    "Right-click a book (or a book row inside a series dialog) for book actions.\n\n" +
                    "• Edit — change title, author, and cover image.\n" +
                    "• Rate — set a 0–5 star rating or clear it.\n" +
                    "• Remove — delete the book from your library (or choose remove from series when prompted).",
                    ("editing-books.png", "Right-click a book for Rate, Edit, Add tags, and Remove.")),

                new HelpSection(
                    "Library views",
                    "Use List and Large icons on the toolbar to switch how the library is shown.\n\n" +
                    "Large icons emphasize covers. Series tiles show stacked covers from member books. List view shows denser rows with the same actions.",
                    ("library-views-list.png", "List view with List selected on the toolbar."),
                    ("library-views-large.png", "Large icons view with Large icons selected on the toolbar.")),

                new HelpSection(
                    "Audiobook player",
                    "Open an audiobook to use the full player.\n\n" +
                    "Controls include play/pause, seek, −10s / +10s, previous/next chapter (when chapters exist), playback speed, and volume. Progress is saved automatically as you listen.",
                    ("audiobook-player.png", "Full audiobook player with transport controls, speed, and volume.")),

                new HelpSection(
                    "Mini player",
                    "While an audiobook session is active and you leave the full player, a mini player appears at the bottom of the window.\n\n" +
                    "It offers the main transport controls, seek, speed, and volume. Expand returns to the full player. Close pauses and hides the bar.",
                    ("mini-player.png", "Mini player bar along the bottom of the main window.")),

                new HelpSection(
                    "EPUB reader",
                    "Open an EPUB to read in the built-in reader.\n\n" +
                    "Move between chapters with Previous / Next, toggle Dark mode, and track reading progress. Position is saved automatically so you can resume later.",
                    ("epub-reader.png", "EPUB reader with chapter navigation and Dark mode.")),
            };

        private async Task LoadImagesAsync()
        {
            foreach (var section in _sections)
            {
                foreach (var image in section.Images)
                    await image.TryLoadAsync();
            }
        }

        private sealed class HelpSection
        {
            public HelpSection(string title, string body, params (string FileName, string Caption)[] images)
            {
                Title = title;
                Body = body;
                Images = new ObservableCollection<HelpImage>();
                foreach (var (fileName, caption) in images)
                    Images.Add(new HelpImage(fileName, caption));
            }

            public string Title { get; }
            public string Body { get; }
            public ObservableCollection<HelpImage> Images { get; }
        }

        private sealed class HelpImage : INotifyPropertyChanged
        {
            private ImageSource? _imageSource;

            public HelpImage(string fileName, string caption)
            {
                FileName = fileName;
                Caption = caption;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string FileName { get; }
            public string Caption { get; }

            public string PlaceholderText => $"Screenshot pending: {FileName}";

            public ImageSource? ImageSource
            {
                get => _imageSource;
                private set
                {
                    _imageSource = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageVisibility)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaceholderVisibility)));
                }
            }

            public Visibility ImageVisibility =>
                ImageSource == null ? Visibility.Collapsed : Visibility.Visible;

            public Visibility PlaceholderVisibility =>
                ImageSource == null ? Visibility.Visible : Visibility.Collapsed;

            public async Task TryLoadAsync()
            {
                try
                {
                    StorageFile? file = null;
                    try
                    {
                        file = await StorageFile.GetFileFromApplicationUriAsync(
                            new Uri($"ms-appx:///Assets/Help/{FileName}"));
                    }
                    catch
                    {
                        var diskPath = Path.Combine(
                            AppContext.BaseDirectory,
                            "Assets",
                            "Help",
                            FileName);
                        if (File.Exists(diskPath))
                            file = await StorageFile.GetFileFromPathAsync(diskPath);
                    }

                    if (file == null)
                    {
                        ImageSource = null;
                        return;
                    }

                    using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    ImageSource = bitmap;
                }
                catch
                {
                    ImageSource = null;
                }
            }
        }
    }
}
