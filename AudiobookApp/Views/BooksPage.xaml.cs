using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using AudiobookApp.Models;
using WinRT.Interop;
using System.Threading.Tasks;

namespace AudiobookApp.Views
{
    public sealed partial class BooksPage : Page
    {
        public BooksPage()
        {
            InitializeComponent();
        }

        public event EventHandler? ImportRequested;

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            ImportRequested?.Invoke(this, EventArgs.Empty);
        }

        public event Action<Book>? RemoveRequested;

        private void RemoveBook_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Book book)
            {
                RemoveRequested?.Invoke(book);
            }
        }


        public void SetBooks(IEnumerable<Book> books)
        {
            BooksList.ItemsSource = books;
        }
    }
}
