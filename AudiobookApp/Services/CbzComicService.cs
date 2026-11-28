using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AudiobookApp.Services
{
    /// <summary>
    /// Reads page images from a .cbz (ZIP) comic archive.
    /// </summary>
    public sealed class CbzComicService : IDisposable
    {
        private ZipArchive? _archive;
        private List<string> _entryNames = new();

        public string FilePath { get; private set; } = string.Empty;

        public IReadOnlyList<string> PageNames => _entryNames;

        public int PageCount => _entryNames.Count;

        public void Open(string cbzPath)
        {
            Dispose();
            if (string.IsNullOrWhiteSpace(cbzPath) || !File.Exists(cbzPath))
                throw new FileNotFoundException("Comic file not found.", cbzPath);

            FilePath = cbzPath;
            var stream = File.OpenRead(cbzPath);
            _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            _entryNames = _archive.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Name)
                            && !e.FullName.EndsWith('/')
                            && !e.FullName.EndsWith('\\')
                            && MediaTypeHelper.IsComicImageExtension(e.Name)
                            && !IsMacJunk(e.FullName))
                .Select(e => e.FullName)
                .OrderBy(n => n, Comparer<string>.Create(NaturalPathCompare))
                .ToList();

            if (_entryNames.Count == 0)
                throw new InvalidOperationException("No image pages found in this CBZ file.");
        }

        public byte[] GetPageBytes(int index)
        {
            if (_archive == null)
                throw new InvalidOperationException("No comic is open.");
            if (index < 0 || index >= _entryNames.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            var entry = _archive.GetEntry(_entryNames[index])
                        ?? throw new InvalidOperationException("Missing comic page entry.");
            using var entryStream = entry.Open();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            return ms.ToArray();
        }

        public void Dispose()
        {
            _archive?.Dispose();
            _archive = null;
            _entryNames = new List<string>();
            FilePath = string.Empty;
        }

        public static int NaturalPathCompare(string? a, string? b)
            => CompareNatural(NormalizePath(a), NormalizePath(b));

        /// <summary>Public helper for unit tests — natural order of comic page file names.</summary>
        public static List<string> SortPageNames(IEnumerable<string> names)
            => names
                .Where(n => !string.IsNullOrWhiteSpace(n) && MediaTypeHelper.IsComicImageExtension(n))
                .OrderBy(n => n, Comparer<string>.Create(NaturalPathCompare))
                .ToList();

        private static string NormalizePath(string? path)
            => (path ?? string.Empty).Replace('\\', '/');

        private static bool IsMacJunk(string fullName)
        {
            var n = fullName.Replace('\\', '/');
            return n.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                   || Path.GetFileName(n).StartsWith("._", StringComparison.Ordinal);
        }

        private static int CompareNatural(string? a, string? b)
        {
            a ??= "";
            b ??= "";

            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    long numA = 0;
                    while (i < a.Length && char.IsDigit(a[i]))
                    {
                        numA = numA * 10 + (a[i] - '0');
                        i++;
                    }

                    long numB = 0;
                    while (j < b.Length && char.IsDigit(b[j]))
                    {
                        numB = numB * 10 + (b[j] - '0');
                        j++;
                    }

                    if (numA != numB)
                        return numA.CompareTo(numB);
                    continue;
                }

                var cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (cmp != 0)
                    return cmp;
                i++;
                j++;
            }

            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
