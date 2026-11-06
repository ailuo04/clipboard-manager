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
using ClipboardManager.Updates;
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
        Assert.DoesNotContain("UpdateTokenBox", names);
        Assert.DoesNotContain("SaveUpdateTokenButton", names);
        Assert.DoesNotContain("DeleteUpdateTokenButton", names);
        Assert.Contains("CloseUpdateButton", names);
        Assert.Contains("InstallUpdateButton", names);

        wpf.Run(() =>
        {
            Grid view = ViewLoader.Load<Grid>("Views/UpdateView.xaml");
            Assert.Equal("应用更新", System.Windows.Automation.AutomationProperties.GetName(view));
            Assert.Null(view.FindName("UpdateTokenBox"));
        });
    }

    [Fact]
    public void CloseButtonCompletesWithNullAndCancelsPendingCheck()
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

            Click((Button)root.FindName("CloseUpdateButton"));

            Assert.True(callbackCalled);
            Assert.Null(result);
            Assert.True(observedToken.IsCancellationRequested);
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
            owner.Content = null;
        });
    }

    [Fact]
    public void PreparedCommitRunsBeforeTrueCloseResult()
    {
        wpf.Run(() =>
        {
            var events = new List<string>();
            var overlay = CreateOverlay(_ => Task.FromResult<AvailableUpdate?>(null),
                () => events.Add("commit"),
                () => events.Add("cancel"));
            overlay.CloseRequest = result => events.Add(result == true ? "close-true" : "close-other");

            overlay.CommitPreparedUpdate();

            Assert.Equal(new[] { "commit", "close-true" }, events);
        });
    }

    [Fact]
    public void FailedPreparedCommitCancelsWithoutCompletingTrue()
    {
        wpf.Run(() =>
        {
            var events = new List<string>();
            var overlay = CreateOverlay(_ => Task.FromResult<AvailableUpdate?>(null),
                () =>
                {
                    events.Add("commit");
                    throw new InvalidOperationException("commit failed");
                },
                () => events.Add("cancel"));
            bool? result = null;
            overlay.CloseRequest = value => result = value;

            Assert.Throws<InvalidOperationException>(() => overlay.CommitPreparedUpdate());

            Assert.Equal(new[] { "commit", "cancel" }, events);
            Assert.Null(result);
        });
    }

    [Fact]
    public async Task CancelAfterPrepareCompletesButBeforeContinuationPreventsCommit()
    {
        var prepareCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateOverlay? overlay = null;
        int commitCount = 0;
        int cancelCount = 0;
        bool? closeResult = null;
        CancellationToken downloadToken = default;

        wpf.Run(() =>
        {
            var update = new AvailableUpdate(new UpdateManifest { Version = "9.9.9" }, Array.Empty<byte>(),
                new UpdatePlan(), new Dictionary<string, UpdateAsset>());
            overlay = new UpdateOverlay(
                _ => Task.FromResult<AvailableUpdate?>(update),
                (_, _, token) =>
                {
                    downloadToken = token;
                    return prepareCompleted.Task;
                },
                () => commitCount++,
                () => cancelCount++,
                (_, _, _) => true);
            overlay.CloseRequest = result =>
            {
                closeResult = result;
                overlay.OnDismissed(result);
            };
            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Click((Button)((Grid)overlay.Content!).FindName("InstallUpdateButton"));

            Assert.False(overlay.WhenIdle.IsCompleted);
            prepareCompleted.SetResult();
            Assert.False(downloadToken.IsCancellationRequested);
            Click((Button)((Grid)overlay.Content!).FindName("CancelDownloadButton"));
        });

        await overlay!.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(downloadToken.IsCancellationRequested);
        Assert.Equal(0, commitCount);
        Assert.Equal(1, cancelCount);
        Assert.NotEqual(true, closeResult);
    }

    [Fact]
    public async Task DismissalCancelsDelayedCheckAndWhenIdleCompletes()
    {
        CancellationToken observedToken = default;
        UpdateOverlay? overlay = null;
        wpf.Run(() =>
        {
            overlay = CreateOverlay(async token =>
            {
                observedToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            });

            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.False(overlay.WhenIdle.IsCompleted);
            overlay.OnDismissed(null);
        });

        await overlay!.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observedToken.IsCancellationRequested);
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

    private static UpdateOverlay CreateOverlay(
        Func<CancellationToken, Task<AvailableUpdate?>> checkAsync,
        Action? commitPrepared = null,
        Action? cancelPrepared = null)
    {
        return new UpdateOverlay(checkAsync,
            (_, _, _) => Task.CompletedTask,
            commitPrepared ?? (() => { }),
            cancelPrepared ?? (() => { }));
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
