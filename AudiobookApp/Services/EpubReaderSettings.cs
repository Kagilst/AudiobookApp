using System;
using System.IO;
using System.Text.Json;

namespace AudiobookApp.Services
{
    /// <summary>
    /// App-wide EPUB reading defaults (font, spacing, width).
    /// </summary>
    public sealed class EpubReaderSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudiobookApp",
            "epub-reader-settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public double FontSize { get; set; } = 18;
        public string FontFamily { get; set; } = "serif";
        public string LineSpacing { get; set; } = "normal";
        public string ContentWidth { get; set; } = "medium";

        public static EpubReaderSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return FromLocalSettingsFallback() ?? new EpubReaderSettings();

                return LoadFrom(SettingsPath) ?? new EpubReaderSettings();
            }
            catch
            {
                return FromLocalSettingsFallback() ?? new EpubReaderSettings();
            }
        }

        public static EpubReaderSettings? LoadFrom(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<EpubReaderSettings>(json, JsonOptions);
        }

        public void Save() => SaveTo(SettingsPath);

        public void SaveTo(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch
            {
                // Keep in-memory prefs for this session.
            }
        }

        /// <summary>
        /// Migrate any values previously stored in ApplicationData.LocalSettings.
        /// </summary>
        private static EpubReaderSettings? FromLocalSettingsFallback()
        {
            try
            {
                var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                if (!values.ContainsKey("EpubReaderFontSize")
                    && !values.ContainsKey("EpubReaderFontFamily")
                    && !values.ContainsKey("EpubReaderLineSpacing")
                    && !values.ContainsKey("EpubReaderContentWidth"))
                {
                    return null;
                }

                var settings = new EpubReaderSettings();

                if (values.TryGetValue("EpubReaderFontSize", out var sizeObj))
                    settings.FontSize = Convert.ToDouble(sizeObj);

                if (values.TryGetValue("EpubReaderFontFamily", out var familyObj)
                    && familyObj is string family)
                {
                    settings.FontFamily = family;
                }

                if (values.TryGetValue("EpubReaderLineSpacing", out var spacingObj)
                    && spacingObj is string spacing)
                {
                    settings.LineSpacing = spacing;
                }

                if (values.TryGetValue("EpubReaderContentWidth", out var widthObj)
                    && widthObj is string width)
                {
                    settings.ContentWidth = width;
                }

                settings.Save();
                return settings;
            }
            catch
            {
                return null;
            }
        }
    }
}
