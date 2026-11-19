using System;
using System.Collections.Generic;

namespace AudiobookApp.Models
{
    /// <summary>
    /// A library row that is either a standalone book or a series tile.
    /// </summary>
    public class LibraryItem
    {
        public bool IsSeries { get; init; }

        public Book? Book { get; init; }

        public Series? Series { get; init; }

        /// <summary>
        /// For books: the single book. For series: ordered members that passed the current filter.
        /// </summary>
        public IReadOnlyList<Book> Members { get; init; } = Array.Empty<Book>();
    }
}
