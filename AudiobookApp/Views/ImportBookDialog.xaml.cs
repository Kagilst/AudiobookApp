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

namespace AudiobookApp.Views
{
    public sealed partial class ImportBookDialog : Page
    {
        public Book Book { get; set; }

        public event Action<Book>? ImportConfirmed;

        public ImportBookDialog(Book book, IEnumerable<LibraryCategory> categories)
        {
            this.InitializeComponent();

            Book = book;

            TitleText.Text = book.Title;

            CategoryCombo.ItemsSource = categories;
            CategoryCombo.DisplayMemberPath = "Name";
        }

        private void Import_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var book = Book;

            if (CategoryCombo.SelectedItem is LibraryCategory category)
            {
                book.CategoryName = category.Name;
            }
            else
            {
                book.CategoryName = "All Books";
            }

            ImportConfirmed?.Invoke(book);
        }
    }
}

