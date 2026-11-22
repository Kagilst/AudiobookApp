using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.Text;

namespace AudiobookApp.Views
{
    public sealed partial class MarqueeText : UserControl
    {
        private const double GapDip = 48;
        private const double PixelsPerSecond = 14;

        private Storyboard? _storyboard;
        private string _text = string.Empty;

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(MarqueeText),
                new PropertyMetadata(string.Empty, OnTextChanged));

        public static readonly DependencyProperty LineHeightProperty =
            DependencyProperty.Register(
                nameof(LineHeight),
                typeof(double),
                typeof(MarqueeText),
                new PropertyMetadata(16.0, OnLineHeightChanged));

        public static readonly DependencyProperty TextOpacityProperty =
            DependencyProperty.Register(
                nameof(TextOpacity),
                typeof(double),
                typeof(MarqueeText),
                new PropertyMetadata(1.0, OnTextOpacityChanged));

        public MarqueeText()
        {
            InitializeComponent();
            FontSize = 12;
            FontWeight = FontWeights.Normal;
            Loaded += (_, _) =>
            {
                SyncTextStyle();
                RestartAnimation();
            };
            Unloaded += (_, _) => StopAnimation();
            RegisterPropertyChangedCallback(FontSizeProperty, (_, _) =>
            {
                SyncTextStyle();
                RestartAnimation();
            });
            RegisterPropertyChangedCallback(FontWeightProperty, (_, _) =>
            {
                SyncTextStyle();
                RestartAnimation();
            });
            RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => SyncTextStyle());
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public double LineHeight
        {
            get => (double)GetValue(LineHeightProperty);
            set => SetValue(LineHeightProperty, value);
        }

        public double TextOpacity
        {
            get => (double)GetValue(TextOpacityProperty);
            set => SetValue(TextOpacityProperty, value);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MarqueeText marquee)
                marquee.ApplyText(e.NewValue as string ?? string.Empty);
        }

        private static void OnLineHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MarqueeText marquee && e.NewValue is double height)
            {
                marquee.Viewport.Height = height;
                marquee.UpdateClip();
            }
        }

        private static void OnTextOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MarqueeText marquee && e.NewValue is double opacity)
            {
                marquee.PrimaryText.Opacity = opacity;
                marquee.SecondaryText.Opacity = opacity;
            }
        }

        private void ApplyText(string value)
        {
            _text = value ?? string.Empty;
            PrimaryText.Text = _text;
            SecondaryText.Text = _text;
            SyncTextStyle();
            RestartAnimation();
        }

        private void SyncTextStyle()
        {
            PrimaryText.FontSize = FontSize;
            SecondaryText.FontSize = FontSize;
            PrimaryText.FontWeight = FontWeight;
            SecondaryText.FontWeight = FontWeight;
            PrimaryText.Opacity = TextOpacity;
            SecondaryText.Opacity = TextOpacity;
            if (Foreground != null)
            {
                PrimaryText.Foreground = Foreground;
                SecondaryText.Foreground = Foreground;
            }
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateClip();
            RestartAnimation();
        }

        private void Text_SizeChanged(object sender, SizeChangedEventArgs e) => RestartAnimation();

        private void UpdateClip()
        {
            ClipGeometry.Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, Viewport.Height));
        }

        private void RestartAnimation()
        {
            StopAnimation();
            UpdateClip();

            if (string.IsNullOrEmpty(_text) || ActualWidth <= 0)
            {
                PrimaryTranslate.X = 0;
                SecondaryText.Visibility = Visibility.Collapsed;
                return;
            }

            PrimaryText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var textWidth = PrimaryText.DesiredSize.Width;
            var viewport = ActualWidth;

            if (textWidth <= viewport + 1)
            {
                PrimaryTranslate.X = 0;
                SecondaryText.Visibility = Visibility.Collapsed;
                return;
            }

            SecondaryText.Visibility = Visibility.Visible;
            var loopWidth = textWidth + GapDip;
            PrimaryTranslate.X = 0;
            SecondaryTranslate.X = loopWidth;

            var duration = TimeSpan.FromSeconds(Math.Max(loopWidth / PixelsPerSecond, 2.5));
            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

            var primaryAnim = new DoubleAnimation
            {
                From = 0,
                To = -loopWidth,
                Duration = duration,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(primaryAnim, PrimaryTranslate);
            Storyboard.SetTargetProperty(primaryAnim, "X");
            storyboard.Children.Add(primaryAnim);

            var secondaryAnim = new DoubleAnimation
            {
                From = loopWidth,
                To = 0,
                Duration = duration,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(secondaryAnim, SecondaryTranslate);
            Storyboard.SetTargetProperty(secondaryAnim, "X");
            storyboard.Children.Add(secondaryAnim);

            _storyboard = storyboard;
            storyboard.Begin();
        }

        private void StopAnimation()
        {
            if (_storyboard == null)
                return;

            _storyboard.Stop();
            _storyboard.Children.Clear();
            _storyboard = null;
        }
    }
}
