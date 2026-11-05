using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager;

/// <summary>Provides the shared owner-centered glass chrome for modal auxiliary pages.</summary>
public abstract class GlassDialogWindow : Window
{
    private readonly Grid _frame;

    protected GlassDialogWindow(Window owner, string title, double width, double height, double minWidth, double minHeight)
    {
        ArgumentNullException.ThrowIfNull(owner);
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = minWidth;
        MinHeight = minHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Brushes.Transparent;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            UseAeroCaptionButtons = false,
            CaptionHeight = 42,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(18)
        });
        _frame = ViewLoader.Load<Grid>("Views/DialogFrame.xaml");
        Content = _frame;
        ((Button)_frame.FindName("DialogCloseButton")).Click += (_, _) => Close();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close();
        };
        StateChanged += (_, _) => UpdateDialogFrame();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateDialogFrame));
        ThemeManager.RegisterWindow(this);
        SourceInitialized += (_, _) => ThemeManager.ApplyBackdrop(this);
    }

    private void UpdateDialogFrame()
    {
        if (WindowState == WindowState.Minimized) return;
        _frame.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : new Thickness();
    }

    protected void SetDialogContent(UIElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ((ContentControl)_frame.FindName("DialogContent")).Content = content;
    }
}
