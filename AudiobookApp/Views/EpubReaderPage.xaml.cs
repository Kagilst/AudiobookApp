using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AudiobookApp.Models;
using AudiobookApp.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VersOne.Epub;
using Windows.Storage;
using Windows.System;
using Windows.UI.Text;

namespace AudiobookApp.Views
{
    public sealed partial class EpubReaderPage : Page
    {
        private const string DarkModeSettingKey = "EpubReaderDarkMode";
        private const string DarkModeFollowsSystemMigratedKey = "EpubReaderDarkModeFollowsSystem";

        private const double MinFontSize = 14;
        private const double MaxFontSize = 28;
        private const double DefaultFontSize = 18;

        private readonly Book _book;
        private readonly List<EpubChapterContent> _chapters = new();
        private readonly List<TocEntry> _tocEntries = new();
        private readonly ObservableCollection<BookmarkListItem> _bookmarkItems = new();
        private readonly List<SearchHit> _searchHits = new();
        private int _chapterIndex;
        private int _searchHitIndex = -1;
        /// <summary>Null means follow Windows light/dark setting.</summary>
        private bool? _darkModeOverride;
        private bool _suppressScrollPersist;
        private bool _suppressChapterCombo;
        private bool _suppressDisplayControls;
        private bool _ignoreNextBookmarkItemClick;
        private double _pendingScrollRatio = -1;

        private double _fontSize = DefaultFontSize;
        private string _fontFamilyKey = "serif";
        private string _lineSpacingKey = "normal";
        private string _contentWidthKey = "medium";

        public event Action<Book>? ProgressChanged;

        public EpubReaderPage(Book book)
        {
            InitializeComponent();
            _book = book;

            BookTitleText.Text = book.Title;
            _book.EpubBookmarks ??= new List<EpubBookmark>();
            _darkModeOverride = ReadDarkModePreference();
            LoadReadingPreferences();
            ApplyTheme();
            ApplyReadingPreferences(restoreScroll: false);
            SyncDisplayControls();
            BookmarkList.ItemsSource = _bookmarkItems;
            RefreshBookmarkList();

            Loaded += async (_, _) => await LoadBookAsync();
            Unloaded += (_, _) =>
            {
                PersistProgress();
                SaveReadingPreferences();
            };
            PageScroller.ViewChanged += PageScroller_ViewChanged;
            PageScroller.SizeChanged += PageScroller_SizeChanged;
        }

        private void DarkModeButton_Click(object sender, RoutedEventArgs e)
        {
            var nextDark = !IsEffectivelyDark();
            _darkModeOverride = nextDark;
            SaveDarkModePreference(nextDark);
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            // Null override → follow Windows; otherwise force light/dark for this reader.
            RootPage.RequestedTheme = _darkModeOverride switch
            {
                true => ElementTheme.Dark,
                false => ElementTheme.Light,
                _ => ElementTheme.Default
            };
            DarkModeButton.Content = IsEffectivelyDark() ? "Light mode" : "Dark mode";
        }

        private void LoadReadingPreferences()
        {
            var settings = EpubReaderSettings.Load();
            _fontSize = Math.Clamp(settings.FontSize, MinFontSize, MaxFontSize);

            _fontFamilyKey = settings.FontFamily is "serif" or "sans"
                ? settings.FontFamily
                : "serif";

            _lineSpacingKey = settings.LineSpacing is "tight" or "normal" or "relaxed"
                ? settings.LineSpacing
                : "normal";

            _contentWidthKey = settings.ContentWidth is "narrow" or "medium" or "wide"
                ? settings.ContentWidth
                : "medium";
        }

        private void SaveReadingPreferences()
        {
            new EpubReaderSettings
            {
                FontSize = _fontSize,
                FontFamily = _fontFamilyKey,
                LineSpacing = _lineSpacingKey,
                ContentWidth = _contentWidthKey
            }.Save();
        }

