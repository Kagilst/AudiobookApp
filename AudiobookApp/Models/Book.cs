using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AudiobookApp.Models
{
    public enum BookMediaType
    {
        Audiobook,
        Epub
    }

    public class Book
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Title { get; set; } = "";

        public string Author { get; set; } = "";

        /// <summary>
        /// Primary file path (first audio file, or the EPUB). Kept for compatibility and covers.
        /// </summary>
        public string FilePath { get; set; } = "";

        /// <summary>
        /// Ordered audio parts for multi-file audiobooks. Empty means use <see cref="FilePath"/> alone.
        /// </summary>
        public List<string> FilePaths { get; set; } = new();

        /// <summary>
        /// Optional custom cover image path. When empty, cover art is taken from <see cref="FilePath"/>.
        /// </summary>
        public string CoverImagePath { get; set; } = "";

        public BookMediaType MediaType { get; set; } = BookMediaType.Audiobook;

        public List<string> Tags { get; set; } = new();

        /// <summary>
        /// User rating from 0 to 5. Null means the book has not been rated.
        /// </summary>
        public double? Rating { get; set; }

        public TimeSpan Duration { get; set; }

        public double LastPositionSeconds { get; set; }

        /// <summary>
        /// Last EPUB reading-order section index (0-based).
        /// </summary>
        public int LastChapterIndex { get; set; }

        /// <summary>
        /// Scroll position within the current EPUB chapter, as a 0–1 ratio of scrollable height.
        /// </summary>
        public double LastEpubScrollRatio { get; set; }

        /// <summary>
        /// User bookmarks for EPUB reading positions.
        /// </summary>
        public List<EpubBookmark> EpubBookmarks { get; set; } = new();

        public double PlaybackSpeed { get; set; } = 1.0;

        [JsonIgnore]
        public string EffectiveCoverPath =>
            !string.IsNullOrWhiteSpace(CoverImagePath) ? CoverImagePath : FilePath;

        [JsonIgnore]
        public IReadOnlyList<string> AudioFilePaths
        {
            get
            {
                if (FilePaths is { Count: > 0 })
                    return FilePaths;

                return string.IsNullOrWhiteSpace(FilePath)
                    ? Array.Empty<string>()
                    : new[] { FilePath };
            }
        }

        [JsonIgnore]
        public bool IsMultiFileAudiobook =>
            MediaType == BookMediaType.Audiobook && AudioFilePaths.Count > 1;

        [JsonIgnore]
        public double ProgressPercent
        {
            get
            {
                if (Duration.TotalSeconds <= 0 || LastPositionSeconds <= 0)
                    return 0;

                var percent = LastPositionSeconds / Duration.TotalSeconds * 100.0;
                return Math.Clamp(percent, 0, 100);
            }
        }

        [JsonIgnore]
        public bool IsCompleted =>
            Duration.TotalSeconds > 0 &&
            LastPositionSeconds >= Duration.TotalSeconds * 0.98;

        [JsonIgnore]
        public bool IsStarted => LastPositionSeconds > 1 && !IsCompleted;

        [JsonIgnore]
        public bool IsNotStarted => LastPositionSeconds <= 1;

        public void NormalizeFilePaths()
        {
            FilePaths ??= new List<string>();

            if (FilePaths.Count == 0 && !string.IsNullOrWhiteSpace(FilePath))
                FilePaths.Add(FilePath);

            FilePaths = FilePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (FilePaths.Count > 0)
                FilePath = FilePaths[0];
        }
    }

    public class EpubBookmark
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public int ChapterIndex { get; set; }

        public double ScrollRatio { get; set; }

        public string Label { get; set; } = "";

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    public enum ProgressFilter
    {
        None,
        Started,
        NotStarted,
        Completed
    }
}
