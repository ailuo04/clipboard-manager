using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ClipboardManager;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using ClipboardManager.ViewModels;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class DetailsOverlayTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public void DetailsViewKeepsSeparateScrollRegionsAndFixedFooterWithoutHostScrollWrapping()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views", "DetailsView.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement root = XDocument.Load(path).Root!;
        string[] names = root.Descendants().Attributes(x + "Name").Select(attribute => attribute.Value).ToArray();
        XElement text = root.Descendants(presentation + "TextBox").Single();
        XElement imageScroll = root.Descendants(presentation + "ScrollViewer")
            .Single(element => (string?)element.Attribute(x + "Name") == "ImageScroll");
        XElement footer = root.Elements(presentation + "Grid")
            .Single(element => (string?)element.Attribute("Grid.Row") == "2");

        Assert.Empty(root.Elements(presentation + "ScrollViewer"));
        XElement[] rows = root.Element(presentation + "Grid.RowDefinitions")!.Elements(presentation + "RowDefinition").ToArray();
        Assert.Equal("Auto", (string?)rows[0].Attribute("Height"));
        Assert.Equal("*", (string?)rows[1].Attribute("Height"));
        Assert.Equal("Auto", (string?)rows[2].Attribute("Height"));
        Assert.Equal("2", (string?)footer.Attribute("Grid.Row"));
        Assert.Equal("Auto", (string?)text.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)imageScroll.Attribute("VerticalScrollBarVisibility"));
        Assert.Contains("完整文本内容", (string?)text.Attribute(presentation + "AutomationProperties.Name"));
        Assert.Contains(new[] { "CopyButton", "FullImage", "ImageScroll", "ImageStatus", "FitCheckBox", "ZoomSlider", "ZoomLabel", "StatusLabel", "CloseButton" }, names);
        Assert.Equal("图片缩放比例", (string?)root.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ZoomSlider")
            .Attribute(presentation + "AutomationProperties.Name"));

        wpf.Run(() =>
        {
            Grid view = ViewLoader.Load<Grid>("Views/DetailsView.xaml");
            Assert.Equal("完整文本内容", System.Windows.Automation.AutomationProperties.GetName((TextBox)view.Children
                .OfType<TextBox>().Single()));
        });
    }

    [Fact]
    public void CloseButtonCompletesWithNullCancelsImageLoadAndClearsSource()
    {
        WithItem(image: true, (owner, item) =>
        {
            CancellationToken observedToken = default;
            var pendingImage = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            DetailsOverlay overlay = CreateOverlay(item, (path, token) =>
            {
                observedToken = token;
                token.Register(() => pendingImage.TrySetCanceled(token));
                return pendingImage.Task;
            });
            bool? result = true;
            overlay.CloseRequest = value =>
            {
                result = value;
                overlay.OnDismissed(value);
            };
            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var image = (Image)((Grid)overlay.Content!).FindName("FullImage");
            image.Source = CreateBitmap();

            Click((Button)((Grid)overlay.Content!).FindName("CloseButton"));

            Assert.Null(result);
            Assert.True(observedToken.IsCancellationRequested);
            Assert.Null(image.Source);
        });
    }

    [Fact]
    public void ImageIsLoadedOnlyOnceWhenOverlayIsDetachedAndRestored()
    {
        WithItem(image: true, (owner, item) =>
        {
            int loadCount = 0;
            DetailsOverlay overlay = CreateOverlay(item, (_, _) =>
            {
                loadCount++;
                return Task.FromResult<BitmapSource>(CreateBitmap());
            });
            var owner = new Window { Width = 340, Height = 500 };
            Grid hostRoot = CreateOverlayRoot();
            owner.Content = hostRoot;
            using var host = new WindowOverlayHost(owner, hostRoot);

            Task<bool?> detailsTask = host.ShowAsync(overlay, OverlaySize.Details);
            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Task<bool?> childTask = host.ShowAsync(new TestOverlay(), OverlaySize.Update);
            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            host.CompleteCurrent(null);
            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            Assert.Equal(1, loadCount);
            Assert.Same(overlay, ((ContentControl)hostRoot.FindName("OverlayContent")).Content);
            Assert.False(detailsTask.IsCompleted);
            Assert.True(childTask.IsCompleted);
            host.CompleteCurrent(null);
            Assert.Null(detailsTask.GetAwaiter().GetResult());
            owner.Content = null;
        });
    }

    [Fact]
    public void CopyTextSuccessUpdatesStatusAndRestoresButton()
    {
        WithItem(image: false, (owner, item) =>
        {
            string? copied = null;
            DetailsOverlay overlay = CreateOverlay(item, copyTextAsync: (text, _) =>
            {
                copied = text;
                return Task.CompletedTask;
            });
            Grid root = (Grid)overlay.Content!;

            Click((Button)root.FindName("CopyButton"));

            Assert.Same(item, overlay.DataContext);
            Assert.Equal(item.Record.Text, copied);
            Assert.Equal("已复制到系统剪贴板", ((TextBlock)root.FindName("StatusLabel")).Text);
            Assert.True(((Button)root.FindName("CopyButton")).IsEnabled);
        });
    }

    [Fact]
    public void CopyTextErrorIsReportedInStatusAndTooltip()
    {
        WithItem(image: false, (owner, item) =>
        {
            DetailsOverlay overlay = CreateOverlay(item, copyTextAsync: (_, _) =>
                Task.FromException(new IOException("clipboard unavailable")));
            Grid root = (Grid)overlay.Content!;

            Click((Button)root.FindName("CopyButton"));

            string expected = "复制失败：clipboard unavailable";
            Assert.Equal(expected, ((TextBlock)root.FindName("StatusLabel")).Text);
            Assert.Equal(expected, ((TextBlock)root.FindName("StatusLabel")).ToolTip);
            Assert.True(((Button)root.FindName("CopyButton")).IsEnabled);
        });
    }

    [Fact]
    public void DetailsCommandUsesAsyncExecutionAndIsDisabledWhenMainViewModelCannotAct()
    {
        WithItem(image: false, (owner, item) =>
        {
            Assert.False(owner.CanAct);
            Assert.IsType<AsyncRelayCommand>(item.DetailsCommand);
            Assert.False(item.DetailsCommand.CanExecute(null));
        });

    }

    [Fact]
    public void FitUsesImageViewportAndZoomEnablesScrollBars()
    {
        WithItem(image: true, (owner, item) =>
        {
            DetailsOverlay overlay = CreateOverlay(item, (_, _) => Task.FromResult<BitmapSource>(CreateBitmap(1200, 800)));
            Grid root = (Grid)overlay.Content!;
            var scroll = (ScrollViewer)root.FindName("ImageScroll");
            var image = (Image)root.FindName("FullImage");
            var fit = (CheckBox)root.FindName("FitCheckBox");
            var slider = (Slider)root.FindName("ZoomSlider");
            overlay.Measure(new Size(800, 760));
            overlay.Arrange(new Rect(0, 0, 800, 760));
            overlay.UpdateLayout();
            overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            overlay.UpdateLayout();

            Assert.True(image.ActualWidth <= scroll.ViewportWidth + 0.1);
            Assert.True(image.ActualHeight <= scroll.ViewportHeight + 0.1);
            Assert.True(fit.IsChecked);
            Assert.False(slider.IsEnabled);
            Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, scroll.VerticalScrollBarVisibility);

            fit.IsChecked = false;
            slider.Value = 2;

            Assert.True(slider.IsEnabled);
            Assert.Equal(2400, image.Width);
            Assert.Equal(1600, image.Height);
            Assert.Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        });
    }

    [Fact]
    public void AsyncRelayCommandDisablesReexecutionUntilTaskCompletes()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(() => completion.Task);

        Assert.True(command.CanExecute(null));
        command.Execute(null);
        Assert.False(command.CanExecute(null));

        completion.SetResult();
        Assert.True(SpinWait.SpinUntil(() => command.CanExecute(null), TimeSpan.FromSeconds(5)));
    }

    private void WithItem(bool image, Action<MainViewModel, RecordViewModel> action)
    {
        MainViewModel? owner = null;
        RecordViewModel? item = null;
        wpf.Run(() =>
        {
            var window = new Window();
            owner = new MainViewModel(window, new HistoryService(new HistoryStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))));
            var record = image
                ? new ClipboardRecord
                {
                    Kind = ClipboardRecordKind.Image,
                    ImageFileName = $"{Guid.NewGuid():N}.png",
                    ImageWidth = 1200,
                    ImageHeight = 800,
                    ByteLength = 1,
                    ContentHash = new string('A', 64),
                    CopiedAtUtc = DateTime.UtcNow
                }
                : new ClipboardRecord
                {
                    Kind = ClipboardRecordKind.Text,
                    Text = "full detail text",
                    ByteLength = 16,
                    ContentHash = new string('B', 64),
                    CopiedAtUtc = DateTime.UtcNow
                };
            item = new RecordViewModel(record, owner);
        });

        try { wpf.Run(() => action(owner!, item!)); }
        finally
        {
            Task shutdown = Task.CompletedTask;
            wpf.Run(() => shutdown = owner!.ShutdownAsync());
            shutdown.GetAwaiter().GetResult();
        }
    }

    private static DetailsOverlay CreateOverlay(
        RecordViewModel item,
        Func<string, CancellationToken, Task<BitmapSource>>? loadImageAsync = null,
        Func<string, CancellationToken, Task>? copyTextAsync = null)
    {
        return new DetailsOverlay(item,
            copyTextAsync ?? ((_, _) => Task.CompletedTask),
            (_, _) => Task.CompletedTask,
            loadImageAsync ?? ((_, _) => Task.FromResult<BitmapSource>(CreateBitmap())));
    }

    private static BitmapSource CreateBitmap(int width = 32, int height = 24)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
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



