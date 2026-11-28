using AudiobookApp.Models;
using AudiobookApp.Services;
using System.Text.Json;

namespace AudiobookApp.Tests;

public class CbzComicServiceTests
{
    [Fact]
    public void SortPageNames_UsesNaturalOrder()
    {
        var input = new[]
        {
            "page10.jpg",
            "page2.jpg",
            "page1.jpg",
            "readme.txt",
            "Page03.PNG"
        };

        var sorted = CbzComicService.SortPageNames(input);
        Assert.Equal(new[] { "page1.jpg", "page2.jpg", "Page03.PNG", "page10.jpg" }, sorted);
    }

    [Fact]
    public void ComicReadingMode_RoundTripsInJson()
    {
        var book = new Book
        {
            Title = "Test",
            MediaType = BookMediaType.Comic,
            LastComicPageIndex = 7,
            ComicReadingMode = ComicReadingMode.VerticalStrip
        };

        var json = JsonSerializer.Serialize(book);
        var loaded = JsonSerializer.Deserialize<Book>(json);
        Assert.NotNull(loaded);
        Assert.Equal(BookMediaType.Comic, loaded!.MediaType);
        Assert.Equal(7, loaded.LastComicPageIndex);
        Assert.Equal(ComicReadingMode.VerticalStrip, loaded.ComicReadingMode);
    }
}
