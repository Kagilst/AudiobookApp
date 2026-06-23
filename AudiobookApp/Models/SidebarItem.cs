using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudiobookApp.Models
{
    public enum SidebarItemType
    {
        AllBooks,
        Category,
        AddButton
    }

    public class SidebarItem
    {
        public string Name { get; set; } = "";
        public SidebarItemType Type { get; set; }
        public LibraryCategory? Category { get; set; }
    }
}
