using System;
using System.Collections.Generic;

namespace AudiobookApp.Models
{
    public class Series
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = "";

        /// <summary>
        /// Ordered book membership (first id is the front cover).
        /// </summary>
        public List<Guid> BookIds { get; set; } = new();
    }
}
