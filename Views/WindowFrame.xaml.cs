using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClipboardManager.Views;

public partial class WindowFrame : Grid
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(180));

    public WindowFrame() => InitializeComponent();

    public event EventHandler? BackRequested;

    public void SetPageHeader(string? title)
    {
        PageTitle.Text = title ?? "本地历史";
        BackButton.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void PlayOverlayEnter()
    {
        var fade = new DoubleAnimation(0, 1, FadeDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var rise = new DoubleAnimation(8, 0, FadeDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        OverlayPanel.BeginAnimation(OpacityProperty, fade);
        var transform = OverlayPanel.RenderTransform as TranslateTransform ?? new TranslateTransform();
        OverlayPanel.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
