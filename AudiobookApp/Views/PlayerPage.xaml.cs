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
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.FileProperties;
using Windows.Storage;
using System.Threading.Tasks;
using Windows.Media.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AudiobookApp.Views
{
    public sealed partial class PlayerPage : Page
    {
        private readonly Book _book;
        private bool _isPlaying;
        private bool _sourceLoaded;

        //Constructor
        public PlayerPage(Book book)
        {
            InitializeComponent();

            _book = book;

            TitleText.Text = book.Title;
            AuthorText.Text = book.Author;

            Loaded += async (_, _) => await LoadCoverAsync();
        }

        //Load Cover Art
        private async Task LoadCoverAsync()
        {
            if (string.IsNullOrWhiteSpace(_book.FilePath))
                return;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(_book.FilePath);

                var thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.SingleItem,
                    300);

                if (thumbnail != null)
                {
                    var bitmap = new BitmapImage();

                    await bitmap.SetSourceAsync(thumbnail);

                    CoverImage.Source = bitmap;

                    CoverPlaceholder.Visibility =
                        Visibility.Collapsed;
                }
            }
            catch
            {
                // Keep placeholder visible
            }
        }

        //Play Pause Button

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_sourceLoaded)
            {
                AudioPlayer.Source =
                    MediaSource.CreateFromUri(
                        new Uri(_book.FilePath));

                _sourceLoaded = true;
            }

            if (!_isPlaying)
            {
                AudioPlayer.MediaPlayer.Play();

                PlayPauseIcon.Symbol = Symbol.Pause;

                _isPlaying = true;
            }
            else
            {
                AudioPlayer.MediaPlayer.Pause();

                PlayPauseIcon.Symbol = Symbol.Play;

                _isPlaying = false;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            AudioPlayer.MediaPlayer?.Pause();

            base.OnNavigatedFrom(e);
        }

    }
}