        private void SyncDisplayControls()
        {
            _suppressDisplayControls = true;

            FontSizeLabel.Text = $"{_fontSize:0} pt";
            FontSizeMinusButton.IsEnabled = _fontSize > MinFontSize;
            FontSizePlusButton.IsEnabled = _fontSize < MaxFontSize;

            SelectComboByTag(FontFamilyCombo, _fontFamilyKey);
            SelectComboByTag(LineSpacingCombo, _lineSpacingKey);
            SelectComboByTag(ContentWidthCombo, _contentWidthKey);

            _suppressDisplayControls = false;
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item
                    && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

        private static string? GetSelectedTag(ComboBox combo)
        {
            return combo.SelectedItem is ComboBoxItem item ? item.Tag as string : null;
        }

        private void ApplyReadingPreferences(bool restoreScroll)
        {
            double scrollRatio = 0;
            if (restoreScroll)
            {
                CaptureScrollRatioQuiet();
                scrollRatio = Math.Clamp(_book.LastEpubScrollRatio, 0, 1);
            }

            var fontFamily = _fontFamilyKey == "sans"
                ? new FontFamily("Segoe UI")
                : new FontFamily("Georgia");

            PageText.FontFamily = fontFamily;
            PageText.FontSize = _fontSize;
            PageText.LineHeight = GetLineHeight(_fontSize, _lineSpacingKey);

            BookTitleText.FontFamily = fontFamily;
            ChapterTitleText.FontFamily = fontFamily;

            var maxWidth = GetContentMaxWidth(_contentWidthKey);
            BookPageBorder.MaxWidth = maxWidth;
            FooterNavGrid.MaxWidth = maxWidth;

            FontSizeLabel.Text = $"{_fontSize:0} pt";
            FontSizeMinusButton.IsEnabled = _fontSize > MinFontSize;
            FontSizePlusButton.IsEnabled = _fontSize < MaxFontSize;

            // Re-render so heading sizes / margins track the current base font size.
            if (_chapters.Count > 0 && _chapterIndex >= 0 && _chapterIndex < _chapters.Count)
            {
                _suppressScrollPersist = true;
                RenderChapter(_chapters[_chapterIndex]);
                _suppressScrollPersist = false;
            }

            if (!restoreScroll)
                return;

            _pendingScrollRatio = scrollRatio;
            PageScroller.UpdateLayout();
            TryApplyPendingScroll();
        }

        private static double GetLineHeight(double fontSize, string spacingKey) =>
            spacingKey switch
            {
                "tight" => fontSize * 1.4,
                "relaxed" => fontSize * 2.1,
                _ => fontSize * 1.75
            };

        private static double GetContentMaxWidth(string widthKey) =>
            widthKey switch
            {
                "narrow" => 640,
                "wide" => 1040,
                _ => 820
            };

        private void ChangeFontSize(double delta)
        {
            var next = Math.Clamp(_fontSize + delta, MinFontSize, MaxFontSize);
            if (Math.Abs(next - _fontSize) < 0.01)
                return;

            CaptureScrollRatioQuiet();
            _fontSize = next;
            SaveReadingPreferences();
            SyncDisplayControls();
            ApplyReadingPreferences(restoreScroll: true);
        }

        private void CaptureScrollRatioQuiet()
        {
            var scrollable = PageScroller.ScrollableHeight;
            if (scrollable > 1)
                _book.LastEpubScrollRatio = Math.Clamp(PageScroller.VerticalOffset / scrollable, 0, 1);
        }

        private void FontSizeMinusButton_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-2);

        private void FontSizePlusButton_Click(object sender, RoutedEventArgs e) => ChangeFontSize(2);

        private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDisplayControls)
                return;

            var tag = GetSelectedTag(FontFamilyCombo);
            if (string.IsNullOrWhiteSpace(tag) || tag == _fontFamilyKey)
                return;

