using System;
using System.Collections.Generic;
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
        public string Title { get; set; } = "";

        public string Author { get; set; } = "";

        public string FilePath { get; set; } = "";

        public BookMediaType MediaType { get; set; } = BookMediaType.Audiobook;

        public List<string> Tags { get; set; } = new();

        /// <summary>
        /// User rating from 0 to 5. Null means the book has not been rated.
        /// </summary>
        public double? Rating { get; set; }

        public TimeSpan Duration { get; set; }

        public double LastPositionSeconds { get; set; }

        public double PlaybackSpeed { get; set; } = 1.0;

        [JsonIgnore]
        public bool IsCompleted =>
            Duration.TotalSeconds > 0 &&
            LastPositionSeconds >= Duration.TotalSeconds * 0.98;

        [JsonIgnore]
        public bool IsStarted => LastPositionSeconds > 1 && !IsCompleted;

        [JsonIgnore]
        public bool IsNotStarted => LastPositionSeconds <= 1;
    }

    public enum ProgressFilter
    {
        None,
        Started,
        NotStarted,
        Completed
    }
}
