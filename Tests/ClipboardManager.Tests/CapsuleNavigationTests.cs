using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.ViewModels;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class CapsuleNavigationTests(SettingsOverlayWpfFixture wpf)
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [Fact]
    public Task ExternalRequestOpensHistoryWithoutResizingCapsule() => WithWindow((window, view) =>
    {
        window.ShowFromExternalRequest();
        DrainDispatcher();
        AssertHistoryOpen(window, view);
    });

    [Fact]
    public Task HotKeyTogglesHistoryWithoutResizingCapsule() => WithWindow((window, view) =>
    {
        Invoke(window, "OnHotKeyPressed", window, EventArgs.Empty);
        DrainDispatcher();
        AssertHistoryOpen(window, view);

        Invoke(window, "OnHotKeyPressed", window, EventArgs.Empty);
        DrainDispatcher();
        Assert.False(view.IsHistoryPopupOpen);
        Assert.Equal(new Size(240, 40), window.RenderSize);
    });

    [Fact]
    public Task HistoryArrowKeysAreNotInterceptedByWindowNavigation() => WithWindow((window, view) =>
    {
        window.ShowFromExternalRequest();
        DrainDispatcher();
        var list = (ListBox)view.FindName("HistoryRecords");
        list.ItemsSource = new[] { "first", "second" };
        view.FocusHistoryRecords();
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), Environment.TickCount, Key.Down)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };

        Invoke(window, "OnWindowPreviewKeyDown", window, args);

        Assert.False(args.Handled);
        Assert.True(view.IsHistoryPopupOpen);
        Assert.Equal(0, list.SelectedIndex);
    });

    [Fact]
    public Task TrayOpenRequestUsesHistoryPopup() => WithWindow((window, view) =>
    {
        var tray = Field<SystemTrayIcon>(window, "_trayIcon");
        var handler = (EventHandler)typeof(SystemTrayIcon).GetField("OpenRequested", PrivateInstance)!.GetValue(tray)!;
        handler(tray, EventArgs.Empty);
        DrainDispatcher();
        AssertHistoryOpen(window, view);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ClosingSettingsRestoresCapsuleAndPreviousHistoryState(bool historyWasOpen) => WithWindow((window, view) =>
    {
        if (historyWasOpen) window.ShowFromExternalRequest();
        DrainDispatcher();
        var host = Field<WindowOverlayHost>(window, "_overlayHost");
        var page = new SettingsOverlay(new AppSettings(), System.IO.Path.GetTempPath());
        _ = host.ShowAsync(page, OverlaySize.Settings);
        DrainDispatcher();
        Assert.False(view.IsHistoryPopupOpen);
        Assert.Equal(new Size(300, 400), window.RenderSize);

        ((SettingsView)page.Content!).CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        DrainDispatcher();
        Assert.False(host.IsOpen);
        Assert.Equal(new Size(240, 40), window.RenderSize);
        Assert.Equal(historyWasOpen, view.IsHistoryPopupOpen);
    });

    [Fact]
    public Task ExternalRequestWhileSettingsOpenKeepsSettingsVisible() => WithWindow((window, view) =>
    {
        var host = Field<WindowOverlayHost>(window, "_overlayHost");
        var page = new SettingsOverlay(new AppSettings(), System.IO.Path.GetTempPath());
        _ = host.ShowAsync(page, OverlaySize.Settings);
        DrainDispatcher();

        window.ShowFromExternalRequest();
        DrainDispatcher();

        Assert.True(host.IsOpen);
        Assert.Same(page, Field<WindowFrame>(window, "_windowFrame").OverlayContent.Content);
        Assert.False(view.IsHistoryPopupOpen);
        Assert.Equal(new Size(300, 400), window.RenderSize);
    });

    [Fact]
    public Task CapsuleIsTheOnlyMainSurface() => WithWindow((window, view) =>
    {
        Assert.True(((Grid)view.FindName("Capsule")).IsVisible);
        Assert.Null(view.FindName("Panel"));
        Assert.Null(view.FindName("SearchBox"));
        Assert.Null(view.FindName("HistoryList"));
    });

    [Fact]
    public void SettingsOnlyShowsOptionsForSupportedSurfaces() => wpf.Run(() =>
    {
        var page = new SettingsOverlay(new AppSettings(), System.IO.Path.GetTempPath());
        var view = (SettingsView)page.Content!;
        Assert.Null(view.FindName("AlwaysOnTopCheckBox"));
        Assert.Null(view.FindName("HideAfterCopyCheckBox"));
        Assert.NotNull(view.FindName("HideCapsulePreviewCheckBox"));
        Assert.NotNull(view.FindName("AutoCheckUpdatesCheckBox"));
    });

    [Fact]
    public async Task LegacyPanelSettingsDoNotPreventLoadingExistingHistory()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipboard-legacy-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HistoryStore(directory);
            var history = new HistoryService(store);
            await history.LoadAsync();
            await history.AddAsync(ClipboardSnapshot.FromText("existing history"));
            var settings = history.Settings;
            settings.MaxRecords = 600;
            settings.HideCapsulePreview = true;
            settings.WidgetLeft = 100;
            settings.WidgetTop = 80;
            await history.UpdateSettingsAsync(settings);
            var document = System.Text.Json.Nodes.JsonNode.Parse(await System.IO.File.ReadAllTextAsync(store.HistoryFilePath))!;
            document["settings"]!["alwaysOnTop"] = true;
            document["settings"]!["hideAfterCopy"] = false;
            await System.IO.File.WriteAllTextAsync(store.HistoryFilePath, document.ToJsonString());

            var reloaded = new HistoryService(new HistoryStore(directory));
            await reloaded.LoadAsync();

            Assert.Equal("existing history", Assert.Single(reloaded.Records).Text);
            Assert.Equal(600, reloaded.Settings.MaxRecords);
            Assert.True(reloaded.Settings.HideCapsulePreview);
            Assert.Equal(100, reloaded.Settings.WidgetLeft);
            Assert.Equal(80, reloaded.Settings.WidgetTop);
        }
        finally
        {
            Assert.Equal(System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar),
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(directory)), ignoreCase: true);
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
    }

    private async Task WithWindow(Action<MainWindow, MainView> action)
    {
        MainWindow? window = null;
        MainView? view = null;
        try
        {
            wpf.Run(() =>
            {
                window = new MainWindow(new HistoryService(new HistoryStore(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")))))
                    { ShowInTaskbar = false, Topmost = false, Opacity = 0 };
                // Navigation tests use real loaded visuals, but never start clipboard capture or updates.
                var loaded = typeof(MainWindow).GetMethod("OnLoaded", PrivateInstance)!;
                window.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), window);
                view = Field<MainView>(window, "_view");
                ((Popup)view.FindName("HistoryPopup")).Child.Opacity = 0;
                window.Show();
                window.UpdateLayout();
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
                    typeof(MainWindow).GetField("_allowClose", PrivateInstance)!.SetValue(window, true);
                    window.Close();
                });
            }
        }
    }

    private static void AssertHistoryOpen(MainWindow window, MainView view)
    {
        Assert.True(view.IsHistoryPopupOpen);
        Assert.Equal(new Size(240, 40), window.RenderSize);
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;

    private static void Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, args);

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
