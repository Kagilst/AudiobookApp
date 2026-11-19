using System;
using System.Collections.Generic;
using System.Linq;

namespace AudiobookApp.Models
{
    public enum ImportGroupReason
    {
        SingleFile,
        Folder,
        AlbumMetadata,
        FilenamePrefix
    }

    public sealed class ImportBookCandidate
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Title { get; set; } = "";

        public string Author { get; set; } = "";

        public BookMediaType MediaType { get; set; } = BookMediaType.Audiobook;

        public List<string> FilePaths { get; set; } = new();

        public ImportGroupReason Reason { get; set; } = ImportGroupReason.SingleFile;

        public bool IsSelected { get; set; } = true;

        public string ReasonLabel => Reason switch
        {
            ImportGroupReason.Folder => "Folder",
            ImportGroupReason.AlbumMetadata => "Album tags",
            ImportGroupReason.FilenamePrefix => "Filename",
            _ => "Single file"
        };

        public string FileSummary
        {
            get
            {
                var count = FilePaths.Count;
                if (MediaType == BookMediaType.Epub)
                    return count == 1 ? "1 epub" : $"{count} epubs";

                return count == 1 ? "1 audio file" : $"{count} audio files";
            }
        }

        public Book ToBook()
        {
            var paths = FilePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new Book
            {
                Id = Guid.NewGuid(),
                Title = string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title.Trim(),
                Author = string.IsNullOrWhiteSpace(Author) ? "Unknown Author" : Author.Trim(),
                MediaType = MediaType,
                FilePath = paths.FirstOrDefault() ?? "",
                FilePaths = paths
            };
        }
    }
}
