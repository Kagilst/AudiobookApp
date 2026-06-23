using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudiobookApp.Models
{
    public class Book
    {
        public string Title { get; set; } = "";

        public string Author { get; set; } = "";

        public string FilePath { get; set; } = "";

        public string CategoryName { get; set; } = "";

        public TimeSpan Duration { get; set; }

        public double LastPositionSeconds { get; set; }
    }
}
