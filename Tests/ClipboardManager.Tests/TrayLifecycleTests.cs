using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipboardManager.Services;
using ClipboardManager.ViewModels;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class TrayLifecycleTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public async Task ExistingProfileStartsHiddenAndCanOpenPauseAndExitFromTray()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new HistoryStore(directory);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindow? window = null;
        try
        {
            await File.WriteAllTextAsync(store.HistoryFilePath,
                """{"version":1,"records":[],"settings":{"autoCheckUpdates":false,"minimizeToTray":false}}""");
            Task startup = Task.CompletedTask;
            wpf.Run(() =>
            {
                window = new MainWindow(new HistoryService(store)) { Opacity = 0, Topmost = false };
                window.Closed += (_, _) => closed.TrySetResult();
                Assert.False(window.ShowInTaskbar);
                startup = window.StartInTrayAsync();
            });
            await startup.WaitAsync(TimeSpan.FromSeconds(10));
            wpf.Run(() =>
            {
                Assert.NotNull(window);
                var model = (MainViewModel)window.DataContext;
                var monitor = Field<ClipboardMonitor>(model, "_monitor");
                var tray = Field<SystemTrayIcon>(window, "_trayIcon");
                var view = Field<MainView>(window, "_view");
                var handle = new WindowInteropHelper(window).Handle;

                Assert.True(model.IsReady);
                Assert.True(monitor.IsStarted);
                if (GetShellWindow() == IntPtr.Zero && !tray.IsEnabled)
                {
                    Assert.True(window.IsVisible);
                    window.ExitApplication();
                    return;
                }
                Assert.False(window.IsVisible);
                Assert.True(tray.IsEnabled);

                SendMessage(handle, TrayNativeMethods.TrayCallbackMessage, IntPtr.Zero, TrayNativeMethods.WmLeftButtonUp);
                Assert.True(window.IsVisible);
                Assert.True(view.IsHistoryPopupOpen);
                Assert.False(window.ShowInTaskbar);
                view.CloseHistoryPopup();
                DrainDispatcher();
                Assert.False(window.IsVisible);
                Assert.True(monitor.IsStarted);

                window.ShowFromExternalRequest();
                Assert.True(view.IsHistoryPopupOpen);
                window.Close();
                Assert.False(window.IsVisible);
                Assert.False(view.IsHistoryPopupOpen);
                Assert.True(monitor.IsStarted);

                SendMessage(handle, TrayNativeMethods.TrayCallbackMessage, IntPtr.Zero, TrayNativeMethods.WmRightButtonUp);
                var menu = Field<ContextMenu>(tray, "_menu");
                Assert.True(menu.IsOpen);
                Click((MenuItem)menu.Items[2]);
                Assert.True(model.IsPaused);
                Click((MenuItem)menu.Items[4]);
            });
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (window is not null && !closed.Task.IsCompleted)
            {
                wpf.Run(() => window.ExitApplication());
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            File.Delete(store.HistoryFilePath);
            if (Directory.Exists(store.ImagesDirectory)) Directory.Delete(store.ImagesDirectory);
            Directory.Delete(directory);
        }
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent, item));

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
}
