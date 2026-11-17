using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClipboardManager.Controls;

/// <summary>Scrolls overflowing capsule text, leaving short text and reduced-motion displays still.</summary>
public sealed class MarqueeText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarqueeText), new PropertyMetadata(string.Empty, OnTextChanged));

    private readonly TextBlock _text;
    private readonly TranslateTransform _offset = new();

    public MarqueeText()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        Focusable = false;
        _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, RenderTransform = _offset };
        // The horizontal panel measures the text at its natural width, inside a clipped viewport.
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(_text);
        Content = line;
        Loaded += (_, _) => Restart();
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, _) => Restart();
        _text.SizeChanged += (_, _) => Restart();
        IsVisibleChanged += (_, _) => Restart();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var marquee = (MarqueeText)target;
        marquee._text.Text = (string?)args.NewValue ?? string.Empty;
        marquee.Restart();
    }

    private void Restart()
    {
        Stop();
        if (!IsLoaded || !IsVisible || ActualWidth <= 0 || !SystemParameters.ClientAreaAnimation) return;
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double overflow = _text.DesiredSize.Width - ActualWidth;
        if (overflow <= 1) return;

        const double pauseSeconds = 1.2;
        double finishSeconds = pauseSeconds + overflow / 28;
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(pauseSeconds))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(finishSeconds))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(finishSeconds + pauseSeconds))));
        _offset.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void Stop()
    {
        _offset.BeginAnimation(TranslateTransform.XProperty, null);
        _offset.X = 0;
    }
}
