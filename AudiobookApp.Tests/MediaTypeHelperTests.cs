using AudiobookApp.Models;
using AudiobookApp.Services;

namespace AudiobookApp.Tests;

public class MediaTypeHelperTests
{
    [Theory]
    [InlineData("book.epub", BookMediaType.Epub)]
    [InlineData(".EPUB", BookMediaType.Epub)]
    [InlineData("C:\\comics\\issue.cbz", BookMediaType.Comic)]
    [InlineData(".cbz", BookMediaType.Comic)]
    [InlineData("track.mp3", BookMediaType.Audiobook)]
    [InlineData("file.pdf", null)]
    public void FromExtension_MapsKnownTypes(string input, BookMediaType? expected)
        => Assert.Equal(expected, MediaTypeHelper.FromExtension(input));

    [Theory]
    [InlineData("page01.jpg", true)]
    [InlineData("cover.PNG", true)]
    [InlineData("note.txt", false)]
    [InlineData("archive.cbz", false)]
    public void IsComicImageExtension_DetectsImages(string input, bool expected)
        => Assert.Equal(expected, MediaTypeHelper.IsComicImageExtension(input));
}
