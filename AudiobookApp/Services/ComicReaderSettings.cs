using System;
using System.IO;
using System.Text.Json;

namespace AudiobookApp.Services
{
    /// <summary>
    /// App-wide comic reader defaults (page width, zoom).
    /// </summary>
    public sealed class ComicReaderSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudiobookApp",
            "comic-reader-settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        /// <summary>thin | normal | wide</summary>
        public string PageWidth { get; set; } = "normal";

        /// <summary>Zoom factor, typically 0.5–3.0.</summary>
        public double Zoom { get; set; } = 1.0;

        public static ComicReaderSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new ComicReaderSettings();

                return LoadFrom(SettingsPath) ?? new ComicReaderSettings();
            }
            catch
            {
                return new ComicReaderSettings();
            }
        }

        public static ComicReaderSettings? LoadFrom(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ComicReaderSettings>(json, JsonOptions);
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
    }
}
