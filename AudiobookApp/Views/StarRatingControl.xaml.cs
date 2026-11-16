using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AudiobookApp.Views
{
    public sealed partial class StarRatingControl : UserControl
    {
        private const int StarCount = 5;
        private const string EmptyStarGlyph = "\uE734";
        private const string FilledStarGlyph = "\uE735";

        private readonly FontIcon[] _emptyStars = new FontIcon[StarCount];
        private readonly FontIcon[] _filledStars = new FontIcon[StarCount];
        private readonly RectangleGeometry[] _clips = new RectangleGeometry[StarCount];
        private readonly Grid[] _starHosts = new Grid[StarCount];

        private INotifyPropertyChanged? _dataContextInpc;

        public static readonly DependencyProperty RatingProperty =
            DependencyProperty.Register(
                nameof(Rating),
                typeof(double),
                typeof(StarRatingControl),
                new PropertyMetadata(double.NaN, OnRatingChanged));

        public static readonly DependencyProperty StarSizeProperty =
            DependencyProperty.Register(
                nameof(StarSize),
                typeof(double),
                typeof(StarRatingControl),
                new PropertyMetadata(14.0, OnStarSizeChanged));

        public StarRatingControl()
        {
            InitializeComponent();
            BuildStars();
            DataContextChanged += StarRatingControl_DataContextChanged;
            Loaded += (_, _) =>
            {
                PullRatingFromDataContext();
                UpdateStars();
            };
            UpdateStars();
        }

        /// <summary>
        /// Rating from 0-5. Use <see cref="double.NaN"/> when the book is not rated.
        /// </summary>
        public double Rating
        {
            get => (double)GetValue(RatingProperty);
            set => SetValue(RatingProperty, value);
        }

        public double StarSize
        {
            get => (double)GetValue(StarSizeProperty);
            set => SetValue(StarSizeProperty, value);
        }

        public void SetRating(double? rating)
        {
            Rating = rating ?? double.NaN;
        }

        private static void OnRatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StarRatingControl control)
                control.UpdateStars();
        }

        private static void OnStarSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StarRatingControl control)
                control.ApplyStarSize();
        }

        private void StarRatingControl_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (_dataContextInpc != null)
                _dataContextInpc.PropertyChanged -= DataContext_PropertyChanged;

            _dataContextInpc = args.NewValue as INotifyPropertyChanged;

            if (_dataContextInpc != null)
                _dataContextInpc.PropertyChanged += DataContext_PropertyChanged;

            PullRatingFromDataContext();
        }

        private void DataContext_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName is "Rating" or "RatingValue")
            {
                PullRatingFromDataContext();
            }
        }

        private void PullRatingFromDataContext()
        {
            if (DataContext == null)
                return;

            var type = DataContext.GetType();

            var ratingValueProp = type.GetProperty("RatingValue");
            if (ratingValueProp?.PropertyType == typeof(double))
            {
                Rating = (double)(ratingValueProp.GetValue(DataContext) ?? double.NaN);
                return;
            }

            var ratingProp = type.GetProperty("Rating");
            if (ratingProp == null)
                return;

            var value = ratingProp.GetValue(DataContext);
            if (value is double d)
                Rating = d;
            else if (value == null)
                Rating = double.NaN;
        }

        private void BuildStars()
        {
            StarsPanel.Children.Clear();

            var emptyBrush = new SolidColorBrush(Color.FromArgb(255, 180, 180, 180));
            var filledBrush = new SolidColorBrush(Color.FromArgb(255, 255, 191, 0));

            for (int i = 0; i < StarCount; i++)
            {
                var host = new Grid
                {
                    Width = StarSize,
                    Height = StarSize
                };

                var empty = new FontIcon
                {
                    Glyph = EmptyStarGlyph,
                    FontSize = StarSize,
                    Foreground = emptyBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var clip = new RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, 0, 0, StarSize)
                };

                var filled = new FontIcon
                {
                    Glyph = FilledStarGlyph,
                    FontSize = StarSize,
                    Foreground = filledBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Clip = clip
                };

                host.Children.Add(empty);
                host.Children.Add(filled);

                _starHosts[i] = host;
                _emptyStars[i] = empty;
                _filledStars[i] = filled;
                _clips[i] = clip;

                StarsPanel.Children.Add(host);
            }
        }

        private void ApplyStarSize()
        {
            for (int i = 0; i < StarCount; i++)
            {
                _starHosts[i].Width = StarSize;
                _starHosts[i].Height = StarSize;
                _emptyStars[i].FontSize = StarSize;
                _filledStars[i].FontSize = StarSize;
            }

            UpdateStars();
        }

        private void UpdateStars()
        {
            var rating = Rating;

            for (int i = 0; i < StarCount; i++)
            {
                double fill = 0;

                if (!double.IsNaN(rating))
                {
                    var value = Math.Clamp(rating, 0, StarCount);
                    fill = Math.Clamp(value - i, 0, 1);
                }

                _clips[i].Rect = new Windows.Foundation.Rect(0, 0, StarSize * fill, StarSize);
            }
        }
    }
}
