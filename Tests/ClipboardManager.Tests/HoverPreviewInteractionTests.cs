using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class HoverPreviewInteractionTests(SettingsOverlayWpfFixture wpf)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointerCanCrossTheGapAndScrollInsideThePreview(bool fromHistory) => wpf.Run(() => WithView(view =>
    {
        Popup popup = Preview(view);
        Button target = view.LatestCopyButton;
        if (fromHistory)
        {
            view.ShowHistoryPopup(CapsulePopupPositioning.Calculate(SystemParameters.WorkArea,
                new Rect(100, 100, 240, 40), view.MeasureHistoryPopup(300)));
            view.HistoryPopupBorder.UpdateLayout();
            target = Descendants<Button>(view.HistoryRecords).First(button => button.Tag is not null);
        }
        Enter(target);
        Pump(360);
        Assert.True(popup.IsOpen);
        Leave(target);
        Pump(100);
        Assert.True(popup.IsOpen);
        var surface = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
        Enter(surface);
        Pump(360);
        Assert.True(popup.IsOpen);
        Assert.True(surface.IsHitTestVisible);
        surface.UpdateLayout();
        ScrollViewer scroll = Assert.Single(Descendants<ScrollViewer>(surface));
        Assert.True(scroll.ScrollableHeight > 0);
        Assert.NotNull(surface.InputHitTest(scroll.TranslatePoint(new Point(20, 20), surface)));
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.MouseWheelEvent });
        Pump(20);
        Assert.True(scroll.VerticalOffset > 0);
        Assert.True(popup.IsOpen);

        ScrollBar bar = Assert.Single(Descendants<ScrollBar>(scroll), item => item.Orientation == Orientation.Vertical);
        Thumb thumb = Assert.Single(Descendants<Thumb>(bar));
        double before = scroll.VerticalOffset;
        thumb.RaiseEvent(new DragDeltaEventArgs(0, 30) { RoutedEvent = Thumb.DragDeltaEvent });
        Pump(20);
        Assert.True(scroll.VerticalOffset > before);
        Assert.True(popup.IsOpen);
    }));

    [Fact]
    public void LeavingBothSurfacesClosesThePreviewAfterTheGracePeriod() => wpf.Run(() => WithView(view =>
    {
        Popup popup = Preview(view);
        Enter(view.LatestCopyButton);
        Pump(360);
        Leave(view.LatestCopyButton);
        Enter((UIElement)popup.Child);
        Leave((UIElement)popup.Child);
        Pump(100);
        Assert.True(popup.IsOpen);
        Pump(260);
        Assert.False(popup.IsOpen);
    }));

    [Fact]
    public void ReturningToTheRecordCancelsPendingDismissal() => wpf.Run(() => WithView(view =>
    {
        Popup popup = Preview(view);
        Enter(view.LatestCopyButton);
        Pump(360);
        Leave(view.LatestCopyButton);
        Pump(100);
        Enter(view.LatestCopyButton);
        Pump(360);
        Assert.True(popup.IsOpen);
    }));

    [Fact]
    public void ClosingHistoryCancelsVisibleAndPendingPreviews() => wpf.Run(() => WithView(view =>
    {
        Popup popup = Preview(view);
        Enter(view.LatestCopyButton);
        view.CloseHistoryPopup();
        Pump(360);
        Assert.False(popup.IsOpen);
        Enter(view.LatestCopyButton);
        Pump(360);
        Assert.True(popup.IsOpen);
        view.CloseHistoryPopup();
        Assert.False(popup.IsOpen);
    }));

    [Fact]
    public void EscapeInsidePreviewClosesBothPopups() => wpf.Run(() => WithView(view =>
    {
        Popup popup = Preview(view);
        Enter(view.LatestCopyButton);
        Pump(360);
        var surface = (UIElement)popup.Child;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(surface),
            Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        surface.RaiseEvent(args);
        Assert.True(args.Handled);
        Assert.False(popup.IsOpen);
        Assert.False(view.IsHistoryPopupOpen);
    }));

    [Fact]
    public void LeavingBeforeTheOpenDelayDoesNotShowAPreview() => wpf.Run(() => WithView(view =>
    {
        Popup popup = Preview(view);
        Enter(view.LatestCopyButton);
        Leave(view.LatestCopyButton);
        Pump(360);
        Assert.False(popup.IsOpen);
    }));

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void PreviewAndHistoryUseNarrowDraggableScrollbars(AppTheme theme) => wpf.Run(() =>
    {
        AppTheme original = ThemeManager.Preference;
        try
        {
            ThemeManager.Apply(theme);
            WithView(view =>
            {
                Popup popup = Preview(view);
                Enter(view.LatestCopyButton);
                Pump(360);
                ((FrameworkElement)popup.Child).UpdateLayout();
                AssertCompactScrollbar(Assert.Single(Descendants<ScrollBar>(popup.Child)));
                view.CloseHistoryPopup();
                view.ShowHistoryPopup(CapsulePopupPositioning.Calculate(SystemParameters.WorkArea,
                    new Rect(100, 100, 240, 40), view.MeasureHistoryPopup(300)));
                Pump(30);
                view.HistoryPopupBorder.UpdateLayout();
                ScrollBar historyBar = Assert.Single(Descendants<ScrollBar>(view.HistoryRecords),
                    item => item.Orientation == Orientation.Vertical && item.IsVisible);
                AssertCompactScrollbar(historyBar);

                string? directory = Environment.GetEnvironmentVariable("CLIPBOARD_WIDGET_QA_DIR");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Enter(view.LatestCopyButton);
                    Pump(360);
                    Capture((FrameworkElement)popup.Child, System.IO.Path.Combine(directory, $"preview-{theme}.png"));
                    Capture(view.HistoryPopupBorder, System.IO.Path.Combine(directory, $"scroll-history-{theme}.png"));
                }
            });
        }
        finally { ThemeManager.Apply(original); }
    });

    [Fact]
    public void HoveringOverTheCapsuleDoesNotActivateItButPressingThePreviewTextDoes() => wpf.Run(() => WithView(view =>
    {
        Window owner = Window.GetWindow(view)!;
        Popup popup = Preview(view);
        Enter(view.LatestCopyButton);
        Pump(360);
        Assert.True(popup.IsOpen);
        Assert.False(view.IsHistoryPopupOpen);
        Assert.False(owner.IsActive);

        var text = Assert.Single(Descendants<TextBox>(popup.Child));
        Assert.Equal("6\r\nJ-USB1", text.Text);
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent };
        text.RaiseEvent(press);

        Assert.True(owner.IsActive);
        Assert.False(press.Handled); // The same press must reach the TextBox's normal selection handler.
        Assert.True(popup.IsOpen);
    }, hoverText: "6\r\nJ-USB1"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyingAPreviewSelectionCopiesOnlyTheSelectedText(bool useContextMenu) => wpf.Run(() =>
    {
        int wholeRecordCopies = 0;
        WithView(view =>
        {
            Popup popup = Preview(view);
            Enter(view.LatestCopyButton);
            Pump(360);
            var text = Assert.Single(Descendants<TextBox>(popup.Child));
            Assert.True(text.IsReadOnly);
            text.Focus();
            text.Select(text.Text.IndexOf("可滚动", StringComparison.Ordinal), 3);
            string? copied = null;
            // Observe the real copy payload, then cancel before it reaches the OS clipboard.
            DataObject.AddCopyingHandler(text, (_, args) =>
            {
                copied = args.DataObject.GetData(DataFormats.UnicodeText) as string;
                args.CancelCommand();
            });
            if (useContextMenu)
            {
                ContextMenu menu = Assert.IsType<ContextMenu>(text.ContextMenu);
                menu.Opacity = 0;
                menu.PlacementTarget = text;
                menu.IsOpen = true;
                Pump(20);
                MenuItem copy = Assert.Single(menu.Items.OfType<MenuItem>());
                Assert.Same(ApplicationCommands.Copy, copy.Command);
                Assert.Same(text, copy.CommandTarget);
                Assert.True(copy.IsEnabled, $"Selection={text.SelectedText}; CanCopy={ApplicationCommands.Copy.CanExecute(null, text)}; Focus={text.IsKeyboardFocusWithin}");
                typeof(MenuItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!.Invoke(copy, null);
                Pump(20);
                menu.IsOpen = false;
            }
            else
            {
                Assert.True(ApplicationCommands.Copy.CanExecute(null, text));
                ApplicationCommands.Copy.Execute(null, text);
            }
            Assert.Equal("可滚动", copied);
            Assert.Equal(0, wholeRecordCopies);
            Assert.True(popup.IsOpen);
        }, () => wholeRecordCopies++);
    });

    [Fact]
    public void CopyMenuKeepsThePreviewAndSelectionAliveWhileThePointerIsOutside() => wpf.Run(() => WithView(view =>
    {
        Popup popup = Preview(view);
        Enter(view.LatestCopyButton);
        Pump(360);
        TextBox text = Assert.Single(Descendants<TextBox>(popup.Child));
        text.Select(text.Text.IndexOf("可滚动", StringComparison.Ordinal), 3);
        ContextMenu menu = Assert.IsType<ContextMenu>(text.ContextMenu);
        menu.Opacity = 0;
        menu.PlacementTarget = text;
        Leave(view.LatestCopyButton);
        Enter((UIElement)popup.Child);
        menu.IsOpen = true;
        Leave((UIElement)popup.Child);
        Pump(360);
        Assert.True(popup.IsOpen);
        Assert.True(view.IsContextMenuOpen);
        Assert.True(view.IsHoverPreviewPointerOver);
        Assert.Equal("可滚动", text.SelectedText);
        menu.IsOpen = false;
        Pump(600);
        Assert.False(popup.IsOpen, $"Menu={view.IsContextMenuOpen}; SurfaceMouse={((UIElement)popup.Child).IsMouseOver}; SourceMouse={view.LatestCopyButton.IsMouseOver}; Capture={((UIElement)popup.Child).IsMouseCaptureWithin}");
        Assert.False(view.IsContextMenuOpen);
    }));

    [Fact]
    public void ClosingThePreviewAlsoClosesItsCopyMenu() => wpf.Run(() => WithView(view =>
    {
        Popup popup = Preview(view);
        Enter(view.LatestCopyButton);
        Pump(360);
        TextBox text = Assert.Single(Descendants<TextBox>(popup.Child));
        ContextMenu menu = Assert.IsType<ContextMenu>(text.ContextMenu);
        menu.Opacity = 0;
        menu.PlacementTarget = text;
        menu.IsOpen = true;
        view.CloseHistoryPopup();
        Assert.False(popup.IsOpen);
        Assert.False(menu.IsOpen);
        Assert.False(view.IsContextMenuOpen);
    }));

    [Fact]
    public void ClickingWithinTheTextDoesNotMoveFocusToThePreviewSurface() => wpf.Run(() => WithView(view =>
    {
        Popup popup = Preview(view);
        Enter(view.LatestCopyButton);
        Pump(360);
        TextBox text = Assert.Single(Descendants<TextBox>(popup.Child));
        DependencyObject focusScope = FocusManager.GetFocusScope(text);
        FocusManager.SetFocusedElement(focusScope, text);
        text.Select(text.Text.IndexOf("可滚动", StringComparison.Ordinal), 3);
        text.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        text.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            { RoutedEvent = Mouse.MouseDownEvent });
        Assert.Same(text, FocusManager.GetFocusedElement(focusScope));
        Assert.Equal("可滚动", text.SelectedText);
    }));

    [Fact]
    public void EditClickClosesPreviewBeforeFurtherInputWithoutDroppingTheCommand() => wpf.Run(() => WithView(view =>
    {
        Popup popup = Preview(view);
        Enter(view.LatestCopyButton);
        Pump(360);
        bool executed = false;
        view.HoverPreviewSurface.ContentTemplateSelector = null;
        view.HoverPreviewSurface.ContentTemplate = (DataTemplate)view.FindResource("HoverPreviewImage");
        view.HoverPreviewSurface.Content = new
        {
            ImagePath = (string?)null, PreviewImageWidth = 200d, PreviewImageHeight = 100d,
            ContentInfo = "编辑切换测试", EditImageCommand = new RelayCommand(() => executed = true)
        };
        view.HoverPreviewSurface.UpdateLayout();
        Button edit = Assert.Single(Descendants<Button>(view.HoverPreviewSurface));
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.Invoke(edit, null);
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Assert.True(executed);
        Assert.False(popup.IsOpen);
    }));

    private static void AssertCompactScrollbar(ScrollBar bar)
    {
        Assert.InRange(bar.ActualWidth, 6, 8);
        Thumb thumb = Assert.Single(Descendants<Thumb>(bar));
        Assert.True(thumb.IsHitTestVisible);
        Border visibleThumb = Assert.Single(Descendants<Border>(thumb));
        Assert.InRange(visibleThumb.ActualWidth, 2, 4);
        Assert.True(visibleThumb.CornerRadius.TopLeft > 0);
    }

    private static Popup Preview(MainView view)
    {
        Popup popup = Assert.IsType<Popup>(view.FindName("HoverPreviewPopup"));
        Assert.True(popup.StaysOpen);
        popup.Child.Opacity = 0;
        return popup;
    }

    private static void WithView(Action<MainView> action, Action? copy = null, string? hoverText = null)
    {
        var record = new
        {
            HoverText = hoverText ?? string.Join("\n", Enumerable.Range(1, 70).Select(index => $"第 {index} 行：可滚动预览内容。")),
            ContentInfo = "预览测试", SingleLine = "可滚动预览内容", ShortTimeLabel = "12:00", PositionLabel = "1",
            CopyCommand = new RelayCommand(copy ?? (() => { }))
        };
        var view = new MainView { DataContext = new
        {
            HasRecords = true, LatestRecord = record, RecordCount = 20, PauseToolTip = "暂停监听",
            HistoryRecords = Enumerable.Repeat(record, 20).ToArray()
        } };
        var owner = new Window
        {
            Content = view, Width = 240, Height = 40, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Left + 40, Top = SystemParameters.WorkArea.Top + 40
        };
        try
        {
            owner.Show();
            owner.UpdateLayout();
            action(view);
        }
        finally
        {
            view.CloseHistoryPopup();
            owner.Close();
        }
    }

    private static void Enter(UIElement element) => element.RaiseEvent(
        new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
    private static void Leave(UIElement element) => element.RaiseEvent(
        new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    internal static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        double opacity = element.Opacity;
        element.Opacity = 1;
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(element), null, new Rect(element.RenderSize));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
        element.Opacity = opacity;
    }
}
