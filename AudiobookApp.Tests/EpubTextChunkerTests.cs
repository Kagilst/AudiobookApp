using AudiobookApp.Services;

namespace AudiobookApp.Tests;

public class EpubTextChunkerTests
{
    [Fact]
    public void SplitIntoChunks_Empty_ReturnsEmpty()
    {
        Assert.Empty(EpubTextChunker.SplitIntoChunks("", 0));
        Assert.Empty(EpubTextChunker.SplitIntoChunks("   ", 0));
    }

    [Fact]
    public void SplitIntoChunks_RespectsStartOffsetAndSentenceBreaks()
    {
        var text = "First sentence. Second sentence! Third one?";
        var chunks = EpubTextChunker.SplitIntoChunks(text, 0, maxLength: 20);
        Assert.NotEmpty(chunks);
        Assert.Equal(0, chunks[0].Offset);
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
    }

    [Fact]
    public void SplitIntoChunks_FromMiddle_DoesNotIncludeEarlierText()
    {
        var text = "AAAA. BBBB. CCCC.";
        var start = text.IndexOf("BBBB", StringComparison.Ordinal);
        var chunks = EpubTextChunker.SplitIntoChunks(text, start, maxLength: 80);
        Assert.NotEmpty(chunks);
        Assert.True(chunks[0].Offset >= start);
        Assert.DoesNotContain("AAAA", chunks[0].Text);
    }

    [Theory]
    [InlineData(-1, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.5, 1.0)]
    public void ClampVolume_Clamps(double input, double expected) =>
        Assert.Equal(expected, EpubTextChunker.ClampVolume(input));

    [Theory]
    [InlineData(0.1, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(9.0, 6.0)]
    public void ClampSpeakingRate_Clamps(double input, double expected) =>
        Assert.Equal(expected, EpubTextChunker.ClampSpeakingRate(input));
}
