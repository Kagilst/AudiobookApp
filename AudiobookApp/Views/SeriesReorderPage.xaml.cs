using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AudiobookApp.Models;

namespace AudiobookApp.Views
{
    public sealed partial class SeriesReorderPage : Page
    {
        private readonly ObservableCollection<BookSortItem> _items = new();
        private readonly Series _series;

        public event Action<Series, IReadOnlyList<Guid>>? OrderConfirmed;
        public event Action? CancelRequested;

        public SeriesReorderPage(Series series, IEnumerable<Book> booksInOrder)
        {
            InitializeComponent();
            _series = series;
            TitleText.Text = string.IsNullOrWhiteSpace(series.Name)
                ? "Reorder series"
                : $"Reorder · {series.Name}";

            foreach (var book in booksInOrder)
                _items.Add(new BookSortItem(book));

            RefreshIndexes();
            BooksList.ItemsSource = _items;
        }

        public void SetContinueMode(bool isContinue)
        {
            SaveButton.Content = isContinue ? "Continue" : "Save order";
            SubtitleText.Text = isContinue
                ? "Arrange the books in series order. Drag to reorder, or use Move up / Move down, then continue to name the series."
                : "Books appear in this order in the series. Drag to reorder, or use Move up / Move down, then save.";
        }

        private void BooksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMoveButtons();
        }

        private void BooksList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            RefreshIndexes();
            UpdateMoveButtons();
        }

        private void UpdateMoveButtons()
        {
            var index = BooksList.SelectedIndex;
            MoveUpButton.IsEnabled = index > 0;
            MoveDownButton.IsEnabled = index >= 0 && index < _items.Count - 1;
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            var index = BooksList.SelectedIndex;
            if (index <= 0)
                return;

            _items.Move(index, index - 1);
            RefreshIndexes();
            BooksList.SelectedIndex = index - 1;
            UpdateMoveButtons();
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            var index = BooksList.SelectedIndex;
            if (index < 0 || index >= _items.Count - 1)
                return;

            _items.Move(index, index + 1);
            RefreshIndexes();
            BooksList.SelectedIndex = index + 1;
            UpdateMoveButtons();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            OrderConfirmed?.Invoke(_series, _items.Select(i => i.Book.Id).ToList());
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke();
        }

        private void RefreshIndexes()
        {
            for (var i = 0; i < _items.Count; i++)
                _items[i].IndexDisplay = $"{i + 1}.";
        }

        private sealed class BookSortItem : INotifyPropertyChanged
        {
            private string _indexDisplay = "";

            public BookSortItem(Book book)
            {
                Book = book;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public Book Book { get; }
            public string Title => Book.Title;
            public string Author => Book.Author;

            public string IndexDisplay
            {
                get => _indexDisplay;
                set
                {
                    _indexDisplay = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndexDisplay)));
                }
            }
        }
    }
}
