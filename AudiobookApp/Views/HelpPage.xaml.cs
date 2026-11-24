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
                    "Use the left pane to switch library scope. Full Library shows audiobooks and novels together. Audiobooks and Novels narrow the list to one media type.\n\n" +
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
                    "• Click a series tile to open its members. From there you can open a book, or right-click a row to rate, edit, add tags, or remove it from the series.\n" +
                    "• On a series tile: Reorder series…, Rename series, or Disband series (books stay in the library).",
                    ("series.png", "Series members panel with covers, ratings, and a centered Close button.")),

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
                    "• Remove — in the library, deletes the book; inside an open series, removes it from that series.",
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
                    "Transport controls cover play/pause, −10s / +10s, and previous/next chapter when chapters exist. Below them you can adjust volume, playback speed, sleep timer, and series autoplay (when the book is in a series).\n\n" +
                    "Use Back to window to return to the library with the collapsed player, or Shrink to mini player for the floating mini player. Progress is saved automatically as you listen.",
                    ("audiobook-player.png", "Full audiobook player with transport, volume, speed, sleep, and shrink controls.")),

                new HelpSection(
                    "Collapsed player",
                    "While an audiobook session is active and you leave the full player, a collapsed player appears at the bottom of the window.\n\n" +
                    "It offers transport, seek, volume, speed, sleep timer, and series autoplay. Full screen opens the full player. Shrink to mini player collapses the app into the floating mini player. Close pauses and hides the bar.",
                    ("collapsed-player.png", "Collapsed player bar along the bottom of the main window.")),

                new HelpSection(
                    "Mini player",
                    "The floating mini player is a small always-available window you can drag around the desktop.\n\n" +
                    "It shows cover art, scrolling title details, transport controls, volume, speed, sleep timer, and series autoplay. Use the chapter progress bar under the text to scrub within the current chapter.\n\n" +
                    "Full screen restores the main app. Pin locks the player in place and keeps it on top. Volume and speed pop out above the window so the sliders stay easy to use.",
                    ("mini-player.png", "Floating mini player with transport, utilities, expand, and pin.")),

                new HelpSection(
                    "EPUB reader",
                    "Open an EPUB to read in the built-in reader.\n\n" +
                    "The left toolbar has Contents (table of contents), Bookmarks, Search, Highlight, and Listen. Previous / Next and the chapter list at the bottom move between sections. Reading position is saved automatically.\n\n" +
                    "• Bookmarks — add a named bookmark at your current place; open Bookmarks to jump or delete.\n" +
                    "• Settings (gear, top right) — appearance (light/dark), text size, font, line spacing, page width, and text-to-speech voice, volume, speed, sleep timer, and Stop.\n" +
                    "• Highlights — select text, right-click, and choose a color (or Highlight + note). Notes appear in the foldable right margin — click a long note to expand it. Nearby notes share a tab strip colored by highlight; numbered tabs stay distinct even when colors match. Open Highlights to browse them (note above text, chapter and position below); right-click a list item or a margin note to edit or delete.\n" +
                    "• Dictionary — select a word, right-click, and choose Look up… for an offline English definition (Princeton WordNet).\n" +
                    "• Listen — play or pause continuous speech across chapters. Listening resumes where you left off. Set a sleep timer in Settings to stop after 5–60 minutes.",
                    ("epub-reader.png", "EPUB reader with highlights, margin notes, and nearby-note tabs.")),
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
