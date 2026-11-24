using System.Text.Json;
using AudiobookApp.Models;

namespace AudiobookApp.Tests;

public class BookAnnotationJsonTests
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    [Fact]
    public void Book_Annotations_RoundTrip()
    {
        var book = new Book
        {
            Title = "Test EPUB",
            MediaType = BookMediaType.Epub,
            FilePath = @"C:\books\test.epub",
            LastChapterIndex = 2,
            LastEpubScrollRatio = 0.42,
            EpubBookmarks =
            {
                new EpubBookmark
                {
                    ChapterIndex = 1,
                    ScrollRatio = 0.25,
                    Label = "Chapter mark"
                }
            },
            EpubHighlights =
            {
                new EpubHighlight
                {
                    ChapterIndex = 1,
                    StartOffset = 10,
                    EndOffset = 40,
                    Color = EpubHighlightColor.Green,
                    Note = "Nice line"
                }
            },
            EpubComments =
            {
                new EpubComment
                {
                    ChapterIndex = 1,
                    ScrollRatio = 0.5,
                    CharOffset = 100,
                    Body = "Margin note"
                }
            },
            LastEpubTtsPosition = new EpubTtsPosition
            {
                ChapterIndex = 2,
                CharOffset = 55
            }
        };

        var json = JsonSerializer.Serialize(book, Options);
        var loaded = JsonSerializer.Deserialize<Book>(json, Options);

        Assert.NotNull(loaded);
        Assert.Equal(BookMediaType.Epub, loaded!.MediaType);
        Assert.Single(loaded.EpubBookmarks);
        Assert.Equal("Chapter mark", loaded.EpubBookmarks[0].Label);
        Assert.Single(loaded.EpubHighlights);
        Assert.Equal(EpubHighlightColor.Green, loaded.EpubHighlights[0].Color);
        Assert.Equal("Nice line", loaded.EpubHighlights[0].Note);
        Assert.Single(loaded.EpubComments);
        Assert.Equal(100, loaded.EpubComments[0].CharOffset);
        Assert.NotNull(loaded.LastEpubTtsPosition);
        Assert.Equal(55, loaded.LastEpubTtsPosition!.CharOffset);
    }
}
