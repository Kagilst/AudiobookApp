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
        private DispatcherTimer _timer = new();
        private bool _isDraggingSlider;

        //Constructor
        public PlayerPage(Book book)
        {
            InitializeComponent();

            _book = book;

            TitleText.Text = book.Title;
            AuthorText.Text = book.Author;

            Loaded += async (_, _) => await LoadCoverAsync();

            Unloaded += PlayerPage_Unloaded;

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void PlayerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();

            if (AudioPlayer.MediaPlayer != null)
            {
                AudioPlayer.MediaPlayer.Pause();
                AudioPlayer.MediaPlayer.Source = null;
            }

            AudioPlayer.Source = null;
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
                // Keeps placeholder visible
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

                AudioPlayer.MediaPlayer.PlaybackSession.PositionChanged +=
                    PlaybackSession_PositionChanged;
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

        //Slider
        private void PlaybackSession_PositionChanged(
            Windows.Media.Playback.MediaPlaybackSession sender,
            object args)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Actual Position: {sender.Position}");
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _timer.Stop();

            if (AudioPlayer.MediaPlayer != null)
            {
                AudioPlayer.MediaPlayer.Pause();
                AudioPlayer.MediaPlayer.Source = null;
            }

            AudioPlayer.Source = null;

            base.OnNavigatedFrom(e);
        }
        private void Timer_Tick(object? sender, object e)
        {
            if (AudioPlayer.MediaPlayer == null)
                return;

            var session = AudioPlayer.MediaPlayer.PlaybackSession;

            if (session.NaturalDuration <= TimeSpan.Zero)
                return;

            ProgressSlider.Maximum =
                session.NaturalDuration.TotalSeconds;

            // DON'T touch the slider while dragging
            if (!_isDraggingSlider)
            {
                ProgressSlider.Value =
                    session.Position.TotalSeconds;
            }

            CurrentTimeText.Text =
                (_isDraggingSlider
                    ? TimeSpan.FromSeconds(ProgressSlider.Value)
                    : session.Position)
                .ToString(@"hh\:mm\:ss");

            TotalTimeText.Text =
                session.NaturalDuration.ToString(@"hh\:mm\:ss");
        }

        private void ProgressSlider_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _isDraggingSlider = true;
        }
        private void ProgressSlider_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            SeekToSliderPosition();
        }

        private void SeekToSliderPosition()
        {
            if (AudioPlayer.MediaPlayer == null)
                return;

            AudioPlayer.MediaPlayer.PlaybackSession.Position =
                TimeSpan.FromSeconds(ProgressSlider.Value);

            _isDraggingSlider = false;
        }

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isDraggingSlider)
                return;

            CurrentTimeText.Text = TimeSpan.FromSeconds(e.NewValue).ToString(@"hh\:mm\:ss");
        }

    }


    // Tool Tip Converter 
    public class SecondsToTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            double seconds = value is double d ? d : 0;
            TimeSpan time = TimeSpan.FromSeconds(seconds);
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