            CaptureScrollRatioQuiet();
            _fontFamilyKey = tag;
            SaveReadingPreferences();
            ApplyReadingPreferences(restoreScroll: true);
        }

        private void LineSpacingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDisplayControls)
                return;

            var tag = GetSelectedTag(LineSpacingCombo);
            if (string.IsNullOrWhiteSpace(tag) || tag == _lineSpacingKey)
                return;

            CaptureScrollRatioQuiet();
            _lineSpacingKey = tag;
            SaveReadingPreferences();
            ApplyReadingPreferences(restoreScroll: true);
        }

        private void ContentWidthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDisplayControls)
                return;

            var tag = GetSelectedTag(ContentWidthCombo);
            if (string.IsNullOrWhiteSpace(tag) || tag == _contentWidthKey)
                return;

            CaptureScrollRatioQuiet();
            _contentWidthKey = tag;
            SaveReadingPreferences();
            ApplyReadingPreferences(restoreScroll: true);
        }

        private static bool? ReadDarkModePreference()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;

                // One-time: drop old forced light/dark so the reader defaults to Windows.
                if (!values.ContainsKey(DarkModeFollowsSystemMigratedKey))
                {
                    values.Remove(DarkModeSettingKey);
                    values[DarkModeFollowsSystemMigratedKey] = true;
                }

                if (values.TryGetValue(DarkModeSettingKey, out var value) && value is bool dark)
                    return dark;

                // No override saved — follow Windows light/dark.
                return null;
            }
            catch
            {
                return null;
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

        private static bool IsSystemInDarkMode()
        {
            try
            {
                return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
            catch
            {
                return false;
            }
        }

        private bool IsEffectivelyDark()
        {
            if (_darkModeOverride is bool forced)
                return forced;

            return RootPage.ActualTheme == ElementTheme.Dark || IsSystemInDarkMode();
        }

        private async Task LoadBookAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;

                var epub = await EpubReader.ReadBookAsync(_book.FilePath);
                _chapters.Clear();
                _tocEntries.Clear();

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

                    var fileKey = file.Key ?? file.FilePath ?? string.Empty;
                    var filePath = file.FilePath ?? fileKey;
                    var title = ExtractTitle(html) ?? $"Section {_chapters.Count + 1}";
                    var wordCount = CountWords(plain);
                    _chapters.Add(new EpubChapterContent(title, html, plain, fileKey, filePath, wordCount));
                }

                ApplyNavigationTitles(epub, _chapters);
                _tocEntries.AddRange(BuildTocEntries(epub, _chapters));
                TocList.ItemsSource = _tocEntries;
                BindChapterCombo();

                if (_chapters.Count == 0)
                {
                    ChapterTitleText.Text = "No readable content found.";
                    ProgressText.Text = "";
                    PreviousChapterButton.IsEnabled = false;
                    NextChapterButton.IsEnabled = false;
                    ChapterCombo.IsEnabled = false;
                    TocButton.IsEnabled = false;
                    BookmarkButton.IsEnabled = false;
                    SearchButton.IsEnabled = false;
                    return;
                }

                RefreshBookmarkList();
                _chapterIndex = Math.Clamp(_book.LastChapterIndex, 0, _chapters.Count - 1);
                ShowChapter(_chapterIndex, restoreScroll: true);
            }
            catch (Exception ex)
            {
                ChapterTitleText.Text = "Unable to open this EPUB.";
                ProgressText.Text = ex.Message;
                PreviousChapterButton.IsEnabled = false;
                NextChapterButton.IsEnabled = false;
                ChapterCombo.IsEnabled = false;
                TocButton.IsEnabled = false;
                BookmarkButton.IsEnabled = false;
                SearchButton.IsEnabled = false;
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }

            await Task.CompletedTask;
        }

        private void BindChapterCombo()
        {
            _suppressChapterCombo = true;
            ChapterCombo.Items.Clear();
            for (var i = 0; i < _chapters.Count; i++)
                ChapterCombo.Items.Add($"{i + 1}. {_chapters[i].Title}");
            _suppressChapterCombo = false;
        }

        private void ShowChapter(int index, bool restoreScroll = false, double? scrollRatioOverride = null)
        {
            if (index < 0 || index >= _chapters.Count)
                return;

            _chapterIndex = index;
            var chapter = _chapters[index];

            ChapterTitleText.Text = chapter.Title;
            PreviousChapterButton.IsEnabled = index > 0;
            NextChapterButton.IsEnabled = index < _chapters.Count - 1;

            _suppressChapterCombo = true;
            if (ChapterCombo.Items.Count > index)
                ChapterCombo.SelectedIndex = index;
            _suppressChapterCombo = false;

            HighlightTocSelection(index);

            _suppressScrollPersist = true;
            RenderChapter(chapter);

            double? targetRatio = scrollRatioOverride;
            if (!targetRatio.HasValue && restoreScroll && _book.LastEpubScrollRatio > 0)
                targetRatio = _book.LastEpubScrollRatio;

            if (targetRatio is > 0)
            {
                _pendingScrollRatio = Math.Clamp(targetRatio.Value, 0, 1);
                _book.LastEpubScrollRatio = _pendingScrollRatio;
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

            UpdateProgressDisplay(GetScrollRatio());
            WriteProgressToBook();
            ProgressChanged?.Invoke(_book);
        }

        private void HighlightTocSelection(int chapterIndex)
        {
            if (_tocEntries.Count == 0)
                return;

            var match = _tocEntries.FindIndex(e => e.ChapterIndex == chapterIndex);
            if (match >= 0)
            {
                TocList.SelectedIndex = match;
                TocList.ScrollIntoView(_tocEntries[match]);
            }
        }

        private void UpdateProgressDisplay(double chapterScrollRatio)
        {
            if (_chapters.Count == 0)
            {
                ProgressText.Text = string.Empty;
                return;
            }

            chapterScrollRatio = Math.Clamp(chapterScrollRatio, 0, 1);
            var chapterPct = (int)Math.Round(chapterScrollRatio * 100);
            var bookPct = (int)Math.Round(ComputeBookProgress(chapterScrollRatio) * 100);

            ProgressText.Text =
                $"Section {_chapterIndex + 1} of {_chapters.Count}  ·  Chapter {chapterPct}%  ·  Book {bookPct}%";
        }

        private double ComputeBookProgress(double chapterScrollRatio)
        {
            var totalWords = _chapters.Sum(c => Math.Max(c.WordCount, 1));
            if (totalWords <= 0)
                return 0;

            long wordsBefore = 0;
            for (var i = 0; i < _chapterIndex; i++)
                wordsBefore += Math.Max(_chapters[i].WordCount, 1);

            var currentWords = Math.Max(_chapters[_chapterIndex].WordCount, 1);
            var progressWords = wordsBefore + (chapterScrollRatio * currentWords);
            return Math.Clamp(progressWords / totalWords, 0, 1);
        }

        private double GetScrollRatio()
        {
            var scrollable = PageScroller.ScrollableHeight;
            if (scrollable <= 1)
                return _chapters.Count == 0 ? 0 : 1;

            return Math.Clamp(PageScroller.VerticalOffset / scrollable, 0, 1);
        }

        private void PageScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_suppressScrollPersist)
                return;

            if (!e.IsIntermediate)
                CaptureScrollRatio();

            UpdateProgressDisplay(GetScrollRatio());
        }

        private void PageScroller_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_pendingScrollRatio >= 0)
                TryApplyPendingScroll();

            UpdateProgressDisplay(GetScrollRatio());
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
            UpdateProgressDisplay(GetScrollRatio());
        }

        private void CaptureScrollRatio()
        {
            var scrollable = PageScroller.ScrollableHeight;
            _book.LastEpubScrollRatio = scrollable > 1
                ? Math.Clamp(PageScroller.VerticalOffset / scrollable, 0, 1)
                : (_chapterIndex >= _chapters.Count - 1 ? 1 : 0);

            WriteProgressToBook();
            ProgressChanged?.Invoke(_book);
        }

        private void WriteProgressToBook()
        {
            if (_chapters.Count == 0)
                return;

            var scrollRatio = Math.Clamp(_book.LastEpubScrollRatio, 0, 1);
            _book.LastChapterIndex = _chapterIndex;

            // Library %: each section equally, including scroll within the current section.
            // IsStarted/IsCompleted derive from LastPositionSeconds vs Duration.
            if (_chapters.Count == 1)
            {
                _book.Duration = TimeSpan.FromSeconds(1);
                _book.LastPositionSeconds = scrollRatio >= 0.98 ? 1 : scrollRatio;
            }
            else
            {
                _book.Duration = TimeSpan.FromSeconds(_chapters.Count - 1);
                _book.LastPositionSeconds = Math.Clamp(_chapterIndex + scrollRatio, 0, _chapters.Count - 1);
            }
        }

        private void RenderChapter(EpubChapterContent chapter)
        {
            PageText.Blocks.Clear();

            var blocks = BuildRichBlocks(chapter.Html, _fontSize);
            if (blocks.Count == 0)
            {
                foreach (var chunk in SplitPlainParagraphs(chapter.PlainText))
                    PageText.Blocks.Add(CreatePlainParagraph(chunk, _fontSize));
                return;
            }

            foreach (var block in blocks)
                PageText.Blocks.Add(block);
        }

        private static Paragraph CreatePlainParagraph(string text, double baseFontSize)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 16)
            };
            paragraph.Inlines.Add(new Run { Text = text, FontSize = baseFontSize });
            return paragraph;
        }

        private static List<Block> BuildRichBlocks(string html, double baseFontSize)
        {
            var blocks = new List<Block>();
            if (string.IsNullOrWhiteSpace(html))
                return blocks;

            var body = SanitizeHtml(ExtractBody(html));
            if (string.IsNullOrWhiteSpace(body))
                return blocks;

            var state = new HtmlRenderState(baseFontSize);
            var tokens = Regex.Matches(body, @"</?[a-zA-Z][^>]*>|[^<]+");

            foreach (Match token in tokens)
            {
                var value = token.Value;
                if (value.Length == 0)
                    continue;

                if (value[0] == '<')
                    HandleHtmlTag(value, state, blocks);
                else
                    AppendDecodedText(WebUtility.HtmlDecode(value), state);
            }

            FlushParagraph(state, blocks);
            return blocks;
        }

        private static string SanitizeHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            // Drop unsafe / non-text containers entirely.
            html = Regex.Replace(
                html,
                @"<(script|style|svg|iframe|object|embed|form|noscript)(\s[^>]*)?>[\s\S]*?</\1\s*>",
                " ",
                RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<!--[\s\S]*?-->", " ");
            html = Regex.Replace(
                html,
                @"<(script|style|svg|iframe|object|embed|form|noscript|img|hr|meta|link)(\s[^>]*)?/?>",
                " ",
                RegexOptions.IgnoreCase);
            return html;
        }

        private static void HandleHtmlTag(string rawTag, HtmlRenderState state, List<Block> blocks)
        {
            var match = Regex.Match(rawTag, @"^</?\s*([a-zA-Z0-9]+)");
            if (!match.Success)
                return;

            var name = match.Groups[1].Value.ToLowerInvariant();
            var isClose = rawTag.StartsWith("</", StringComparison.Ordinal);
            var selfClosing = rawTag.EndsWith("/>", StringComparison.Ordinal)
                              || name is "br" or "wbr";

            if (name is "br" or "wbr")
            {
                EnsureParagraph(state, BlockKind.Paragraph);
                state.Current!.Inlines.Add(new LineBreak());
                return;
            }

            if (selfClosing)
                return;

            if (!isClose)
            {
                switch (name)
                {
                    case "p":
                    case "div":
                        FlushParagraph(state, blocks);
                        EnsureParagraph(state, BlockKind.Paragraph);
                        break;
                    case "h1":
                    case "h2":
                    case "h3":
                    case "h4":
                    case "h5":
                    case "h6":
                        FlushParagraph(state, blocks);
                        EnsureParagraph(state, HeadingKind(name));
                        break;
                    case "blockquote":
                        FlushParagraph(state, blocks);
                        EnsureParagraph(state, BlockKind.Quote);
                        break;
                    case "li":
                        FlushParagraph(state, blocks);
                        EnsureParagraph(state, BlockKind.ListItem);
                        break;
                    case "ul":
                    case "ol":
                    case "section":
                    case "article":
                    case "header":
                    case "footer":
                    case "nav":
                    case "main":
                    case "aside":
                    case "figure":
                    case "figcaption":
                        FlushParagraph(state, blocks);
                        break;
                    case "b":
                    case "strong":
                        state.BoldDepth++;
                        break;
                    case "i":
                    case "em":
                    case "cite":
                    case "dfn":
                        state.ItalicDepth++;
                        break;
                    case "u":
                        state.UnderlineDepth++;
                        break;
                }

                return;
            }

            switch (name)
            {
                case "p":
                case "div":
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                case "blockquote":
                case "li":
                case "ul":
                case "ol":
                case "section":
                case "article":
                case "header":
                case "footer":
                case "nav":
                case "main":
                case "aside":
                case "figure":
                case "figcaption":
                    FlushParagraph(state, blocks);
                    break;
                case "b":
                case "strong":
                    if (state.BoldDepth > 0)
                        state.BoldDepth--;
                    break;
                case "i":
                case "em":
                case "cite":
                case "dfn":
                    if (state.ItalicDepth > 0)
                        state.ItalicDepth--;
                    break;
                case "u":
                    if (state.UnderlineDepth > 0)
                        state.UnderlineDepth--;
                    break;
            }
        }

        private static BlockKind HeadingKind(string name) =>
            name switch
            {
                "h1" => BlockKind.Heading1,
                "h2" => BlockKind.Heading2,
                "h3" => BlockKind.Heading3,
                "h4" => BlockKind.Heading4,
                "h5" => BlockKind.Heading5,
                _ => BlockKind.Heading6
            };

        private static void EnsureParagraph(HtmlRenderState state, BlockKind kind)
        {
            if (state.Current is not null && state.Kind != kind && state.Current.Inlines.Count > 0)
            {
                // Different block type while content pending — caller should have flushed.
            }

            if (state.Current is null)
            {
                state.Current = new Paragraph();
                state.Kind = kind;
                state.HasVisibleText = false;
                ApplyBlockStyle(state.Current, kind, state.BaseFontSize);

                if (kind == BlockKind.ListItem)
                {
                    state.Current.Inlines.Add(new Run
                    {
                        Text = "• ",
                        FontSize = state.BaseFontSize
                    });
                }
            }
        }

        private static void AppendDecodedText(string text, HtmlRenderState state)
        {
            if (string.IsNullOrEmpty(text))
                return;

            // Collapse excessive whitespace but keep single spaces / newlines as breaks.
            text = Regex.Replace(text, @"[ \t\f\v]+", " ");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");

            if (string.IsNullOrWhiteSpace(text))
            {
                if (state.Current is not null && state.HasVisibleText && text.Contains('\n'))
                    state.Current.Inlines.Add(new LineBreak());
                return;
            }

            EnsureParagraph(state, state.Current is null ? BlockKind.Paragraph : state.Kind);

            var parts = text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Length > 0)
                {
                    state.Current!.Inlines.Add(CreateStyledRun(part, state));
                    if (!string.IsNullOrWhiteSpace(part))
                        state.HasVisibleText = true;
                }

                if (i < parts.Length - 1 && state.HasVisibleText)
                    state.Current!.Inlines.Add(new LineBreak());
            }
        }

        private static Run CreateStyledRun(string text, HtmlRenderState state)
        {
            var run = new Run
            {
                Text = text,
                FontSize = GetFontSizeForKind(state.Kind, state.BaseFontSize)
            };

            if (state.Kind is BlockKind.Heading1 or BlockKind.Heading2 or BlockKind.Heading3
                or BlockKind.Heading4 or BlockKind.Heading5 or BlockKind.Heading6)
            {
                run.FontWeight = FontWeights.SemiBold;
            }
            else if (state.BoldDepth > 0)
            {
                run.FontWeight = FontWeights.Bold;
            }

            if (state.ItalicDepth > 0 || state.Kind == BlockKind.Quote)
                run.FontStyle = FontStyle.Italic;

            if (state.UnderlineDepth > 0)
                run.TextDecorations = TextDecorations.Underline;

            return run;
        }

        private static void FlushParagraph(HtmlRenderState state, List<Block> blocks)
        {
            if (state.Current is null)
                return;

            if (state.HasVisibleText)
                blocks.Add(state.Current);

            state.Current = null;
            state.Kind = BlockKind.Paragraph;
            state.HasVisibleText = false;
        }

        private static void ApplyBlockStyle(Paragraph paragraph, BlockKind kind, double baseFontSize)
        {
            paragraph.Margin = kind switch
            {
                BlockKind.Heading1 => new Thickness(0, 8, 0, 14),
                BlockKind.Heading2 => new Thickness(0, 6, 0, 12),
                BlockKind.Heading3 => new Thickness(0, 4, 0, 10),
                BlockKind.Heading4 or BlockKind.Heading5 or BlockKind.Heading6 => new Thickness(0, 4, 0, 8),
                BlockKind.Quote => new Thickness(24, 4, 8, 16),
                BlockKind.ListItem => new Thickness(12, 0, 0, 8),
                _ => new Thickness(0, 0, 0, 16)
            };
        }

        private static double GetFontSizeForKind(BlockKind kind, double baseFontSize) =>
            kind switch
            {
                BlockKind.Heading1 => baseFontSize * 1.55,
                BlockKind.Heading2 => baseFontSize * 1.35,
                BlockKind.Heading3 => baseFontSize * 1.2,
                BlockKind.Heading4 => baseFontSize * 1.1,
                BlockKind.Heading5 => baseFontSize * 1.05,
                BlockKind.Heading6 => baseFontSize,
                _ => baseFontSize
            };

        private enum BlockKind
        {
            Paragraph,
            Heading1,
            Heading2,
            Heading3,
            Heading4,
            Heading5,
            Heading6,
            Quote,
            ListItem
        }

        private sealed class HtmlRenderState
        {
            public HtmlRenderState(double baseFontSize)
            {
                BaseFontSize = baseFontSize;
            }

            public double BaseFontSize { get; }
            public Paragraph? Current { get; set; }
            public BlockKind Kind { get; set; } = BlockKind.Paragraph;
            public bool HasVisibleText { get; set; }
            public int BoldDepth { get; set; }
            public int ItalicDepth { get; set; }
            public int UnderlineDepth { get; set; }
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

        private void ChapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressChapterCombo || ChapterCombo.SelectedIndex < 0)
                return;

            if (ChapterCombo.SelectedIndex == _chapterIndex)
                return;

            ShowChapter(ChapterCombo.SelectedIndex, restoreScroll: false);
        }

        private void TocButton_Click(object sender, RoutedEventArgs e)
        {
            HighlightTocSelection(_chapterIndex);
        }

        private void TocList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not TocEntry entry || entry.ChapterIndex < 0)
                return;

            TocFlyout.Hide();

            if (entry.ChapterIndex == _chapterIndex)
                return;

            ShowChapter(entry.ChapterIndex, restoreScroll: false);
        }

        private void BookmarkButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshBookmarkList();
        }

        private void AddBookmarkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chapters.Count == 0)
                return;

            CaptureScrollRatioQuiet();
            var ratio = Math.Clamp(_book.LastEpubScrollRatio, 0, 1);
            var chapter = _chapters[_chapterIndex];
            var label = $"{chapter.Title} · {(int)Math.Round(ratio * 100)}%";

            _book.EpubBookmarks ??= new List<EpubBookmark>();
            _book.EpubBookmarks.Add(new EpubBookmark
            {
                ChapterIndex = _chapterIndex,
                ScrollRatio = ratio,
                Label = label,
                CreatedUtc = DateTime.UtcNow
            });

            RefreshBookmarkList();
            ProgressChanged?.Invoke(_book);
        }

        private void BookmarkList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_ignoreNextBookmarkItemClick)
            {
                _ignoreNextBookmarkItemClick = false;
                return;
            }

            if (e.ClickedItem is not BookmarkListItem item)
                return;

            BookmarkFlyout.Hide();
            JumpToBookmark(item.Bookmark);
        }

        private void DeleteBookmarkButton_Click(object sender, RoutedEventArgs e)
        {
            _ignoreNextBookmarkItemClick = true;

            if (sender is not Button { Tag: Guid id })
                return;

            _book.EpubBookmarks ??= new List<EpubBookmark>();
            var removed = _book.EpubBookmarks.RemoveAll(b => b.Id == id);
            if (removed <= 0)
                return;

            RefreshBookmarkList();
            ProgressChanged?.Invoke(_book);
        }

        private void JumpToBookmark(EpubBookmark bookmark)
        {
            if (_chapters.Count == 0)
                return;

            var index = Math.Clamp(bookmark.ChapterIndex, 0, _chapters.Count - 1);
            ShowChapter(index, restoreScroll: true, scrollRatioOverride: Math.Clamp(bookmark.ScrollRatio, 0, 1));
        }

        private void RefreshBookmarkList()
        {
            _book.EpubBookmarks ??= new List<EpubBookmark>();
            _bookmarkItems.Clear();

            foreach (var bookmark in _book.EpubBookmarks
                         .OrderBy(b => b.ChapterIndex)
                         .ThenBy(b => b.ScrollRatio)
                         .ThenBy(b => b.CreatedUtc))
            {
                var chapterTitle = bookmark.ChapterIndex >= 0 && bookmark.ChapterIndex < _chapters.Count
                    ? _chapters[bookmark.ChapterIndex].Title
                    : $"Section {bookmark.ChapterIndex + 1}";

                var display = string.IsNullOrWhiteSpace(bookmark.Label)
                    ? chapterTitle
                    : bookmark.Label;

                _bookmarkItems.Add(new BookmarkListItem
                {
                    Bookmark = bookmark,
                    DisplayLabel = display,
                    DetailText = $"Section {bookmark.ChapterIndex + 1} · {(int)Math.Round(bookmark.ScrollRatio * 100)}%"
                });
            }

            BookmarkEmptyText.Visibility = _bookmarkItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Focus(FocusState.Programmatic);
        }

        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                RunSearch();
            }
        }

        private void SearchGoButton_Click(object sender, RoutedEventArgs e) => RunSearch();

        private void SearchPrevButton_Click(object sender, RoutedEventArgs e) => MoveSearchHit(-1);

        private void SearchNextButton_Click(object sender, RoutedEventArgs e) => MoveSearchHit(1);

        private void SearchResultsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not SearchHit hit)
                return;

            var index = _searchHits.IndexOf(hit);
            if (index < 0)
                return;

            _searchHitIndex = index;
            UpdateSearchNavState();
            JumpToSearchHit(hit, hideFlyout: true);
        }

        private void RunSearch()
        {
            var query = SearchBox.Text?.Trim() ?? string.Empty;
            _searchHits.Clear();
            _searchHitIndex = -1;

            if (query.Length == 0 || _chapters.Count == 0)
            {
                SearchResultsList.ItemsSource = null;
                SearchStatusText.Text = query.Length == 0 ? "Enter text to search." : "No chapters loaded.";
                UpdateSearchNavState();
                return;
            }

            const int maxHits = 200;
            const int maxPerChapter = 25;
            var comparison = StringComparison.OrdinalIgnoreCase;

            for (var chapterIndex = 0; chapterIndex < _chapters.Count && _searchHits.Count < maxHits; chapterIndex++)
            {
                var plain = _chapters[chapterIndex].PlainText;
                if (string.IsNullOrEmpty(plain))
                    continue;

                var perChapter = 0;
                var start = 0;
                while (perChapter < maxPerChapter && _searchHits.Count < maxHits)
                {
                    var found = plain.IndexOf(query, start, comparison);
                    if (found < 0)
                        break;

                    var ratio = plain.Length <= 1
                        ? 0
                        : Math.Clamp((double)found / plain.Length, 0, 1);

                    _searchHits.Add(new SearchHit
                    {
                        ChapterIndex = chapterIndex,
                        CharOffset = found,
                        ScrollRatio = ratio,
                        ChapterLabel = $"Section {chapterIndex + 1}: {_chapters[chapterIndex].Title}",
                        Snippet = BuildSnippet(plain, found, query.Length)
                    });

                    perChapter++;
                    start = found + Math.Max(query.Length, 1);
                }
            }

            SearchResultsList.ItemsSource = _searchHits.ToList();

            if (_searchHits.Count == 0)
            {
                SearchStatusText.Text = "No matches.";
                UpdateSearchNavState();
                return;
            }

            _searchHitIndex = 0;
            SearchStatusText.Text = $"1 of {_searchHits.Count}";
            UpdateSearchNavState();
            SearchResultsList.SelectedIndex = 0;
            JumpToSearchHit(_searchHits[0], hideFlyout: false);
        }

        private void MoveSearchHit(int delta)
        {
            if (_searchHits.Count == 0)
                return;

            _searchHitIndex = (_searchHitIndex + delta + _searchHits.Count) % _searchHits.Count;
            UpdateSearchNavState();
            SearchResultsList.SelectedIndex = _searchHitIndex;
            SearchResultsList.ScrollIntoView(_searchHits[_searchHitIndex]);
            JumpToSearchHit(_searchHits[_searchHitIndex], hideFlyout: false);
        }

        private void UpdateSearchNavState()
        {
            var hasHits = _searchHits.Count > 0;
            SearchPrevButton.IsEnabled = hasHits;
            SearchNextButton.IsEnabled = hasHits;

            if (hasHits && _searchHitIndex >= 0)
                SearchStatusText.Text = $"{_searchHitIndex + 1} of {_searchHits.Count}";
        }

        private void JumpToSearchHit(SearchHit hit, bool hideFlyout)
        {
            if (hideFlyout)
                SearchFlyout.Hide();

            ShowChapter(hit.ChapterIndex, restoreScroll: true, scrollRatioOverride: hit.ScrollRatio);
        }

        private static string BuildSnippet(string text, int matchIndex, int matchLength)
        {
            const int radius = 42;
            var start = Math.Max(0, matchIndex - radius);
            var end = Math.Min(text.Length, matchIndex + Math.Max(matchLength, 1) + radius);
            var snippet = text[start..end].Replace('\n', ' ').Replace('\r', ' ');
            snippet = Regex.Replace(snippet, @"\s+", " ").Trim();

            if (start > 0)
                snippet = "…" + snippet;
            if (end < text.Length)
                snippet += "…";

            return snippet;
        }

        private void PersistProgress()
        {
            CaptureScrollRatio();
        }

        private static void ApplyNavigationTitles(EpubBook epub, List<EpubChapterContent> chapters)
        {
            if (epub.Navigation is null || epub.Navigation.Count == 0 || chapters.Count == 0)
                return;

            var byPath = BuildPathIndex(chapters);
            var claimed = new HashSet<int>();

            foreach (var item in FlattenNavigation(epub.Navigation))
            {
                if (string.IsNullOrWhiteSpace(item.Title) || item.Link is null)
                    continue;

                var navTitle = item.Title.Trim();
                if (IsWeakTitle(navTitle, -1))
                    continue;

                var path = item.Link.ContentFilePath;
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (!TryResolveChapterIndex(path, byPath, chapters, out var index)
                    || index < 0
                    || index >= chapters.Count
                    || claimed.Contains(index))
                {
                    continue;
                }

                // Prefer nav labels when the reading-order title is weak/generic.
                if (IsWeakTitle(chapters[index].Title, index + 1))
                {
                    chapters[index].Title = navTitle;
                    claimed.Add(index);
                }
            }
        }

        private static bool IsWeakTitle(string title, int chapterNumber)
        {
            if (string.IsNullOrWhiteSpace(title))
                return true;

            var t = title.Trim();
            if (t.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || t.Equals("Untitled", StringComparison.OrdinalIgnoreCase)
                || t.Equals("Contents", StringComparison.OrdinalIgnoreCase)
                || t.Equals("Table of Contents", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (Regex.IsMatch(t, @"^Section\s+\d+$", RegexOptions.IgnoreCase))
                return true;

            if (chapterNumber > 0
                && Regex.IsMatch(t, $@"^Section\s+{chapterNumber}$", RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (Regex.IsMatch(t, @"\.(x?html?|xml)$", RegexOptions.IgnoreCase))
                return true;

            // Very long first-line dumps make poor chapter titles.
            if (t.Length > 120)
                return true;

            return false;
        }

        private static List<TocEntry> BuildTocEntries(EpubBook epub, List<EpubChapterContent> chapters)
        {
            if (chapters.Count == 0)
                return new List<TocEntry>();

            var byPath = BuildPathIndex(chapters);
            var entries = new List<TocEntry>();

            if (epub.Navigation is { Count: > 0 })
            {
                void Walk(IEnumerable<EpubNavigationItem> items, int depth)
                {
                    foreach (var item in items)
                    {
                        var title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled" : item.Title.Trim();
                        var chapterIndex = -1;
                        if (item.Link?.ContentFilePath is { Length: > 0 } path)
                            TryResolveChapterIndex(path, byPath, chapters, out chapterIndex);

                        entries.Add(new TocEntry
                        {
                            Title = title,
                            ChapterIndex = chapterIndex,
                            Depth = depth
                        });

                        if (item.NestedItems is { Count: > 0 })
                            Walk(item.NestedItems, depth + 1);
                    }
                }

                Walk(epub.Navigation, 0);

                if (entries.Any(e => e.ChapterIndex >= 0))
                    return entries;
            }

            return chapters.Select((c, i) => new TocEntry
            {
                Title = c.Title,
                ChapterIndex = i,
                Depth = 0
            }).ToList();
        }

        private static IEnumerable<EpubNavigationItem> FlattenNavigation(IEnumerable<EpubNavigationItem> items)
        {
            foreach (var item in items)
            {
                yield return item;
                if (item.NestedItems is { Count: > 0 })
                {
                    foreach (var nested in FlattenNavigation(item.NestedItems))
                        yield return nested;
                }
            }
        }

        private static Dictionary<string, int> BuildPathIndex(List<EpubChapterContent> chapters)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < chapters.Count; i++)
            {
                AddPathKey(map, chapters[i].FileKey, i);
                AddPathKey(map, chapters[i].FilePath, i);
            }

            return map;
        }

        private static void AddPathKey(Dictionary<string, int> map, string? key, int index)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            map.TryAdd(key, index);
            map.TryAdd(NormalizePath(key), index);

            var fileName = System.IO.Path.GetFileName(key.Replace('\\', '/'));
            if (!string.IsNullOrWhiteSpace(fileName))
                map.TryAdd(fileName, index);
        }

        private static bool TryResolveChapterIndex(
            string contentFilePath,
            Dictionary<string, int> byPath,
            List<EpubChapterContent> chapters,
            out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(contentFilePath))
                return false;

            var candidates = new[]
            {
                contentFilePath,
                NormalizePath(contentFilePath),
                System.IO.Path.GetFileName(contentFilePath.Replace('\\', '/'))
            };

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && byPath.TryGetValue(candidate, out index))
                    return true;
            }

            var needle = NormalizePath(contentFilePath);
            for (var i = 0; i < chapters.Count; i++)
            {
                var hay = NormalizePath(chapters[i].FilePath);
                if (hay.EndsWith(needle, StringComparison.OrdinalIgnoreCase)
                    || needle.EndsWith(hay, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
            }

            return false;
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            return Regex.Matches(text, @"\S+").Count;
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
            foreach (var tag in new[] { "h1", "h2", "h3" })
            {
                var heading = Regex.Match(
                    html,
                    $@"<{tag}[^>]*>(.*?)</{tag}>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!heading.Success)
                    continue;

                var value = CleanInline(heading.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(value) && !IsWeakTitle(value, -1))
                    return value;
            }

            var title = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (title.Success)
            {
                var value = CleanInline(title.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(value) && !IsWeakTitle(value, -1))
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
            public EpubChapterContent(string title, string html, string plainText, string fileKey, string filePath, int wordCount)
            {
                Title = title;
                Html = html;
                PlainText = plainText;
                FileKey = fileKey;
                FilePath = filePath;
                WordCount = wordCount;
            }

            public string Title { get; set; }
            public string Html { get; }
            public string PlainText { get; }
            public string FileKey { get; }
            public string FilePath { get; }
            public int WordCount { get; }
        }

        private sealed class TocEntry
        {
            public string Title { get; set; } = string.Empty;
            public int ChapterIndex { get; set; } = -1;
            public int Depth { get; set; }

            public string DisplayTitle => Title;

            public Thickness IndentMargin => new Thickness(Depth * 16, 0, 0, 0);
        }

        private sealed class BookmarkListItem
        {
            public EpubBookmark Bookmark { get; set; } = new();
            public Guid Id => Bookmark.Id;
            public string DisplayLabel { get; set; } = string.Empty;
            public string DetailText { get; set; } = string.Empty;
        }

        private sealed class SearchHit
        {
            public int ChapterIndex { get; set; }
            public int CharOffset { get; set; }
            public double ScrollRatio { get; set; }
            public string ChapterLabel { get; set; } = string.Empty;
            public string Snippet { get; set; } = string.Empty;
        }
    }
}
