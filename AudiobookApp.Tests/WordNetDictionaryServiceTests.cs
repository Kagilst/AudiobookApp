using AudiobookApp.Services;

namespace AudiobookApp.Tests;

public class WordNetDictionaryServiceTests
{
    [Theory]
    [InlineData("mountain", "mountain")]
    [InlineData("  Hello, ", "Hello")]
    [InlineData("sunbursts of light", "sunbursts")]
    [InlineData("", "")]
    public void ExtractLookupWord_PicksFirstToken(string input, string expected)
        => Assert.Equal(expected, WordNetDictionaryService.ExtractLookupWord(input));

    [Fact]
    public void Lookup_FindsCommonNoun()
    {
        using var dict = new WordNetDictionaryService();
        Assert.True(dict.IsAvailable, "wordnet-en.db should be copied next to the test host under Assets/Dictionary.");

        var result = dict.Lookup("mountain");
        Assert.True(result.Found);
        Assert.Equal("mountain", result.MatchedLemma, ignoreCase: true);
        Assert.Contains(result.Senses, s => s.PartOfSpeech == "n");
        Assert.Contains(result.Senses, s => s.Definition.Contains("natural elevation", StringComparison.OrdinalIgnoreCase)
                                            || s.Definition.Length > 10);
    }

    [Fact]
    public void Lookup_UsesSimplePluralFallback()
    {
        using var dict = new WordNetDictionaryService();
        Assert.True(dict.IsAvailable);

        var result = dict.Lookup("mountains");
        Assert.True(result.Found);
        Assert.Equal("mountain", result.MatchedLemma, ignoreCase: true);
    }
}
