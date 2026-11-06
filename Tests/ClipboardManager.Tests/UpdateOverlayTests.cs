using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Xml.Linq;
using ClipboardManager;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class UpdateOverlayTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public void UpdateViewKeepsItsOwnScrollableBodyAndAccessibleControls()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views", "UpdateView.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace automation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement root = XDocument.Load(path).Root!;
        XElement scrollViewer = root.Elements(presentation + "ScrollViewer").Single();
        XElement footer = root.Elements(presentation + "StackPanel")
            .Single(element => (string?)element.Attribute("Grid.Row") == "2");
        string[] names = root.Descendants().Attributes(x + "Name").Select(attribute => attribute.Value).ToArray();

        Assert.Equal("应用更新", (string?)root.Attribute("AutomationProperties.Name"));
        Assert.Equal("1", (string?)scrollViewer.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)footer.Attribute("Grid.Row"));
        Assert.Contains("UpdateTokenBox", names);
        Assert.Contains("SaveUpdateTokenButton", names);
        Assert.Contains("DeleteUpdateTokenButton", names);
        Assert.Contains("CloseUpdateButton", names);
        Assert.Contains("InstallUpdateButton", names);

        wpf.Run(() =>
        {
            Grid view = ViewLoader.Load<Grid>("Views/UpdateView.xaml");
            Assert.Equal("应用更新", System.Windows.Automation.AutomationProperties.GetName(view));
            Assert.Equal("GitHub 只读访问令牌", System.Windows.Automation.AutomationProperties.GetName(
                (PasswordBox)view.FindName("UpdateTokenBox")));
        });
    }

    [Fact]
    public void CloseButtonCompletesWithNullCancelsPendingCheckAndClearsCredentialInput()
    {
        wpf.Run(() =>
        {
            CancellationToken observedToken = default;
            var pendingCheck = new TaskCompletionSource<AvailableUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var overlay = CreateOverlay(token =>
            {
                observedToken = token;
                token.Register(() => pendingCheck.TrySetCanceled(token));
                return pendingCheck.Task;
            });
            bool callbackCalled = false;
            bool? result = true;
            overlay.CloseRequest = value =>
            {
                callbackCalled = true;
                result = value;
                overlay.OnDismissed(value);
            };
            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Grid root = (Grid)overlay.Content!;
            var tokenBox = (PasswordBox)root.FindName("UpdateTokenBox");
            tokenBox.Password = "test-token";

            Click((Button)root.FindName("CloseUpdateButton"));

            Assert.True(callbackCalled);
            Assert.Null(result);
            Assert.True(observedToken.IsCancellationRequested);
            Assert.Equal(string.Empty, tokenBox.Password);
        });
    }

    [Fact]
    public void FirstLoadedAttachmentChecksOnceAcrossDetachAndReattach()
    {
        wpf.Run(() =>
        {
            int checks = 0;
            var overlay = CreateOverlay(_ =>
            {
                checks++;
                return Task.FromResult<AvailableUpdate?>(null);
            });

            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            Assert.Equal(1, checks);
            Assert.Equal("已是最新版本", ((TextBlock)((Grid)overlay.Content!).FindName("UpdateStatusText")).Text);
        });
    }

    [Fact]
    public void EscapeDismissalCancelsPendingCheckAndResolvesCurrentOverlay()
    {
        wpf.Run(() =>
        {
            CancellationToken observedToken = default;
            var pendingCheck = new TaskCompletionSource<AvailableUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var overlay = CreateOverlay(token =>
            {
                observedToken = token;
                token.Register(() => pendingCheck.TrySetCanceled(token));
                return pendingCheck.Task;
            });
            var owner = new Window { Width = 340, Height = 500 };
            Grid hostRoot = CreateOverlayRoot();
            owner.Content = hostRoot;
            using var host = new WindowOverlayHost(owner, hostRoot);
            Task<bool?> result = host.ShowAsync(overlay, OverlaySize.Update);
            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            using var keySource = new HwndSource(new HwndSourceParameters("UpdateEscapeTest")
            {
                Width = 1,
                Height = 1,
                WindowStyle = 0
            });
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            ((Grid)hostRoot.FindName("OverlayLayer")).RaiseEvent(escape);

            Assert.True(escape.Handled);
            Assert.Null(result.GetAwaiter().GetResult());
            Assert.True(observedToken.IsCancellationRequested);
            Assert.Equal(string.Empty, ((PasswordBox)((Grid)overlay.Content!).FindName("UpdateTokenBox")).Password);
            owner.Content = null;
        });
    }

    [Fact]
    public void SettingsUpdateCallbackOpensNestedOverlayAndPreservesUnsavedValues()
    {
        wpf.Run(() =>
        {
            var owner = new Window { Width = 340, Height = 500 };
            Grid hostRoot = CreateOverlayRoot();
            owner.Content = hostRoot;
            using var host = new WindowOverlayHost(owner, hostRoot);
            var child = new TestOverlay();
            Func<Task> showUpdates = () =>
            {
                host.ShowAsync(child, OverlaySize.Update);
                return Task.CompletedTask;
            };
            var settings = new SettingsOverlay(new AppSettings(), Path.GetTempPath(), showUpdates);
            Task<bool?> settingsResult = host.ShowAsync(settings, OverlaySize.Settings);
            Grid settingsRoot = (Grid)settings.Content!;
            var maxRecords = (TextBox)settingsRoot.FindName("MaxRecordsBox");
            maxRecords.Text = "875";

            Click((Button)settingsRoot.FindName("OpenUpdatesButton"));

            Assert.Same(child, ((ContentControl)hostRoot.FindName("OverlayContent")).Content);
            host.CompleteCurrent(null);
            Assert.Same(settings, ((ContentControl)hostRoot.FindName("OverlayContent")).Content);
            Assert.Equal("875", maxRecords.Text);
            host.CloseAll(null);
            Assert.Null(settingsResult.GetAwaiter().GetResult());
            owner.Content = null;
        });
    }

    private static UpdateOverlay CreateOverlay(Func<CancellationToken, Task<AvailableUpdate?>> checkAsync)
    {
        return new UpdateOverlay(checkAsync,
            (_, _, _) => Task.CompletedTask,
            () => { });
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static Grid CreateOverlayRoot()
    {
        var root = new Grid();
        NameScope.SetNameScope(root, new NameScope());
        var main = new Grid();
        var content = new ContentControl();
        var panel = new Border { Child = content };
        var layer = new Grid { Visibility = Visibility.Collapsed };
        layer.Children.Add(panel);
        root.Children.Add(main);
        root.Children.Add(layer);
        root.RegisterName("MainSurface", main);
        root.RegisterName("OverlayLayer", layer);
        root.RegisterName("OverlayPanel", panel);
        root.RegisterName("OverlayContent", content);
        return root;
    }

    private sealed class TestOverlay : OverlayView { }
}
