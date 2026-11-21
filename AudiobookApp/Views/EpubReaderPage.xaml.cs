using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using AudiobookApp.Models;
using VersOne.Epub;
using Windows.Storage;
using Windows.UI;

namespace AudiobookApp.Views
{
    public sealed partial class EpubReaderPage : Page
    {
        private const string DarkModeSettingKey = "EpubReaderDarkMode";

        private readonly Book _book;
        private readonly List<EpubChapterContent> _chapters = new();
        private int _chapterIndex;
        private bool _isDarkMode;
        private bool _suppressScrollPersist;
        private double _pendingScrollRatio = -1;

        public event Action<Book>? ProgressChanged;

        public EpubReaderPage(Book book)
        {
            InitializeComponent();
            _book = book;

            BookTitleText.Text = book.Title;
            _isDarkMode = ReadDarkModePreference();
            ApplyTheme();

            Loaded += async (_, _) => await LoadBookAsync();
            Unloaded += (_, _) => PersistProgress();
            PageScroller.ViewChanged += PageScroller_ViewChanged;
            PageScroller.SizeChanged += PageScroller_SizeChanged;
        }

        private void DarkModeButton_Click(object sender, RoutedEventArgs e)
        {
            _isDarkMode = !_isDarkMode;
            SaveDarkModePreference(_isDarkMode);
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            if (_isDarkMode)
            {
                RootPage.Background = BrushFromHex("#1A1714");
                BookPageBorder.Background = BrushFromHex("#252019");
                BookPageBorder.BorderBrush = BrushFromHex("#3D342C");
                BookTitleText.Foreground = BrushFromHex("#E8DFD0");
                ChapterTitleText.Foreground = BrushFromHex("#B8A99A");
                PageText.Foreground = BrushFromHex("#EDE6DB");
                ProgressText.Foreground = BrushFromHex("#B8A99A");

                StyleNavButton(PreviousChapterButton, "#3D342C", "#E8DFD0", "#5A4E42");
                StyleNavButton(NextChapterButton, "#3D342C", "#E8DFD0", "#5A4E42");
                StyleNavButton(DarkModeButton, "#3D342C", "#E8DFD0", "#5A4E42");
                DarkModeButton.Content = "Light mode";
            }
            else
            {
                RootPage.Background = BrushFromHex("#E8DFD0");
                BookPageBorder.Background = BrushFromHex("#FBF6EB");
                BookPageBorder.BorderBrush = BrushFromHex("#C9B79A");
                BookTitleText.Foreground = BrushFromHex("#3B2F2F");
                ChapterTitleText.Foreground = BrushFromHex("#6B5A4E");
                PageText.Foreground = BrushFromHex("#2C2420");
                ProgressText.Foreground = BrushFromHex("#6B5A4E");

                StyleNavButton(PreviousChapterButton, "#6B5344", "#FBF6EB", "#4E3C31");
                StyleNavButton(NextChapterButton, "#6B5344", "#FBF6EB", "#4E3C31");
                StyleNavButton(DarkModeButton, "#6B5344", "#FBF6EB", "#4E3C31");
                DarkModeButton.Content = "Dark mode";
            }
        }

        private static void StyleNavButton(Button button, string background, string foreground, string border)
        {
            button.Background = BrushFromHex(background);
            button.Foreground = BrushFromHex(foreground);
            button.BorderBrush = BrushFromHex(border);
        }

        private static SolidColorBrush BrushFromHex(string hex)
        {
            hex = hex.TrimStart('#');
            byte a = 255;
            if (hex.Length == 8)
            {
                a = Convert.ToByte(hex[..2], 16);
                hex = hex[2..];
            }

            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex.Substring(2, 2), 16);
            var b = Convert.ToByte(hex.Substring(4, 2), 16);
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }

