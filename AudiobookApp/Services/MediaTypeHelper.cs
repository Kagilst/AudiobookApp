using System;
using System.IO;
using AudiobookApp.Models;

namespace AudiobookApp.Services
{
    public static class MediaTypeHelper
    {
        public static BookMediaType? FromExtension(string? pathOrExtension)
        {
            if (string.IsNullOrWhiteSpace(pathOrExtension))
                return null;

            var ext = pathOrExtension.Contains('.') && !pathOrExtension.StartsWith('.')
                ? Path.GetExtension(pathOrExtension)
                : pathOrExtension;
            if (string.IsNullOrWhiteSpace(ext))
                return null;

            if (!ext.StartsWith('.'))
                ext = "." + ext;

            return ext.ToLowerInvariant() switch
            {
                ".mp3" or ".m4b" or ".mp4" => BookMediaType.Audiobook,
                ".epub" => BookMediaType.Epub,
                ".cbz" => BookMediaType.Comic,
                _ => null
            };
        }

        public static bool IsComicImageExtension(string? pathOrExtension)
        {
            if (string.IsNullOrWhiteSpace(pathOrExtension))
                return false;

            var ext = pathOrExtension.Contains('.') && !pathOrExtension.StartsWith('.')
                ? Path.GetExtension(pathOrExtension)
                : pathOrExtension;
            if (string.IsNullOrWhiteSpace(ext))
                return false;

            if (!ext.StartsWith('.'))
                ext = "." + ext;

            return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
        }
    }
}
