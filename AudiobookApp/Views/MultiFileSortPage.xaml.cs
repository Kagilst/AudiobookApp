using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudiobookApp.Views
{
    public sealed partial class MultiFileSortPage : Page
    {
        private readonly ObservableCollection<FileSortItem> _items = new();

        public event Action<IReadOnlyList<string>>? OrderConfirmed;
        public event Action? CancelRequested;

        public MultiFileSortPage(IEnumerable<string> paths)
        {
            InitializeComponent();

            foreach (var path in paths)
                _items.Add(new FileSortItem(path));

            RefreshIndexes();
            FilesList.ItemsSource = _items;
        }

        private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMoveButtons();
        }

        private void FilesList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            RefreshIndexes();
            UpdateMoveButtons();
        }

        private void UpdateMoveButtons()
        {
            var index = FilesList.SelectedIndex;
            MoveUpButton.IsEnabled = index > 0;
            MoveDownButton.IsEnabled = index >= 0 && index < _items.Count - 1;
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            var index = FilesList.SelectedIndex;
            if (index <= 0)
                return;

            _items.Move(index, index - 1);
            RefreshIndexes();
            FilesList.SelectedIndex = index - 1;
            UpdateMoveButtons();
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            var index = FilesList.SelectedIndex;
            if (index < 0 || index >= _items.Count - 1)
                return;

            _items.Move(index, index + 1);
            RefreshIndexes();
            FilesList.SelectedIndex = index + 1;
            UpdateMoveButtons();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            OrderConfirmed?.Invoke(_items.Select(i => i.Path).ToList());
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke();
        }

        private void RefreshIndexes()
        {
            for (int i = 0; i < _items.Count; i++)
                _items[i].IndexDisplay = $"{i + 1}.";
        }

        private sealed class FileSortItem : INotifyPropertyChanged
        {
            private string _indexDisplay = "";

            public FileSortItem(string path)
            {
                Path = path;
                FileName = System.IO.Path.GetFileName(path);
                Directory = System.IO.Path.GetDirectoryName(path) ?? "";
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string Path { get; }
            public string FileName { get; }
            public string Directory { get; }

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
