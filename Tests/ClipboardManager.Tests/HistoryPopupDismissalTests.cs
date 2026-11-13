using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipboardManager.Services;
using ClipboardManager.ViewModels;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class HistoryPopupDismissalTests(SettingsOverlayWpfFixture wpf)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ApplicationDeactivationClosesHistoryWithoutAnotherOwnerDeactivatedEvent(bool openMenu) =>
        WithHistoryPopup((window, view) =>
        {
            Assert.False(window.IsActive);
            if (openMenu) OpenMoreMenu(view);

            SendApplicationActivation(window, false);
            DrainDispatcher();

            Assert.False(view.IsHistoryPopupOpen);
            Assert.False(view.IsContextMenuOpen);
        });

    [Fact]
    public Task ApplicationActivationKeepsHistoryOpen() => WithHistoryPopup((window, view) =>
    {
        SendApplicationActivation(window, true);
        DrainDispatcher();

        Assert.True(view.IsHistoryPopupOpen);
    });

    [Fact]
    public Task ReactivationCancelsQueuedApplicationDismissal() => WithHistoryPopup((window, view) =>
    {
        SendApplicationActivation(window, false);
        SendApplicationActivation(window, true);
        DrainDispatcher();

        Assert.True(view.IsHistoryPopupOpen);
    });

    [Fact]
    public Task ClosingMoreMenuInsideApplicationKeepsHistoryOpen() => WithHistoryPopup((window, view) =>
    {
        OpenMoreMenu(view);
        var button = Field<Button>(view, "MoreButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        DrainDispatcher();

        Assert.False(view.IsContextMenuOpen);
        Assert.True(view.IsHistoryPopupOpen);
    });

    [Fact]
    public Task DeferredOwnerDeactivationIsReplayedAfterRecordDragCompletes() => WithHistoryPopup((window, view) =>
    {
        Invoke(view, "BeginRecordDrag");
        Invoke(window, "OnDeactivated", window, EventArgs.Empty);
        DrainDispatcher();
        Assert.True(view.IsHistoryPopupOpen);

        Invoke(view, "CompleteRecordDrag");
        DrainDispatcher();

        Assert.False(view.IsHistoryPopupOpen);
    });

    [Fact]
    public Task ApplicationDeactivationWaitsUntilRecordDragCompletes() => WithHistoryPopup((window, view) =>
    {
        Invoke(view, "BeginRecordDrag");
        SendApplicationActivation(window, false);
        DrainDispatcher();
        Assert.True(view.IsHistoryPopupOpen);

        Invoke(view, "CompleteRecordDrag");
        DrainDispatcher();

        Assert.False(view.IsHistoryPopupOpen);
    });

    private async Task WithHistoryPopup(Action<MainWindow, MainView> action)
    {
        MainWindow? window = null;
        MainView? view = null;
        try
        {
            wpf.Run(() =>
            {
                // Exercise the real HWND and event wiring without starting clipboard capture,
                // registering a global hotkey, or loading the user's clipboard history.
                var history = new HistoryService(new HistoryStore(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
                window = new MainWindow(history) { ShowInTaskbar = false, Topmost = false, Opacity = 0 };

                view = Field<MainView>(window, "_view");
                var popup = Field<Popup>(view, "HistoryPopup");
                ((UIElement)popup.Child).Opacity = 0;
                Field<ContextMenu>(view, "_menu").Opacity = 0;
                new WindowInteropHelper(window).EnsureHandle();
                // Keep the owner unloaded; a detached popup can still create its own HWND.
                view.Children.Remove(popup);
                popup.PlacementTarget = null;
                popup.Placement = PlacementMode.Absolute;
                popup.HorizontalOffset = 0;
                popup.VerticalOffset = 0;
                popup.IsOpen = true;
                Assert.True(view.IsHistoryPopupOpen);
                action(window, view);
            });
        }
        finally
        {
            if (window is not null)
            {
                Task shutdown = Task.CompletedTask;
                wpf.Run(() =>
                {
                    view?.CloseHistoryPopup();
                    shutdown = ((MainViewModel)window.DataContext).ShutdownAsync();
                });
                await shutdown;
                wpf.Run(() =>
                {
                    typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
                        .SetValue(window, true);
                    window.Close();
                });
            }
        }
    }

    private static void OpenMoreMenu(MainView view)
    {
        var button = Field<Button>(view, "MoreButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        Assert.True(view.IsContextMenuOpen);
        Assert.True(view.IsHistoryPopupOpen);
    }

    private static void SendApplicationActivation(Window window, bool active) =>
        SendMessage(new WindowInteropHelper(window).Handle, 0x001C, active ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.GetValue(target)!;

    private static void Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(target, args);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