        private static bool ReadDarkModePreference()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                return values.TryGetValue(DarkModeSettingKey, out var value) && value is bool dark && dark;
            }
            catch
            {
                return false;
            }
        }

        private static void SaveDarkModePreference(bool isDark)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[DarkModeSettingKey] = isDark;
            }
            catch
            {
                // Ignore persistence failures; theme still applies for this session.
            }
        }

        private async Task LoadBookAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;

                var epub = await EpubReader.ReadBookAsync(_book.FilePath);
                _chapters.Clear();

                if (!string.IsNullOrWhiteSpace(epub.Title))
                    BookTitleText.Text = epub.Title;

                foreach (var file in epub.ReadingOrder)
                {
                    var html = file.Content ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(html))
                        continue;

                    var plain = HtmlToPlainText(html);
                    if (string.IsNullOrWhiteSpace(plain))
                        continue;

                    var title = ExtractTitle(html) ?? $"Section {_chapters.Count + 1}";
                    _chapters.Add(new EpubChapterContent(title, html, plain));
                }

                if (_chapters.Count == 0)
                {
                    ChapterTitleText.Text = "No readable content found.";
                    ProgressText.Text = "";
                    PreviousChapterButton.IsEnabled = false;
                    NextChapterButton.IsEnabled = false;
                    return;
                }

                _chapterIndex = Math.Clamp(_book.LastChapterIndex, 0, _chapters.Count - 1);
                ShowChapter(_chapterIndex, restoreScroll: true);
            }
            catch (Exception ex)
            {
                ChapterTitleText.Text = "Unable to open this EPUB.";
                ProgressText.Text = ex.Message;
                PreviousChapterButton.IsEnabled = false;
                NextChapterButton.IsEnabled = false;
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }

            await Task.CompletedTask;
        }

        private void ShowChapter(int index, bool restoreScroll = false)
        {
            if (index < 0 || index >= _chapters.Count)
                return;

            _chapterIndex = index;
            var chapter = _chapters[index];

            ChapterTitleText.Text = chapter.Title;
            ProgressText.Text = $"Page {index + 1} of {_chapters.Count}";
            PreviousChapterButton.IsEnabled = index > 0;
            NextChapterButton.IsEnabled = index < _chapters.Count - 1;

            _suppressScrollPersist = true;
            RenderChapter(chapter);

            if (restoreScroll && _book.LastEpubScrollRatio > 0)
            {
                _pendingScrollRatio = Math.Clamp(_book.LastEpubScrollRatio, 0, 1);
                PageScroller.UpdateLayout();
                TryApplyPendingScroll();
            }
            else
            {
                _pendingScrollRatio = -1;
                _book.LastEpubScrollRatio = 0;
                PageScroller.ChangeView(0, 0, null, true);
            }

            _suppressScrollPersist = false;

            _book.LastChapterIndex = index;
            if (_chapters.Count > 1)
            {
                _book.LastPositionSeconds = index;
                _book.Duration = TimeSpan.FromSeconds(_chapters.Count - 1);
            }

            ProgressChanged?.Invoke(_book);
        }

        private void PageScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_suppressScrollPersist || e.IsIntermediate)
                return;

            CaptureScrollRatio();
        }

        private void PageScroller_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_pendingScrollRatio >= 0)
                TryApplyPendingScroll();
        }

        private void TryApplyPendingScroll()
        {
            if (_pendingScrollRatio < 0)
                return;

            PageScroller.UpdateLayout();
            var scrollable = PageScroller.ScrollableHeight;
            if (scrollable <= 1)
                return;

            var offset = _pendingScrollRatio * scrollable;
            _suppressScrollPersist = true;
            PageScroller.ChangeView(null, offset, null, true);
            _suppressScrollPersist = false;
            _pendingScrollRatio = -1;
        }

        private void CaptureScrollRatio()
        {
            var scrollable = PageScroller.ScrollableHeight;
            _book.LastEpubScrollRatio = scrollable > 1
                ? Math.Clamp(PageScroller.VerticalOffset / scrollable, 0, 1)
                : 0;
        }

        private void RenderChapter(EpubChapterContent chapter)
        {
            PageText.Blocks.Clear();

            var paragraphs = ExtractParagraphs(chapter.Html);
            if (paragraphs.Count == 0)
            {
                foreach (var chunk in SplitPlainParagraphs(chapter.PlainText))
                    PageText.Blocks.Add(CreateParagraph(chunk));
                return;
            }

            foreach (var text in paragraphs)
                PageText.Blocks.Add(CreateParagraph(text));
        }

        private static Paragraph CreateParagraph(string text)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 16)
            };
            paragraph.Inlines.Add(new Run { Text = text });
            return paragraph;
        }

        private void PreviousChapter_Click(object sender, RoutedEventArgs e)
        {
            if (_chapterIndex > 0)
                ShowChapter(_chapterIndex - 1, restoreScroll: false);
        }

        private void NextChapter_Click(object sender, RoutedEventArgs e)
        {
            if (_chapterIndex < _chapters.Count - 1)
                ShowChapter(_chapterIndex + 1, restoreScroll: false);
        }

        private void PersistProgress()
        {
            CaptureScrollRatio();
            _book.LastChapterIndex = _chapterIndex;
            if (_chapters.Count > 1)
            {
                _book.LastPositionSeconds = _chapterIndex;
                _book.Duration = TimeSpan.FromSeconds(_chapters.Count - 1);
            }

            ProgressChanged?.Invoke(_book);
        }

        private static List<string> ExtractParagraphs(string html)
        {
            var results = new List<string>();
            var body = ExtractBody(html);

            foreach (Match match in Regex.Matches(
                         body,
                         @"<(p|h[1-6]|div|li|blockquote)(\s[^>]*)?>([\s\S]*?)</\1>",
                         RegexOptions.IgnoreCase))
            {
                var inner = match.Groups[3].Value;
                inner = Regex.Replace(inner, @"<(br|BR)\s*/?>", "\n");
                inner = Regex.Replace(inner, "<.*?>", string.Empty);
                inner = WebUtility.HtmlDecode(inner).Trim();
                inner = Regex.Replace(inner, @"[ \t]+\n", "\n");
                inner = Regex.Replace(inner, @"\n{3,}", "\n\n");

                if (!string.IsNullOrWhiteSpace(inner))
                    results.Add(inner);
            }

            return results;
        }

        private static IEnumerable<string> SplitPlainParagraphs(string plain)
        {
            foreach (var part in Regex.Split(plain, @"\n\s*\n"))
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    yield return trimmed;
            }
        }

        private static string ExtractBody(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            var match = Regex.Match(
                html,
                @"<body[^>]*>([\s\S]*?)</body>",
                RegexOptions.IgnoreCase);

            return match.Success ? match.Groups[1].Value : html;
        }

        private static string? ExtractTitle(string html)
        {
            var h1 = Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (h1.Success)
                return CleanInline(h1.Groups[1].Value);

            var title = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (title.Success)
            {
                var value = CleanInline(title.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static string HtmlToPlainText(string html)
        {
            var body = ExtractBody(html);
            body = Regex.Replace(body, @"<(br|BR)\s*/?>", "\n");
            body = Regex.Replace(body, @"</(p|h[1-6]|div|li|blockquote)>", "\n\n", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "<.*?>", string.Empty);
            return WebUtility.HtmlDecode(body).Trim();
        }

        private static string CleanInline(string html)
        {
            return WebUtility.HtmlDecode(Regex.Replace(html, "<.*?>", string.Empty)).Trim();
        }

        private sealed class EpubChapterContent
        {
            public EpubChapterContent(string title, string html, string plainText)
            {
                Title = title;
                Html = html;
                PlainText = plainText;
            }

            public string Title { get; }
            public string Html { get; }
            public string PlainText { get; }
        }
    }
}
