using System;
using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Utilities;

namespace ClipboardManager.Views;

public partial class WindowFrame : Grid
{
    public WindowFrame() => InitializeComponent();

    /// <summary>Raised by the page header's back and close buttons; both leave the current page.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Titles the open page. The back arrow appears only when the page was opened from another page.</summary>
    public void SetPageHeader(string? title, bool canGoBack = false)
    {
        PageTitle.Text = title ?? "";
        BackButton.Visibility = canGoBack ? Visibility.Visible : Visibility.Collapsed;
    }

    public void PlayOverlayEnter() => TransitionAnimations.PlayEnter(OverlayPanel, verticalOffset: 8, milliseconds: 180);

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
