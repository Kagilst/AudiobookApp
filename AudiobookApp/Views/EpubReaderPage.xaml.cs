using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AudiobookApp.Models;
using AudiobookApp.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VersOne.Epub;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
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
        private const double CommentGutterWidth = 260;
        private const double CommentGutterToggleWidth = 28;
        private const double CommentCardMinGap = 10;
        private const double NoteClusterProximityPx = 72;
        private const int NoteCollapsedMaxLines = 4;
        private const string CommentGutterExpandedSettingKey = "EpubCommentGutterExpanded";

        private readonly Book _book;
        private readonly List<EpubChapterContent> _chapters = new();
        private readonly List<TocEntry> _tocEntries = new();
        private readonly ObservableCollection<BookmarkListItem> _bookmarkItems = new();
        private readonly ObservableCollection<HighlightListItem> _highlightItems = new();
        private readonly List<SearchHit> _searchHits = new();
        private readonly HashSet<Guid> _expandedNoteIds = new();
        private readonly Dictionary<string, Guid> _noteClusterSelectedIds = new();
        private readonly EpubTtsService _tts = new();
        private readonly EpubTtsSleepTimer _ttsSleep = new();
        private readonly WordNetDictionaryService _dictionary = new();
        private readonly Dictionary<string, byte[]> _epubImages = new(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _ttsSleepUiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private int _chapterIndex;
        private int _searchHitIndex = -1;
        /// <summary>Null means follow Windows light/dark setting.</summary>
        private bool? _darkModeOverride;
        private bool _suppressScrollPersist;
        private bool _suppressChapterCombo;
        private bool _suppressDisplayControls;
        private bool _ignoreNextBookmarkItemClick;
        private int? _capturedSelectionStart;
        private int? _capturedSelectionEnd;
        private string _capturedSelectedText = string.Empty;
        private double _pendingScrollRatio = -1;
        private int? _ttsActiveStart;
        private int? _ttsActiveEnd;
        private bool _commentGutterExpanded = true;
        private bool _suppressTtsVolumeSlider;
        private bool _suppressTtsSpeedSlider;
        private bool _suppressTtsSleepCombo;

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
            _book.EpubHighlights ??= new List<EpubHighlight>();
            _darkModeOverride = ReadDarkModePreference();
            LoadReadingPreferences();
            ApplyTheme();
            ApplyReadingPreferences(restoreScroll: false);
            SyncDisplayControls();
            BookmarkList.ItemsSource = _bookmarkItems;
            HighlightList.ItemsSource = _highlightItems;
            _commentGutterExpanded = ReadCommentGutterExpanded();
            ApplyCommentGutterLayout();
            RefreshBookmarkList();
            RefreshHighlightList();
            InitTtsVoices();
            _ttsSleepUiTimer.Tick += (_, _) => OnTtsSleepTick();
            _tts.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateTtsUi);
            _tts.PositionChanged += OnTtsPositionChanged;
            _tts.Completed += () => DispatcherQueue.TryEnqueue(() =>
            {
                _ttsActiveStart = null;
                _ttsActiveEnd = null;
                CancelTtsSleepTimer();
                ApplyHighlightsToPage();
                UpdateTtsUi();
            });

            Loaded += async (_, _) => await LoadBookAsync();
            Unloaded += (_, _) =>
            {
                PersistTtsPosition();
                CancelTtsSleepTimer();
                _tts.Stop();
                _tts.Dispose();
                _dictionary.Dispose();
                _epubImages.Clear();
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

            ApplyReaderMaxWidths();

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

        private void ApplyReaderMaxWidths()
        {
            var textWidth = GetContentMaxWidth(_contentWidthKey);
            var side = CommentGutterToggleWidth + (_commentGutterExpanded ? CommentGutterWidth : 0);
            BookPageBorder.MaxWidth = textWidth + side;
            FooterNavGrid.MaxWidth = textWidth + side;
        }

        private void CommentGutterToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _commentGutterExpanded = !_commentGutterExpanded;
            SaveCommentGutterExpanded(_commentGutterExpanded);
            ApplyCommentGutterLayout();
            ApplyReaderMaxWidths();
            if (_commentGutterExpanded)
                DispatcherQueue.TryEnqueue(RefreshNoteGutter);
        }

        private void ApplyCommentGutterLayout()
        {
            if (CommentGutterHost == null || CommentGutterToggleButton == null)
                return;

            CommentGutterHost.Visibility = _commentGutterExpanded ? Visibility.Visible : Visibility.Collapsed;
            CommentGutterToggleButton.Content = _commentGutterExpanded ? "‹" : "›";
            ToolTipService.SetToolTip(
                CommentGutterToggleButton,
                _commentGutterExpanded ? "Hide notes" : "Show notes");
        }

        private static bool ReadCommentGutterExpanded()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings.Values;
                if (settings.TryGetValue(CommentGutterExpandedSettingKey, out var value) && value is bool expanded)
                    return expanded;
            }
            catch
            {
            }

            return true;
        }

        private static void SaveCommentGutterExpanded(bool expanded)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[CommentGutterExpandedSettingKey] = expanded;
            }
            catch
            {
            }
        }

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
                LoadEpubImageMap(epub);

                if (!string.IsNullOrWhiteSpace(epub.Title))
                    BookTitleText.Text = epub.Title;

                foreach (var file in epub.ReadingOrder)
                {
                    var html = file.Content ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(html))
                        continue;

                    var plain = HtmlToPlainText(html);
                    var hasImages = Regex.IsMatch(html, @"<img\b", RegexOptions.IgnoreCase);
                    if (string.IsNullOrWhiteSpace(plain) && !hasImages)
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
                    HighlightButton.IsEnabled = false;
                    TtsButton.IsEnabled = false;
                    return;
                }

                RefreshBookmarkList();
                RefreshHighlightList();
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
                HighlightButton.IsEnabled = false;
                TtsButton.IsEnabled = false;
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

            var blocks = BuildRichBlocks(chapter, _fontSize);
            if (blocks.Count == 0)
            {
                foreach (var chunk in SplitPlainParagraphs(chapter.PlainText))
                    PageText.Blocks.Add(CreatePlainParagraph(chunk, _fontSize));
            }
            else
            {
                foreach (var block in blocks)
                    PageText.Blocks.Add(block);
            }

            ApplyHighlightsToPage();
            DispatcherQueue.TryEnqueue(RefreshNoteGutter);
        }

        private void LoadEpubImageMap(EpubBook epub)
        {
            _epubImages.Clear();
            try
            {
                foreach (var imageFile in epub.Content.Images.Local)
                    IndexEpubImage(imageFile.FilePath, imageFile.Key, imageFile.Content);
            }
            catch
            {
                // Older/odd packages — ignore image map failures.
            }
        }

        private void IndexEpubImage(string? filePath, string? key, byte[]? content)
        {
            if (content is not { Length: > 0 })
                return;

            void Add(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return;
                var normalized = NormalizePath(raw);
                _epubImages[normalized] = content;
                var fileName = Path.GetFileName(normalized);
                if (!string.IsNullOrWhiteSpace(fileName))
                    _epubImages.TryAdd(fileName, content);
            }

            Add(filePath);
            Add(key);
        }

        private void PageText_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Math.Abs(e.PreviousSize.Height - e.NewSize.Height) > 0.5)
                RefreshNoteGutter();
        }

        private void ApplyHighlightsToPage()
        {
            PageText.TextHighlighters.Clear();

            _book.EpubHighlights ??= new List<EpubHighlight>();
            var plain = GetRenderedPlainText(PageText);
            var plainLen = plain.Length;

            foreach (var group in _book.EpubHighlights
                         .Where(h => h.ChapterIndex == _chapterIndex && h.EndOffset > h.StartOffset)
                         .GroupBy(h => h.Color))
            {
                var highlighter = new TextHighlighter
                {
                    Background = new SolidColorBrush(GetHighlightColor(group.Key, isTts: false))
                };

                foreach (var h in group)
                {
                    var start = Math.Clamp(h.StartOffset, 0, plainLen);
                    var end = Math.Clamp(h.EndOffset, start, plainLen);
                    if (end <= start)
                        continue;
                    highlighter.Ranges.Add(new TextRange { StartIndex = start, Length = end - start });
                }

                if (highlighter.Ranges.Count > 0)
                    PageText.TextHighlighters.Add(highlighter);
            }

            if (_ttsActiveStart is int ttsStart && _ttsActiveEnd is int ttsEnd && ttsEnd > ttsStart)
            {
                var start = Math.Clamp(ttsStart, 0, plainLen);
                var end = Math.Clamp(ttsEnd, start, plainLen);
                if (end > start)
                {
                    var ttsHighlighter = new TextHighlighter
                    {
                        Background = new SolidColorBrush(GetHighlightColor(EpubHighlightColor.Blue, isTts: true))
                    };
                    ttsHighlighter.Ranges.Add(new TextRange { StartIndex = start, Length = end - start });
                    PageText.TextHighlighters.Add(ttsHighlighter);
                }
            }
        }

        private static Color GetHighlightColor(EpubHighlightColor color, bool isTts)
        {
            if (isTts)
                return Color.FromArgb(90, 100, 180, 255);

            return color switch
            {
                EpubHighlightColor.Green => Color.FromArgb(120, 140, 220, 120),
                EpubHighlightColor.Blue => Color.FromArgb(120, 120, 180, 240),
                EpubHighlightColor.Pink => Color.FromArgb(120, 240, 140, 180),
                _ => Color.FromArgb(120, 255, 230, 100)
            };
        }

        /// <summary>
        /// Run-character stream used by TextHighlighter (no synthetic line-break characters).
        /// </summary>
        private static string GetRenderedPlainText(RichTextBlock rtb)
        {
            var sb = new StringBuilder();
            foreach (var paragraph in rtb.Blocks.OfType<Paragraph>())
                AppendRunTextOnly(paragraph.Inlines, sb);
            return sb.ToString();
        }

        private static void AppendRunTextOnly(InlineCollection inlines, StringBuilder sb)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        sb.Append(run.Text);
                        break;
                    case Span span:
                        AppendRunTextOnly(span.Inlines, sb);
                        break;
                }
            }
        }

        private static IEnumerable<Run> EnumerateRuns(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        yield return run;
                        break;
                    case Span span:
                        foreach (var nested in EnumerateRuns(span.Inlines))
                            yield return nested;
                        break;
                }
            }
        }

        /// <summary>Strip line breaks from SelectedText so it matches the run-only highlighter stream.</summary>
        private static string ToHighlighterText(string? selected) =>
            (selected ?? string.Empty)
                .Replace("\r\n", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty);

        private bool TryGetSelectionOffsets(out int start, out int end, out string selected)
        {
            start = 0;
            end = 0;
            selected = PageText.SelectedText ?? string.Empty;

            if (TryGetLiveSelectionOffsets(out start, out end, out var liveSelected))
            {
                selected = liveSelected;
                return true;
            }

            if (_capturedSelectionStart is int capturedStart &&
                _capturedSelectionEnd is int capturedEnd &&
                capturedEnd > capturedStart)
            {
                start = capturedStart;
                end = capturedEnd;
                selected = _capturedSelectedText;
                return true;
            }

            return false;
        }

        private bool TryGetLiveSelectionOffsets(out int start, out int end, out string selected)
        {
            start = 0;
            end = 0;
            selected = PageText.SelectedText ?? string.Empty;
            if (string.IsNullOrEmpty(selected))
                return false;

            var needle = ToHighlighterText(selected);
            if (needle.Length == 0)
                return false;

            var plain = GetRenderedPlainText(PageText);
            if (plain.Length == 0)
                return false;

            // Hint from pointer position mapped into run-only space.
            var hint = GetRunOnlyCharIndex(PageText, PageText.SelectionStart);

            var best = -1;
            var bestDistance = int.MaxValue;
            for (var i = plain.IndexOf(needle, StringComparison.Ordinal);
                 i >= 0;
                 i = plain.IndexOf(needle, i + 1, StringComparison.Ordinal))
            {
                var distance = Math.Abs(i - hint);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            if (best < 0)
                return false;

            // Exact text guarantee: highlighter range == selected text without break chars.
            start = best;
            end = best + needle.Length;
            return plain[start..end] == needle;
        }

        private static int GetRunOnlyCharIndex(RichTextBlock rtb, TextPointer? pointer)
        {
            if (pointer == null)
                return 0;

            var index = 0;
            foreach (var paragraph in rtb.Blocks.OfType<Paragraph>())
            {
                foreach (var run in EnumerateRuns(paragraph.Inlines))
                {
                    var text = run.Text ?? string.Empty;
                    if (text.Length == 0)
                        continue;

                    TextPointer? runStart = null;
                    TextPointer? runEnd = null;
                    try
                    {
                        runStart = run.ContentStart;
                        runEnd = run.ContentEnd;
                    }
                    catch
                    {
                        index += text.Length;
                        continue;
                    }

                    if (runStart == null || runEnd == null)
                    {
                        index += text.Length;
                        continue;
                    }

                    if (pointer.Offset <= runStart.Offset)
                        return index;

                    if (pointer.Offset >= runEnd.Offset)
                    {
                        index += text.Length;
                        continue;
                    }

                    var pointerSpan = Math.Max(runEnd.Offset - runStart.Offset, 1);
                    var local = pointer.Offset - runStart.Offset;
                    var localChar = pointerSpan == text.Length
                        ? local
                        : (int)Math.Round(local * (double)text.Length / pointerSpan);
                    return index + Math.Clamp(localChar, 0, text.Length);
                }
            }

            return index;
        }

        private void CaptureSelectionForContextMenu()
        {
            _capturedSelectionStart = null;
            _capturedSelectionEnd = null;
            _capturedSelectedText = string.Empty;

            if (!TryGetLiveSelectionOffsets(out var start, out var end, out var selected))
                return;

            _capturedSelectionStart = start;
            _capturedSelectionEnd = end;
            _capturedSelectedText = selected;
        }

        private void ClearCapturedSelection()
        {
            _capturedSelectionStart = null;
            _capturedSelectionEnd = null;
            _capturedSelectedText = string.Empty;
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

        private List<Block> BuildRichBlocks(EpubChapterContent chapter, double baseFontSize)
        {
            var blocks = new List<Block>();
            var html = chapter.Html;
            if (string.IsNullOrWhiteSpace(html))
                return blocks;

            var body = SanitizeHtml(ExtractBody(html));
            if (string.IsNullOrWhiteSpace(body))
                return blocks;

            var state = new HtmlRenderState(baseFontSize, chapter.FilePath);
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

            // Drop unsafe / non-text containers entirely. Keep <img> for rendering.
            html = Regex.Replace(
                html,
                @"<(script|style|svg|iframe|object|embed|form|noscript)(\s[^>]*)?>[\s\S]*?</\1\s*>",
                " ",
                RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<!--[\s\S]*?-->", " ");
            html = Regex.Replace(
                html,
                @"<(script|style|svg|iframe|object|embed|form|noscript|hr|meta|link)(\s[^>]*)?/?>",
                " ",
                RegexOptions.IgnoreCase);
            return html;
        }

        private void HandleHtmlTag(string rawTag, HtmlRenderState state, List<Block> blocks)
        {
            var match = Regex.Match(rawTag, @"^</?\s*([a-zA-Z0-9]+)");
            if (!match.Success)
                return;

            var name = match.Groups[1].Value.ToLowerInvariant();
            var isClose = rawTag.StartsWith("</", StringComparison.Ordinal);
            var selfClosing = rawTag.EndsWith("/>", StringComparison.Ordinal)
                              || name is "br" or "wbr" or "img";

            if (name is "br" or "wbr")
            {
                EnsureParagraph(state, BlockKind.Paragraph);
                state.Current!.Inlines.Add(new LineBreak());
                return;
            }

            if (name == "img")
            {
                TryAppendImage(rawTag, state);
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

        private void TryAppendImage(string rawTag, HtmlRenderState state)
        {
            var srcMatch = Regex.Match(rawTag, @"\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!srcMatch.Success)
                return;

            var src = WebUtility.HtmlDecode(srcMatch.Groups[1].Value.Trim());
            if (string.IsNullOrWhiteSpace(src)
                || src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return;

            if (!TryResolveEpubImageBytes(state.ChapterFilePath, src, out var bytes) || bytes.Length == 0)
                return;

            var bitmap = TryCreateBitmap(bytes);
            if (bitmap == null)
                return;

            EnsureParagraph(state, state.Current is null ? BlockKind.Paragraph : state.Kind);
            var maxWidth = Math.Max(GetContentMaxWidth(_contentWidthKey) - 16, 200);
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = maxWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8)
            };
            state.Current!.Inlines.Add(new InlineUIContainer { Child = image });
            state.HasVisibleText = true;
        }

        private bool TryResolveEpubImageBytes(string chapterFilePath, string src, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            var resolved = ResolveEpubHref(chapterFilePath, src);
            if (_epubImages.TryGetValue(resolved, out bytes!))
                return true;

            var fileName = Path.GetFileName(resolved);
            if (!string.IsNullOrWhiteSpace(fileName) && _epubImages.TryGetValue(fileName, out bytes!))
                return true;

            // Try without leading folders (some packages use absolute-looking paths).
            foreach (var key in _epubImages.Keys)
            {
                if (key.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    bytes = _epubImages[key];
                    return true;
                }
            }

            bytes = Array.Empty<byte>();
            return false;
        }

        private static string ResolveEpubHref(string baseFilePath, string href)
        {
            var cut = href.IndexOfAny(new[] { '#', '?' });
            if (cut >= 0)
                href = href[..cut];
            href = href.Replace('\\', '/').Trim();
            if (href.StartsWith('/'))
                return NormalizePath(href);

            var baseDir = NormalizePath(Path.GetDirectoryName(baseFilePath.Replace('\\', '/')) ?? "");
            var combined = string.IsNullOrEmpty(baseDir) ? href : $"{baseDir}/{href}";
            var parts = new List<string>();
            foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".")
                    continue;
                if (segment == "..")
                {
                    if (parts.Count > 0)
                        parts.RemoveAt(parts.Count - 1);
                    continue;
                }

                parts.Add(segment);
            }

            return string.Join('/', parts);
        }

        private static BitmapImage? TryCreateBitmap(byte[] bytes)
        {
            try
            {
                var bitmap = new BitmapImage();
                using var stream = new InMemoryRandomAccessStream();
                stream.WriteAsync(bytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
                stream.Seek(0);
                bitmap.SetSource(stream);
                return bitmap;
            }
            catch
            {
                return null;
            }
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
            public HtmlRenderState(double baseFontSize, string chapterFilePath)
            {
                BaseFontSize = baseFontSize;
                ChapterFilePath = chapterFilePath ?? string.Empty;
            }

            public double BaseFontSize { get; }
            public string ChapterFilePath { get; }
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

        private async void AddBookmarkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chapters.Count == 0)
                return;

            CaptureScrollRatioQuiet();
            var ratio = Math.Clamp(_book.LastEpubScrollRatio, 0, 1);
            var chapter = _chapters[_chapterIndex];
            var defaultLabel = $"{chapter.Title} · {(int)Math.Round(ratio * 100)}%";
            var name = await PromptTextAsync(
                "Bookmark",
                "Name this bookmark:",
                defaultLabel,
                multiline: false);
            if (name == null)
                return;

            var label = string.IsNullOrWhiteSpace(name) ? defaultLabel : name.Trim();

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
                var plain = GetChapterPlainText(chapterIndex);
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

        private void HighlightButton_Click(object sender, RoutedEventArgs e)
        {
            CaptureSelectionForContextMenu();
            RefreshHighlightList();
        }

        private void TtsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tts.IsPlaying && !_tts.IsPaused)
            {
                _tts.Pause();
                PersistTtsPosition();
                UpdateTtsUi();
                return;
            }

            _ = TtsStartOrResumeAsync();
        }

        private void PageTextContextFlyout_Opening(object sender, object e) =>
            CaptureSelectionForContextMenu();

        private async void ContextDefine_Click(object sender, RoutedEventArgs e)
        {
            CaptureSelectionForContextMenu();
            var selected = _capturedSelectedText;
            if (string.IsNullOrWhiteSpace(selected) &&
                !string.IsNullOrWhiteSpace(PageText.SelectedText))
                selected = PageText.SelectedText;

            var word = WordNetDictionaryService.ExtractLookupWord(selected);
            if (string.IsNullOrWhiteSpace(word))
            {
                await ShowInfoAsync("Dictionary", "Select a word to look up.");
                return;
            }

            await ShowDictionaryLookupAsync(word);
        }

        private async Task ShowDictionaryLookupAsync(string word)
        {
            if (!_dictionary.IsAvailable)
            {
                await ShowInfoAsync(
                    "Dictionary",
                    "The offline WordNet dictionary pack was not found. Reinstall the app or rebuild Assets/Dictionary/wordnet-en.db.");
                return;
            }

            var result = await _dictionary.LookupAsync(word);
            if (!result.Found)
            {
                await ShowInfoAsync("Dictionary", $"No definition found for “{word}”.");
                return;
            }

            var body = new StackPanel { Spacing = 10, MaxWidth = 440 };
            var matched = string.Equals(result.Query, result.MatchedLemma, StringComparison.OrdinalIgnoreCase)
                ? result.MatchedLemma
                : $"{result.Query} → {result.MatchedLemma}";
            body.Children.Add(new TextBlock
            {
                Text = matched,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.WrapWholeWords
            });

            string? lastPos = null;
            var senseIndex = 0;
            foreach (var sense in result.Senses.Take(12))
            {
                if (!string.Equals(lastPos, sense.PartOfSpeech, StringComparison.Ordinal))
                {
                    lastPos = sense.PartOfSpeech;
                    senseIndex = 0;
                    body.Children.Add(new TextBlock
                    {
                        Text = sense.PartOfSpeechLabel,
                        FontSize = 12,
                        Opacity = 0.7,
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                }

                senseIndex++;
                var block = new StackPanel { Spacing = 2 };
                block.Children.Add(new TextBlock
                {
                    Text = $"{senseIndex}. {sense.Definition}",
                    TextWrapping = TextWrapping.WrapWholeWords,
                    FontSize = 13
                });
                if (!string.IsNullOrWhiteSpace(sense.Synonyms))
                {
                    block.Children.Add(new TextBlock
                    {
                        Text = "Synonyms: " + sense.Synonyms,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        FontSize = 12,
                        Opacity = 0.75
                    });
                }

                body.Children.Add(block);
            }

            if (result.Senses.Count > 12)
            {
                body.Children.Add(new TextBlock
                {
                    Text = $"…and {result.Senses.Count - 12} more senses",
                    FontSize = 12,
                    Opacity = 0.65
                });
            }

            var scroller = new ScrollViewer
            {
                Content = body,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            await new ContentDialog
            {
                Title = "Dictionary",
                Content = scroller,
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            }.ShowAsync();
        }

        private void ContextHighlightColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag })
                return;

            var parts = tag.Split('|');
            if (!Enum.TryParse<EpubHighlightColor>(parts[0], out var color))
                return;

            var askNote = parts.Length > 1 &&
                          parts[1].Equals("note", StringComparison.OrdinalIgnoreCase);
            _ = CreateHighlightFromSelectionAsync(color, askNote);
        }

        private async Task CreateHighlightFromSelectionAsync(EpubHighlightColor color, bool askNote)
        {
            // Capture immediately — flyouts/dialogs clear the live selection.
            if (!TryGetSelectionOffsets(out var start, out var end, out _))
            {
                await ShowInfoAsync("Highlight", "Select some text in the chapter first.");
                return;
            }

            string? note = null;
            if (askNote)
            {
                note = await PromptNoteAsync("Highlight note", "Optional note for this highlight:", string.Empty);
                if (note == null)
                {
                    ClearCapturedSelection();
                    return;
                }

                if (string.IsNullOrWhiteSpace(note))
                    note = null;
            }

            _book.EpubHighlights ??= new List<EpubHighlight>();
            _book.EpubHighlights.Add(new EpubHighlight
            {
                ChapterIndex = _chapterIndex,
                StartOffset = start,
                EndOffset = end,
                Color = color,
                Note = note,
                CreatedUtc = DateTime.UtcNow
            });

            ClearCapturedSelection();
            HighlightFlyout.Hide();
            RenderChapter(_chapters[_chapterIndex]);
            RefreshHighlightList();
            ProgressChanged?.Invoke(_book);
        }

        private void HighlightList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not HighlightListItem item)
                return;

            HighlightFlyout.Hide();
            JumpToHighlight(item.Highlight);
        }

        private void HighlightListItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: HighlightListItem item })
                return;

            e.Handled = true;
            var highlightId = item.Highlight.Id;
            var menu = new MenuFlyout();

            var editNote = new MenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(item.Highlight.Note) ? "Add note…" : "Edit note…"
            };
            editNote.Click += async (_, _) => await EditHighlightNoteByIdAsync(highlightId);
            menu.Items.Add(editNote);

            var delete = new MenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(item.Highlight.Note) ? "Delete highlight" : "Delete highlight + note"
            };
            delete.Click += (_, _) => RemoveHighlightById(highlightId);
            menu.Items.Add(delete);

            if (sender is FrameworkElement target)
                menu.ShowAt(target, e.GetPosition(target));
        }

        private async void EditHighlightNote_Click(object sender, RoutedEventArgs e)
        {
            Guid id;
            if (sender is FrameworkElement { Tag: string tag } && Guid.TryParse(tag, out id))
            {
                await EditHighlightNoteByIdAsync(id);
                return;
            }

            if (sender is FrameworkElement { Tag: Guid guid })
                await EditHighlightNoteByIdAsync(guid);
        }

        private async Task EditHighlightNoteByIdAsync(Guid id)
        {
            var highlight = _book.EpubHighlights?.FirstOrDefault(h => h.Id == id);
            if (highlight == null)
                return;

            var note = await PromptNoteAsync("Highlight note", "Edit note:", highlight.Note ?? string.Empty);
            if (note == null)
                return;

            highlight.Note = string.IsNullOrWhiteSpace(note) ? null : note;
            RefreshHighlightList();
            RefreshNoteGutter();
            ProgressChanged?.Invoke(_book);
        }

        private void DeleteHighlight_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag } && Guid.TryParse(tag, out var id))
            {
                RemoveHighlightById(id);
                return;
            }

            if (sender is FrameworkElement { Tag: Guid guid })
                RemoveHighlightById(guid);
        }

        private void RemoveHighlightById(Guid id)
        {
            _book.EpubHighlights ??= new List<EpubHighlight>();
            if (_book.EpubHighlights.RemoveAll(h => h.Id == id) <= 0)
                return;

            _expandedNoteIds.Remove(id);
            foreach (var key in _noteClusterSelectedIds.Keys.ToList())
            {
                if (key.Contains(id.ToString("N"), StringComparison.Ordinal) ||
                    _noteClusterSelectedIds[key] == id)
                    _noteClusterSelectedIds.Remove(key);
            }

            RefreshHighlightList();
            if (_chapterIndex >= 0 && _chapterIndex < _chapters.Count)
                RenderChapter(_chapters[_chapterIndex]);
            ProgressChanged?.Invoke(_book);
        }

        private void JumpToHighlight(EpubHighlight highlight)
        {
            if (_chapters.Count == 0)
                return;

            var index = Math.Clamp(highlight.ChapterIndex, 0, _chapters.Count - 1);
            var plain = GetChapterPlainText(index);
            var ratio = plain.Length <= 1
                ? 0
                : Math.Clamp((double)highlight.StartOffset / plain.Length, 0, 1);
            ShowChapter(index, restoreScroll: true, scrollRatioOverride: ratio);
        }

        private void RefreshHighlightList()
        {
            _book.EpubHighlights ??= new List<EpubHighlight>();
            _highlightItems.Clear();

            foreach (var highlight in _book.EpubHighlights
                         .OrderBy(h => h.ChapterIndex)
                         .ThenBy(h => h.StartOffset)
                         .ThenBy(h => h.CreatedUtc))
            {
                var source = GetChapterPlainText(highlight.ChapterIndex);
                var text = ExtractHighlightText(source, highlight.StartOffset, highlight.EndOffset);
                var note = NormalizeNoteText(highlight.Note);
                var chapterTitle = highlight.ChapterIndex >= 0 && highlight.ChapterIndex < _chapters.Count
                    ? _chapters[highlight.ChapterIndex].Title
                    : $"Section {highlight.ChapterIndex + 1}";
                var plainLen = Math.Max(source.Length, 1);
                var percent = (int)Math.Round(Math.Clamp((double)highlight.StartOffset / plainLen, 0, 1) * 100);
                _highlightItems.Add(new HighlightListItem
                {
                    Highlight = highlight,
                    DisplayLabel = text,
                    NoteText = note,
                    DetailText = $"{chapterTitle} · {percent}%"
                });
            }

            HighlightEmptyText.Visibility = _highlightItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshNoteGutter();
        }

        /// <summary>
        /// Places highlight notes for the current chapter in the right gutter, aligned to the highlight.
        /// Overlaps cascade downward with a minimum gap.
        /// </summary>
        private void RefreshNoteGutter()
        {
            if (CommentGutter == null || PageText == null || !_commentGutterExpanded)
                return;

            CommentGutter.Children.Clear();
            _book.EpubHighlights ??= new List<EpubHighlight>();

            var plainLen = Math.Max(GetChapterPlainText(_chapterIndex).Length, 1);
            var contentHeight = Math.Max(PageText.ActualHeight, 1);
            var gutterInnerWidth = CommentGutterHost.ActualWidth > 1
                ? Math.Max(CommentGutterHost.ActualWidth - CommentGutterHost.Padding.Left - CommentGutterHost.Padding.Right, 180)
                : CommentGutterWidth - 20;

            var noted = _book.EpubHighlights
                .Where(h => h.ChapterIndex == _chapterIndex && !string.IsNullOrWhiteSpace(h.Note))
                .Select(h =>
                {
                    var ratio = Math.Clamp((double)h.StartOffset / plainLen, 0, 1);
                    return (Highlight: h, IdealY: ratio * contentHeight);
                })
                .OrderBy(x => x.IdealY)
                .ThenBy(x => x.Highlight.CreatedUtc)
                .ToList();

            var clusters = new List<List<(EpubHighlight Highlight, double IdealY)>>();
            foreach (var item in noted)
            {
                if (clusters.Count == 0)
                {
                    clusters.Add(new List<(EpubHighlight, double)> { item });
                    continue;
                }

                var current = clusters[^1];
                var lastIdeal = current[^1].IdealY;
                if (item.IdealY - lastIdeal <= NoteClusterProximityPx)
                    current.Add(item);
                else
                    clusters.Add(new List<(EpubHighlight, double)> { item });
            }

            double nextTop = 0;
            double maxBottom = contentHeight;

            foreach (var cluster in clusters)
            {
                var idealY = cluster[0].IdealY;
                var highlights = cluster.Select(c => c.Highlight).ToList();
                var card = CreateMarginNoteCluster(highlights, gutterInnerWidth);
                card.Measure(new Windows.Foundation.Size(gutterInnerWidth, double.PositiveInfinity));
                var height = Math.Max(card.DesiredSize.Height, 56);
                var top = Math.Max(idealY, nextTop);

                Canvas.SetLeft(card, 0);
                Canvas.SetTop(card, top);
                CommentGutter.Children.Add(card);

                if (Math.Abs(top - idealY) > 1)
                {
                    var tickColor = GetHighlightColor(highlights[0].Color, isTts: false);
                    var tick = new Border
                    {
                        Width = 3,
                        Height = 14,
                        CornerRadius = new CornerRadius(1.5),
                        Background = new SolidColorBrush(tickColor),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(tick, -6);
                    Canvas.SetTop(tick, idealY);
                    CommentGutter.Children.Add(tick);
                }

                nextTop = top + height + CommentCardMinGap;
                maxBottom = Math.Max(maxBottom, nextTop);
            }

            CommentGutter.Width = gutterInnerWidth;
            CommentGutter.Height = maxBottom;
        }

        private static string GetNoteClusterKey(IReadOnlyList<EpubHighlight> highlights)
            => string.Join("|", highlights.Select(h => h.Id.ToString("N")));

        private Border CreateMarginNoteCluster(IReadOnlyList<EpubHighlight> highlights, double width)
        {
            if (highlights.Count == 1)
                return CreateSingleMarginNoteCard(highlights[0], width);

            var clusterKey = GetNoteClusterKey(highlights);
            var selectedId = _noteClusterSelectedIds.TryGetValue(clusterKey, out var saved) &&
                             highlights.Any(h => h.Id == saved)
                ? saved
                : highlights[0].Id;
            _noteClusterSelectedIds[clusterKey] = selectedId;

            var root = new Border
            {
                Width = width,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
                Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
                Tag = clusterKey
            };
            root.SizeChanged += (_, e) =>
            {
                root.Clip = new RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
                };
            };

            var layout = new Grid { RowSpacing = 0 };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tabStrip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Padding = new Thickness(6, 6, 6, 4)
            };
            var tabScroller = new ScrollViewer
            {
                Content = tabStrip,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled
            };
            Grid.SetRow(tabScroller, 0);

            var selected = highlights.FirstOrDefault(h => h.Id == selectedId) ?? highlights[0];
            selectedId = selected.Id;
            _noteClusterSelectedIds[clusterKey] = selectedId;
            root.BorderBrush = new SolidColorBrush(GetHighlightColor(selected.Color, isTts: false));

            const double bodyPadX = 20;
            var bodyHost = new Border
            {
                Padding = new Thickness(10, 4, 10, 8),
                MinHeight = 40,
                Child = BuildNoteBody(selected, Math.Max(width - bodyPadX, 40)),
                ContextFlyout = BuildNoteContextMenu(selected.Id)
            };
            Grid.SetRow(bodyHost, 1);

            for (var i = 0; i < highlights.Count; i++)
            {
                var highlight = highlights[i];
                var tab = CreateNoteTab(highlight, i, isSelected: highlight.Id == selectedId);
                tab.Tapped += (_, e) =>
                {
                    e.Handled = true;
                    if (_noteClusterSelectedIds.TryGetValue(clusterKey, out var current) && current == highlight.Id)
                        return;
                    _noteClusterSelectedIds[clusterKey] = highlight.Id;
                    RefreshNoteGutterPreservingScroll();
                };
                tabStrip.Children.Add(tab);
            }

            layout.Children.Add(tabScroller);
            layout.Children.Add(bodyHost);
            root.Child = layout;
            return root;
        }

        private Border CreateSingleMarginNoteCard(EpubHighlight highlight, double width)
        {
            const double padX = 20;
            var body = BuildNoteBody(highlight, Math.Max(width - padX, 40));
            var card = new Border
            {
                Width = width,
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(GetHighlightColor(highlight.Color, isTts: false)),
                Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
                Child = body,
                Tag = highlight.Id,
                ContextFlyout = BuildNoteContextMenu(highlight.Id)
            };
            card.SizeChanged += (_, e) =>
            {
                card.Clip = new RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
                };
            };
            return card;
        }

        private static bool NoteNeedsCollapse(string noteText, double contentWidth)
        {
            if (string.IsNullOrWhiteSpace(noteText) || contentWidth <= 1)
                return false;

            var probe = new TextBlock
            {
                Text = noteText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Width = contentWidth
            };
            probe.Measure(new Windows.Foundation.Size(contentWidth, double.PositiveInfinity));
            var fullHeight = probe.DesiredSize.Height;

            probe.MaxLines = NoteCollapsedMaxLines;
            probe.TextTrimming = TextTrimming.CharacterEllipsis;
            probe.InvalidateMeasure();
            probe.Measure(new Windows.Foundation.Size(contentWidth, double.PositiveInfinity));
            return fullHeight > probe.DesiredSize.Height + 0.5;
        }

        private FrameworkElement BuildNoteBody(EpubHighlight highlight, double contentWidth)
        {
            var noteText = NormalizeNoteText(highlight.Note);
            var needsCollapse = NoteNeedsCollapse(noteText, contentWidth);
            var expanded = needsCollapse && _expandedNoteIds.Contains(highlight.Id);
            if (!needsCollapse)
                _expandedNoteIds.Remove(highlight.Id);

            // Explicit Width is required: Canvas measures with infinite width, so without it
            // TextBlock grows sideways instead of wrapping downward.
            var body = new TextBlock
            {
                Text = noteText,
                Width = contentWidth,
                MaxWidth = contentWidth,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                IsTextSelectionEnabled = false,
                IsHitTestVisible = false,
                MaxLines = needsCollapse && !expanded ? NoteCollapsedMaxLines : 0,
                TextTrimming = needsCollapse && !expanded ? TextTrimming.CharacterEllipsis : TextTrimming.None
            };

            var stack = new StackPanel
            {
                Spacing = 4,
                Width = contentWidth
            };
            stack.Children.Add(body);

            var host = new Border
            {
                Width = contentWidth,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Child = stack
            };

            if (needsCollapse)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = expanded ? "Click to collapse" : "Click to expand",
                    FontSize = 11,
                    Opacity = 0.65,
                    IsHitTestVisible = false
                });

                host.Tapped += (_, e) =>
                {
                    e.Handled = true;
                    if (!_expandedNoteIds.Add(highlight.Id))
                        _expandedNoteIds.Remove(highlight.Id);
                    RefreshNoteGutterPreservingScroll();
                };
            }

            return host;
        }

        private void RefreshNoteGutterPreservingScroll()
        {
            if (PageScroller == null)
            {
                RefreshNoteGutter();
                return;
            }

            var offset = PageScroller.VerticalOffset;
            RefreshNoteGutter();
            PageScroller.UpdateLayout();
            PageScroller.ChangeView(null, offset, null, disableAnimation: true);
        }

        private MenuFlyout BuildNoteContextMenu(Guid highlightId)
        {
            var editItem = new MenuFlyoutItem { Text = "Edit note…" };
            editItem.Click += async (_, _) => await EditHighlightNoteByIdAsync(highlightId);
            var deleteItem = new MenuFlyoutItem { Text = "Delete highlight + note" };
            deleteItem.Click += (_, _) => RemoveHighlightById(highlightId);

            var menu = new MenuFlyout();
            menu.Items.Add(editItem);
            menu.Items.Add(deleteItem);
            return menu;
        }

        private Border CreateNoteTab(EpubHighlight highlight, int index, bool isSelected)
        {
            var tab = new Border
            {
                MinWidth = 28,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 0, 6, 0),
                Child = new TextBlock
                {
                    Text = (index + 1).ToString(),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            ApplyNoteTabVisual(tab, highlight, isSelected, index);
            return tab;
        }

        private static void ApplyNoteTabVisual(Border tab, EpubHighlight highlight, bool isSelected, int index)
        {
            var fill = GetHighlightColor(highlight.Color, isTts: false);
            // Same-color tabs stay distinguishable via index label + alternating border/contrast.
            var borderAlpha = (byte)(isSelected ? 255 : 160);
            var outline = Color.FromArgb(
                borderAlpha,
                (byte)Math.Clamp(fill.R - 40 - (index % 3) * 18, 0, 255),
                (byte)Math.Clamp(fill.G - 40 - ((index + 1) % 3) * 18, 0, 255),
                (byte)Math.Clamp(fill.B - 40 - ((index + 2) % 3) * 18, 0, 255));

            tab.Background = new SolidColorBrush(Color.FromArgb(
                isSelected ? (byte)220 : (byte)140,
                fill.R, fill.G, fill.B));
            tab.BorderThickness = new Thickness(isSelected ? 2 : 1);
            tab.BorderBrush = new SolidColorBrush(outline);
            tab.Opacity = isSelected ? 1.0 : 0.78;

            if (tab.Child is TextBlock label)
            {
                label.Foreground = new SolidColorBrush(
                    IsLightHighlight(fill)
                        ? Color.FromArgb(255, 20, 20, 20)
                        : Color.FromArgb(255, 245, 245, 245));
                // Pattern under number: solid vs underline style via character for same-color tabs.
                label.Text = isSelected ? $"●{index + 1}" : $"{index + 1}";
            }
        }

        private static bool IsLightHighlight(Color c)
            => (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) > 160;

        private static string ExtractHighlightText(string source, int startOffset, int endOffset)
        {
            if (string.IsNullOrEmpty(source))
                return "(highlight)";

            var start = Math.Clamp(startOffset, 0, source.Length);
            var end = Math.Clamp(endOffset, start, source.Length);
            if (end <= start)
                return "(highlight)";

            var text = source[start..end]
                .Replace('\n', ' ')
                .Replace('\r', ' ');
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length == 0)
                return "(highlight)";
            if (text.Length > 160)
                text = text[..160] + "…";
            return text;
        }

        private void InitTtsVoices()
        {
            TtsVoiceCombo.Items.Clear();
            var voices = _tts.GetVoices();
            ComboBoxItem? selectedItem = null;
            foreach (var voice in voices)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{voice.DisplayName} ({voice.Language})",
                    Tag = voice
                };
                TtsVoiceCombo.Items.Add(item);
                if (selectedItem == null &&
                    voice.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    selectedItem = item;
                }
            }

            if (selectedItem != null)
            {
                TtsVoiceCombo.SelectedItem = selectedItem;
                if (selectedItem.Tag is VoiceInformation voice)
                    _tts.SetVoice(voice);
            }
            else if (TtsVoiceCombo.Items.Count > 0)
            {
                TtsVoiceCombo.SelectedIndex = 0;
                if (TtsVoiceCombo.SelectedItem is ComboBoxItem { Tag: VoiceInformation v })
                    _tts.SetVoice(v);
            }

            _suppressTtsVolumeSlider = true;
            TtsVolumeSlider.Value = Math.Clamp(_tts.Volume * 100.0, 0, 100);
            TtsVolumeValueText.Text = $"{(int)Math.Round(TtsVolumeSlider.Value)}%";
            _suppressTtsVolumeSlider = false;

            _suppressTtsSpeedSlider = true;
            TtsSpeedSlider.Value = Math.Clamp(_tts.SpeakingRate, 0.5, 2.0);
            TtsSpeedValueText.Text = $"{TtsSpeedSlider.Value:0.0}×";
            _suppressTtsSpeedSlider = false;

            UpdateTtsUi();
        }

        private void TtsVoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TtsVoiceCombo.SelectedItem is ComboBoxItem { Tag: VoiceInformation voice })
                _tts.SetVoice(voice);
        }

        private void TtsVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppressTtsVolumeSlider || TtsVolumeSlider == null)
                return;

            _tts.Volume = e.NewValue / 100.0;
            if (TtsVolumeValueText != null)
                TtsVolumeValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
        }

        private void TtsSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppressTtsSpeedSlider || TtsSpeedSlider == null)
                return;

            _tts.SpeakingRate = e.NewValue;
            if (TtsSpeedValueText != null)
                TtsSpeedValueText.Text = $"{e.NewValue:0.0}×";
        }

        private async Task TtsStartOrResumeAsync()
        {
            if (_chapters.Count == 0)
                return;

            if (_tts.IsPlaying && _tts.IsPaused)
            {
                _tts.Resume();
                UpdateTtsUi();
                return;
            }

            if (_tts.IsPlaying)
                return;

            var startChapter = _chapterIndex;
            var startOffset = 0;

            if (!string.IsNullOrWhiteSpace(PageText.SelectedText) &&
                TryGetSelectionOffsets(out var selStart, out _, out _))
            {
                startOffset = selStart;
            }
            else if (_book.LastEpubTtsPosition is { } saved &&
                     saved.ChapterIndex >= 0 &&
                     saved.ChapterIndex < _chapters.Count)
            {
                startChapter = saved.ChapterIndex;
                startOffset = Math.Max(0, saved.CharOffset);
            }
            else
            {
                CaptureScrollRatioQuiet();
                var plain = GetChapterPlainText(_chapterIndex);
                startOffset = plain.Length <= 1
                    ? 0
                    : (int)Math.Clamp(_book.LastEpubScrollRatio * plain.Length, 0, plain.Length);
            }

            var chapters = _chapters
                .Select((_, i) => (i, GetChapterPlainText(i)))
                .ToList();

            UpdateTtsUi();
            await _tts.StartAsync(chapters, startChapter, startOffset);
            PersistTtsPosition();
            UpdateTtsUi();
        }

        private void TtsStopButton_Click(object sender, RoutedEventArgs e)
        {
            PersistTtsPosition();
            CancelTtsSleepTimer();
            _tts.Stop();
            _ttsActiveStart = null;
            _ttsActiveEnd = null;
            ApplyHighlightsToPage();
            UpdateTtsUi();
        }

        private void TtsSleepCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTtsSleepCombo || TtsSleepCombo == null)
                return;

            if (TtsSleepCombo.SelectedItem is not ComboBoxItem { Tag: string tag } ||
                !int.TryParse(tag, out var minutes))
            {
                return;
            }

            if (minutes <= 0)
            {
                CancelTtsSleepTimer();
                UpdateTtsUi();
                return;
            }

            _ttsSleep.StartMinutes(minutes);
            if (!_ttsSleepUiTimer.IsEnabled)
                _ttsSleepUiTimer.Start();
            UpdateTtsUi();
        }

        private void OnTtsSleepTick()
        {
            if (!_ttsSleep.IsActive)
            {
                _ttsSleepUiTimer.Stop();
                UpdateTtsUi();
                return;
            }

            if (_ttsSleep.HasExpired())
            {
                PersistTtsPosition();
                CancelTtsSleepTimer();
                _tts.Stop();
                _ttsActiveStart = null;
                _ttsActiveEnd = null;
                ApplyHighlightsToPage();
                UpdateTtsUi();
                return;
            }

            UpdateTtsSleepStatus();
            if (TtsButton != null)
                ToolTipService.SetToolTip(TtsButton, $"Listening · sleep {_ttsSleep.FormatRemaining()}");
        }

        private void CancelTtsSleepTimer()
        {
            _ttsSleep.Cancel();
            _ttsSleepUiTimer.Stop();
            _suppressTtsSleepCombo = true;
            if (TtsSleepCombo != null)
            {
                foreach (var item in TtsSleepCombo.Items)
                {
                    if (item is ComboBoxItem { Tag: string tag } cbi && tag == "0")
                    {
                        TtsSleepCombo.SelectedItem = cbi;
                        break;
                    }
                }
            }
            _suppressTtsSleepCombo = false;
        }

        private void UpdateTtsSleepStatus()
        {
            if (TtsSleepStatusText == null)
                return;

            TtsSleepStatusText.Text = _ttsSleep.IsActive
                ? $"Sleep in {_ttsSleep.FormatRemaining()}"
                : string.Empty;
        }

        private void OnTtsPositionChanged(int chapterIndex, int chunkStart, int chunkEnd)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _book.LastEpubTtsPosition = new EpubTtsPosition
                {
                    ChapterIndex = chapterIndex,
                    CharOffset = chunkStart,
                    UpdatedUtc = DateTime.UtcNow
                };

                if (chunkEnd > chunkStart)
                {
                    _ttsActiveStart = chunkStart;
                    _ttsActiveEnd = chunkEnd;
                }

                if (chapterIndex != _chapterIndex)
                {
                    ShowChapter(chapterIndex, restoreScroll: true,
                        scrollRatioOverride: Math.Clamp(
                            (double)chunkStart / Math.Max(GetChapterPlainText(chapterIndex).Length, 1), 0, 1));
                }
                else if (_chapterIndex >= 0 && _chapterIndex < _chapters.Count)
                {
                    ApplyHighlightsToPage();
                    var len = Math.Max(GetChapterPlainText(chapterIndex).Length, 1);
                    var ratio = Math.Clamp((double)chunkStart / len, 0, 1);
                    _ = PageScroller.ChangeView(null, ratio * PageScroller.ScrollableHeight, null, true);
                }

                UpdateTtsUi();
            });
        }

        /// <summary>
        /// Plain text for offsets shared by search, highlights, and TTS.
        /// Prefers the currently rendered chapter text so selection offsets stay aligned.
        /// </summary>
        private string GetChapterPlainText(int chapterIndex)
        {
            if (chapterIndex == _chapterIndex)
            {
                var rendered = GetRenderedPlainText(PageText);
                if (rendered.Length > 0)
                    return rendered;
            }

            if (chapterIndex < 0 || chapterIndex >= _chapters.Count)
                return string.Empty;

            return _chapters[chapterIndex].PlainText ?? string.Empty;
        }

        private void PersistTtsPosition()
        {
            if (!_tts.IsPlaying && _book.LastEpubTtsPosition == null)
                return;

            _book.LastEpubTtsPosition = new EpubTtsPosition
            {
                ChapterIndex = _tts.ChapterIndex,
                CharOffset = _tts.CharOffset,
                UpdatedUtc = DateTime.UtcNow
            };
            ProgressChanged?.Invoke(_book);
        }

        private void UpdateTtsUi()
        {
            if (TtsButton != null)
            {
                TtsButton.Content = _tts.IsPlaying && !_tts.IsPaused ? "Pause" : "Listen";
                ToolTipService.SetToolTip(
                    TtsButton,
                    _ttsSleep.IsActive
                        ? $"Sleep {_ttsSleep.FormatRemaining()}"
                        : (_tts.IsPlaying && !_tts.IsPaused ? "Pause listening" : "Play or resume listening"));
            }

            UpdateTtsSleepStatus();

            if (TtsStatusText == null)
                return;

            if (_tts.IsPlaying && _tts.IsPaused)
                TtsStatusText.Text = $"Paused · section {_tts.ChapterIndex + 1}";
            else if (_tts.IsPlaying)
                TtsStatusText.Text = $"Reading section {_tts.ChapterIndex + 1}…";
            else
                TtsStatusText.Text = _book.LastEpubTtsPosition != null
                    ? $"Stopped · resume section {_book.LastEpubTtsPosition.ChapterIndex + 1}"
                    : "Stopped";
        }

        /// <summary>
        /// Google Docs and other sources often paste \r, U+2028, or U+2029 as line breaks.
        /// Normalize to \n so TextBox/TextBlock show every line.
        /// </summary>
        private static string NormalizeNoteText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Replace('\u2028', '\n')
                .Replace('\u2029', '\n')
                .Trim();
        }

        private async Task<string?> PromptNoteAsync(string title, string message, string initial)
            => await PromptTextAsync(title, message, initial, multiline: true);

        private async Task<string?> PromptTextAsync(
            string title,
            string message,
            string initial,
            bool multiline = true)
        {
            var normalized = NormalizeNoteText(initial);
            // AcceptsReturn must be enabled BEFORE Text is assigned — otherwise WinUI
            // TextBox keeps only the first line and drops everything after the first Enter.
            var box = new TextBox
            {
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Height = multiline ? 240 : double.NaN,
                MinHeight = multiline ? 160 : 0,
                PlaceholderText = message
            };
            if (multiline)
            {
                ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
                ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Disabled);
            }
            box.Text = normalized;

            var dialog = new ContentDialog
            {
                Title = title,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
                        box
                    }
                },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return null;

            return multiline ? NormalizeNoteText(box.Text) : box.Text.Trim();
        }

        private async Task ShowInfoAsync(string title, string message)
        {
            await new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            }.ShowAsync();
        }

        private sealed class BookmarkListItem
        {
            public EpubBookmark Bookmark { get; set; } = new();
            public Guid Id => Bookmark.Id;
            public string DisplayLabel { get; set; } = string.Empty;
            public string DetailText { get; set; } = string.Empty;
        }

        private sealed class HighlightListItem
        {
            public EpubHighlight Highlight { get; set; } = new();
            public Guid Id => Highlight.Id;
            public string DisplayLabel { get; set; } = string.Empty;
            public string NoteText { get; set; } = string.Empty;
            public string DetailText { get; set; } = string.Empty;
            public Visibility NoteVisibility =>
                string.IsNullOrWhiteSpace(NoteText) ? Visibility.Collapsed : Visibility.Visible;
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
