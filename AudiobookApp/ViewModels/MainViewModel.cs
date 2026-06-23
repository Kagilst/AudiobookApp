using AudiobookApp.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudiobookApp.ViewModels
{
    public class MainViewModel
    {
        public ObservableCollection<LibraryCategory> Categories { get; } = new();
        public ObservableCollection<Book> Books { get; } = new();
        public ObservableCollection<SidebarItem> SidebarItems { get; } = new();
    }
}
