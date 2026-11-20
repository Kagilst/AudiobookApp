using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AudiobookApp.Services
{
    public static class CoverImageService
    {
        private static readonly string[] ImageExtensions =
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
        };

        public static async Task<string?> PickAndStoreCoverAsync(Window window, Guid bookId)
        {
            var picker = new FileOpenPicker();
            foreach (var ext in ImageExtensions)
                picker.FileTypeFilter.Add(ext);

            var hwnd = WindowNative.GetWindowHandle(window);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null)
                return null;

            var coversDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudiobookApp",
                "covers");

            Directory.CreateDirectory(coversDir);

            var extension = Path.GetExtension(file.Name);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".jpg";

            var destPath = Path.Combine(coversDir, $"{bookId:N}{extension}");
            if (File.Exists(destPath))
                File.Delete(destPath);

            var destFolder = await StorageFolder.GetFolderFromPathAsync(coversDir);
            var copied = await file.CopyAsync(
                destFolder,
                Path.GetFileName(destPath),
                NameCollisionOption.ReplaceExisting);

            return copied.Path;
        }
    }
}
