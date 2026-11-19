using AudiobookApp.Models;
using System.Collections.ObjectModel;

namespace AudiobookApp.ViewModels
{
    public class MainViewModel
    {
        public ObservableCollection<Book> Books { get; } = new();

        public ObservableCollection<Series> Series { get; } = new();
    }
}
