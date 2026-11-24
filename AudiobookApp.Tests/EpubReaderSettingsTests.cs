using AudiobookApp.Services;

namespace AudiobookApp.Tests;

public class EpubReaderSettingsTests
{
    [Fact]
    public void SaveTo_LoadFrom_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "AudiobookAppTests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var settings = new EpubReaderSettings
            {
                FontSize = 22,
                FontFamily = "sans",
                LineSpacing = "relaxed",
                ContentWidth = "wide"
            };
            settings.SaveTo(path);

            var loaded = EpubReaderSettings.LoadFrom(path);
            Assert.NotNull(loaded);
            Assert.Equal(22, loaded!.FontSize);
            Assert.Equal("sans", loaded.FontFamily);
            Assert.Equal("relaxed", loaded.LineSpacing);
            Assert.Equal("wide", loaded.ContentWidth);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadFrom_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "AudiobookAppTests", Guid.NewGuid().ToString("N"), "missing.json");
        Assert.Null(EpubReaderSettings.LoadFrom(path));
    }
}
