using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AudiobookApp.Models;
using ATL;

namespace AudiobookApp.Services
{
    public static class DirectoryBookImporter
    {
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".m4b", ".mp4"
        };

        private static readonly HashSet<string> EpubExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".epub"
        };

        private static readonly HashSet<string> ComicExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cbz"
        };

        private static readonly Regex TrailingPartRegex = new(
            @"[\s._\-]*(?:cd|disc|disk|part|chapter|ch|track|pt)?[\s._\-]*\(?\d{1,3}\)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<ImportBookCandidate> ScanDirectory(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return new List<ImportBookCandidate>();

            var mediaFiles = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
                .Where(IsMediaFile)
                .ToList();

            var candidates = new List<ImportBookCandidate>();

            foreach (var folderGroup in mediaFiles.GroupBy(f => Path.GetDirectoryName(f) ?? rootPath))
            {
                var folder = folderGroup.Key;
                var files = folderGroup.ToList();
                candidates.AddRange(BuildCandidatesForFolder(folder, files));
            }

            return candidates
                .OrderBy(c => c.Title, Comparer<string>.Create(CompareNatural))
                .ToList();
        }

        private static IEnumerable<ImportBookCandidate> BuildCandidatesForFolder(
            string folder,
            List<string> files)
        {
            var epubs = files.Where(f => EpubExtensions.Contains(Path.GetExtension(f))).ToList();
            var comics = files.Where(f => ComicExtensions.Contains(Path.GetExtension(f))).ToList();
            var audio = files.Where(f => AudioExtensions.Contains(Path.GetExtension(f))).ToList();

            foreach (var epub in epubs.OrderBy(f => Path.GetFileName(f), Comparer<string>.Create(CompareNatural)))
            {
                yield return new ImportBookCandidate
                {
                    Title = Path.GetFileNameWithoutExtension(epub),
                    Author = "Unknown Author",
                    MediaType = BookMediaType.Epub,
                    FilePaths = new List<string> { epub },
                    Reason = ImportGroupReason.SingleFile
                };
            }

            foreach (var comic in comics.OrderBy(f => Path.GetFileName(f), Comparer<string>.Create(CompareNatural)))
            {
                yield return new ImportBookCandidate
                {
                    Title = Path.GetFileNameWithoutExtension(comic),
                    Author = "Unknown Author",
                    MediaType = BookMediaType.Comic,
                    FilePaths = new List<string> { comic },
                    Reason = ImportGroupReason.SingleFile
                };
            }

            if (audio.Count == 0)
                yield break;

            if (audio.Count == 1)
            {
                yield return CreateAudioCandidate(audio, ImportGroupReason.SingleFile);
                yield break;
            }

            var albumGroups = GroupByAlbum(audio);
            if (albumGroups.Count > 1)
            {
                foreach (var group in albumGroups)
                    yield return CreateAudioCandidate(group, ImportGroupReason.AlbumMetadata);
                yield break;
            }

            if (albumGroups.Count == 1 && albumGroups[0].Count == audio.Count)
            {
                var albumName = TryGetAlbumName(albumGroups[0][0]);
                if (!string.IsNullOrWhiteSpace(albumName))
                {
                    yield return CreateAudioCandidate(albumGroups[0], ImportGroupReason.AlbumMetadata);
                    yield break;
                }
            }

            var prefixGroups = GroupByFilenamePrefix(audio);
            if (prefixGroups.Count > 1)
            {
                foreach (var group in prefixGroups)
                {
                    var reason = group.Count > 1
                        ? ImportGroupReason.FilenamePrefix
                        : ImportGroupReason.SingleFile;
                    yield return CreateAudioCandidate(group, reason);
                }
                yield break;
            }

            yield return CreateAudioCandidate(audio, ImportGroupReason.Folder);
        }

        private static List<List<string>> GroupByAlbum(List<string> audioFiles)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in audioFiles)
            {
                var album = TryGetAlbumName(path);
                var artist = TryGetAlbumArtist(path);
                if (string.IsNullOrWhiteSpace(album))
                    continue;

                var key = $"{album.Trim()}||{(artist ?? "").Trim()}";
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    map[key] = list;
                }

                list.Add(path);
            }

            // Only use album grouping when it accounts for most files and forms real groups.
            var groupedPaths = map.Values.SelectMany(v => v).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (groupedPaths.Count < audioFiles.Count * 0.6)
                return new List<List<string>>();

            var result = map.Values
                .Select(g => g.OrderBy(f => Path.GetFileName(f), Comparer<string>.Create(CompareNatural)).ToList())
                .ToList();

            var leftovers = audioFiles
                .Where(f => !groupedPaths.Contains(f))
                .OrderBy(f => Path.GetFileName(f), Comparer<string>.Create(CompareNatural))
                .ToList();

            foreach (var leftover in leftovers)
                result.Add(new List<string> { leftover });

            return result;
        }

        private static List<List<string>> GroupByFilenamePrefix(List<string> audioFiles)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in audioFiles)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var prefix = NormalizePrefix(name);
                if (prefix.Length < 4)
                    prefix = name;

                if (!map.TryGetValue(prefix, out var list))
                {
                    list = new List<string>();
                    map[prefix] = list;
                }

                list.Add(path);
            }

            // If everything collapses to one prefix, caller will use folder grouping.
            if (map.Count <= 1)
                return new List<List<string>>();

            // Require at least one multi-file group to accept prefix clustering.
            if (!map.Values.Any(v => v.Count >= 2))
                return new List<List<string>>();

            return map.Values
                .Select(g => g.OrderBy(f => Path.GetFileName(f), Comparer<string>.Create(CompareNatural)).ToList())
                .OrderBy(g => Path.GetFileNameWithoutExtension(g[0]), Comparer<string>.Create(CompareNatural))
                .ToList();
        }

        private static string NormalizePrefix(string fileNameWithoutExtension)
        {
            var trimmed = fileNameWithoutExtension.Trim();
            var previous = "";
            while (!string.Equals(previous, trimmed, StringComparison.Ordinal))
            {
                previous = trimmed;
                trimmed = TrailingPartRegex.Replace(trimmed, "").Trim();
                trimmed = trimmed.TrimEnd(' ', '.', '_', '-', ':');
            }

            return trimmed;
        }

        private static ImportBookCandidate CreateAudioCandidate(
            List<string> paths,
            ImportGroupReason reason)
        {
            var ordered = paths
                .OrderBy(f => Path.GetFileName(f), Comparer<string>.Create(CompareNatural))
                .ToList();

            var first = ordered[0];
            var title = TryGetAlbumName(first);
            if (string.IsNullOrWhiteSpace(title) && ordered.Count > 1)
                title = NormalizePrefix(Path.GetFileNameWithoutExtension(first));
            if (string.IsNullOrWhiteSpace(title))
                title = Path.GetFileNameWithoutExtension(first);

            if (reason == ImportGroupReason.Folder)
            {
                var folderName = Path.GetFileName(Path.GetDirectoryName(first) ?? "");
                if (!string.IsNullOrWhiteSpace(folderName))
                    title = folderName;
            }

            var author = TryGetAlbumArtist(first);
            if (string.IsNullOrWhiteSpace(author))
                author = TryGetArtist(first);
            if (string.IsNullOrWhiteSpace(author))
                author = "Unknown Author";

            return new ImportBookCandidate
            {
                Title = title!,
                Author = author!,
                MediaType = BookMediaType.Audiobook,
                FilePaths = ordered,
                Reason = ordered.Count == 1 ? ImportGroupReason.SingleFile : reason
            };
        }

        private static string? TryGetAlbumName(string path)
        {
            try
            {
                var track = new Track(path);
                return string.IsNullOrWhiteSpace(track.Album) ? null : track.Album.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string? TryGetAlbumArtist(string path)
        {
            try
            {
                var track = new Track(path);
                if (!string.IsNullOrWhiteSpace(track.AlbumArtist))
                    return track.AlbumArtist.Trim();
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string? TryGetArtist(string path)
        {
            try
            {
                var track = new Track(path);
                return string.IsNullOrWhiteSpace(track.Artist) ? null : track.Artist.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsMediaFile(string path)
        {
            var ext = Path.GetExtension(path);
            return AudioExtensions.Contains(ext)
                   || EpubExtensions.Contains(ext)
                   || ComicExtensions.Contains(ext);
        }

        public static int CompareNatural(string? a, string? b)
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
                }
                else
                {
                    var cmp = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (cmp != 0)
                        return cmp;
                    i++;
                    j++;
                }
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
