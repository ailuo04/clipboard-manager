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

    /// <summary>Raised by the page header's back and close buttons; both leave the current page.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Titles the open page. The back arrow appears only when the page was opened from another page.</summary>
    public void SetPageHeader(string? title, bool canGoBack = false)
    {
        PageTitle.Text = title ?? "";
        BackButton.Visibility = canGoBack ? Visibility.Visible : Visibility.Collapsed;
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
