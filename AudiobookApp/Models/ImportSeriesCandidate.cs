using System;
using System.Collections.Generic;
using System.Linq;

namespace AudiobookApp.Models
{
    public enum ImportSeriesGroupReason
    {
        SameFolder
    }

    public sealed class ImportSeriesCandidate
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = "";

        public List<ImportBookCandidate> Books { get; set; } = new();

        public ImportSeriesGroupReason Reason { get; set; }

        public bool IsSelected { get; set; } = true;

        public string ReasonLabel => Reason switch
        {
            ImportSeriesGroupReason.SameFolder => "Same folder",
            _ => "Suggested"
        };

        public string BookSummary =>
            Books.Count == 1 ? "1 book" : $"{Books.Count} books";
    }

    public static class DirectorySeriesGrouper
    {
        public static List<ImportSeriesCandidate> ProposeSeries(
            IEnumerable<ImportBookCandidate> books)
        {
            var list = books.Where(b => b.FilePaths.Count > 0).ToList();
            var series = new List<ImportSeriesCandidate>();

            foreach (var folderGroup in list.GroupBy(GetPrimaryFolder, StringComparer.OrdinalIgnoreCase))
            {
                var members = folderGroup.ToList();
                if (members.Count < 2)
                    continue;

                var name = PathGetFileName(folderGroup.Key);
                if (string.IsNullOrWhiteSpace(name))
                    name = members[0].Title;

                series.Add(new ImportSeriesCandidate
                {
                    Name = name,
                    Books = members
                        .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    Reason = ImportSeriesGroupReason.SameFolder
                });
            }

            return series
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string GetPrimaryFolder(ImportBookCandidate book)
        {
            var path = book.FilePaths.FirstOrDefault() ?? "";
            return System.IO.Path.GetDirectoryName(path) ?? "";
        }

        private static string PathGetFileName(string path) =>
            string.IsNullOrWhiteSpace(path) ? "" : System.IO.Path.GetFileName(path);
    }
}
