using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Xml.Linq;
using ClipboardManager;
using ClipboardManager.Models;
using ClipboardManager.Services;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class ThemeAndOverlayTests
{
    [Fact]
    public void OverlayPageStackPopsNestedPagesInLastInFirstOutOrder()
    {
        var stack = new OverlayPageStack<string>();

        stack.Push("settings");
        stack.Push("updates");

        Assert.Equal("updates", stack.Current);
        Assert.Equal("updates", stack.Pop());
        Assert.Equal("settings", stack.Current);
        Assert.Equal("settings", stack.Pop());
        Assert.Null(stack.Current);
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void OverlayPageStackPopOnEmptyReturnsNull()
    {
        var stack = new OverlayPageStack<string>();

        Assert.Null(stack.Pop());
        Assert.Null(stack.Current);
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void OverlayPageStackRejectsNullPages()
    {
        var stack = new OverlayPageStack<string>();

        Action pushNull = () => stack.Push(null!);
        Assert.Throws<ArgumentNullException>(pushNull);
    }

    [Fact]
    public void OverlayPageStackClearRemovesAllPages()
    {
        var stack = new OverlayPageStack<string>();
        stack.Push("settings");
        stack.Push("updates");

        stack.Clear();

        Assert.Null(stack.Current);
        Assert.Equal(0, stack.Count);
        Assert.Null(stack.Pop());
    }

    [Fact]
    public void OverlayViewCompleteForwardsNullableResultAndDismissal()
    {
        var closeResults = new List<bool?>();
        bool? dismissedResult = null;
        var thread = new Thread(() =>
        {
            var view = new TestOverlayView();
            view.CloseRequest = closeResults.Add;
            view.CompleteWith(true);
            view.CompleteWith(null);
            view.OnDismissed(false);
            dismissedResult = view.DismissedResult;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Equal(new bool?[] { true, null }, closeResults);
        Assert.False(dismissedResult);
    }

    [Fact]
    public void OverlayWindowGeometryCentersAnOrdinaryDesiredSize()
    {
        var workArea = new Rect(100, 50, 800, 600);
        var result = OverlayWindowGeometry.Fit(workArea, new Size(400, 300), 24);

        Assert.Equal(new Rect(300, 200, 400, 300), result);
        AssertGeometryIsInsideAndCentered(workArea, result, new Size(400, 300));
    }

    [Fact]
    public void OverlayWindowGeometryClampsOversizedWidthAndHeightToAvailableArea()
    {
        var workArea = new Rect(10, 20, 300, 200);
        var result = OverlayWindowGeometry.Fit(workArea, new Size(500, 400), 20);

        Assert.Equal(new Rect(30, 40, 260, 160), result);
        AssertGeometryIsInsideAndCentered(workArea, result, new Size(260, 160));
    }

    [Fact]
    public void OverlayWindowGeometryNeverReturnsNegativeSizeWhenMarginConsumesWorkArea()
    {
        var workArea = new Rect(10, 15, 100, 60);
        var result = OverlayWindowGeometry.Fit(workArea, new Size(40, 20), 40);

        Assert.Equal(new Rect(50, 45, 20, 0), result);
        AssertGeometryIsInsideAndCentered(workArea, result, new Size(20, 0));
    }

    [Fact]
    public void OverlayWindowGeometryTreatsNegativeMarginAsZero()
    {
        var workArea = new Rect(10, 15, 100, 60);
        var result = OverlayWindowGeometry.Fit(workArea, new Size(40, 20), -5);

        Assert.Equal(new Rect(40, 35, 40, 20), result);
        AssertGeometryIsInsideAndCentered(workArea, result, new Size(40, 20));
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

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

    private sealed class TestOverlayView : OverlayView
    {
        internal bool? DismissedResult { get; private set; }

        internal void CompleteWith(bool? result) => Complete(result);

        internal override void OnDismissed(bool? result) => DismissedResult = result;
    }

    private static void AssertGeometryIsInsideAndCentered(Rect workArea, Rect result, Size expectedSize)
    {
        Assert.True(result.Left >= workArea.Left);
        Assert.True(result.Top >= workArea.Top);
        Assert.True(result.Right <= workArea.Right);
        Assert.True(result.Bottom <= workArea.Bottom);
        Assert.Equal(expectedSize.Width, result.Width);
        Assert.Equal(expectedSize.Height, result.Height);
        Assert.Equal(workArea.Left + workArea.Width / 2, result.Left + result.Width / 2);
        Assert.Equal(workArea.Top + workArea.Height / 2, result.Top + result.Height / 2);
    }

    [Theory]
    [InlineData(null, AppTheme.Light)]
    [InlineData(AppTheme.Light, AppTheme.Light)]
    [InlineData(AppTheme.Dark, AppTheme.Dark)]
    [InlineData(AppTheme.System, AppTheme.System)]
    [InlineData((AppTheme)99, AppTheme.Light)]
    public void MissingOrInvalidThemeUsesLightAndValidPreferenceIsPreserved(AppTheme? saved, AppTheme expected)
    {
        Assert.Equal(expected, ThemePreferencePolicy.Resolve(saved));
    }

    [Theory]
    [InlineData(null, AppTheme.Light)]
    [InlineData("null", AppTheme.Light)]
    [InlineData("{}", AppTheme.Light)]
    [InlineData("{", AppTheme.Light)]
    [InlineData("{\"theme\":99}", AppTheme.Light)]
    [InlineData("{\"theme\":\"Unknown\"}", AppTheme.Light)]
    [InlineData("{\"theme\":\"Light\"}", AppTheme.Light)]
    [InlineData("{\"theme\":\"Dark\"}", AppTheme.Dark)]
    [InlineData("{\"theme\":\"System\"}", AppTheme.System)]
    public void SavedThemeJsonUsesLightForMissingOrInvalidValuesAndPreservesValidThemes(string? json, AppTheme expected)
    {
        Assert.Equal(expected, ThemePreferencePolicy.ResolveSavedPreferenceJson(json));
    }

    [Fact]
    public void InvalidPreferenceIsNormalizedToLightBeforeSerialization()
    {
        using var document = JsonDocument.Parse(ThemePreferencePolicy.SerializePreference((AppTheme)99));
        var theme = document.RootElement.GetProperty("theme");

        Assert.Equal(JsonValueKind.String, theme.ValueKind);
        Assert.Equal("Light", theme.GetString());
    }

    [Fact]
    public void LightAndDarkPalettesExposeIdenticalResourceKeys()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        static string[] Keys(string path, XNamespace ns) => XDocument.Load(path).Root!
            .DescendantsAndSelf().Attributes(ns + "Key").Select(attribute => attribute.Value)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();

        string[] lightKeys = Keys(Path.Combine(root, "LightGlass.xaml"), x);
        string[] darkKeys = Keys(Path.Combine(root, "LiquidGlass.xaml"), x);

        Assert.Equal(lightKeys, darkKeys);
        Assert.Contains("OverlayScrimBrush", lightKeys);
        Assert.Contains("OverlayGlassBrush", lightKeys);
        Assert.Contains("OverlayEdgeBrush", lightKeys);
        Assert.Contains("OverlayShadowEffect", lightKeys);
    }

    [Fact]
    public void WindowFramePlacesCollapsedOverlayAboveNamedMainSurface()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views", "WindowFrame.xaml");
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement root = XDocument.Load(path).Root!;
        XElement main = root.Elements(ns + "Grid").Single(element => (string?)element.Attribute(x + "Name") == "MainSurface");
        XElement overlay = root.Elements(ns + "Grid").Single(element => (string?)element.Attribute(x + "Name") == "OverlayLayer");
        XElement titleBar = main.Elements(ns + "Grid").Single(element => (string?)element.Attribute(x + "Name") == "TitleBar");
        XElement content = main.Elements(ns + "Grid").Single(element => (string?)element.Attribute(x + "Name") == "WindowContent");
        XElement panel = overlay.Descendants(ns + "Border").Single(element => (string?)element.Attribute(x + "Name") == "OverlayPanel");

        Assert.Null(titleBar.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)content.Attribute("Grid.Row"));
        Assert.Equal("Collapsed", (string?)overlay.Attribute("Visibility"));
        Assert.Equal("10", (string?)overlay.Attribute("Panel.ZIndex"));
        Assert.Equal("{DynamicResource OverlayScrimBrush}", (string?)overlay.Attribute("Background"));
        Assert.Equal("{DynamicResource OverlayGlassBrush}", (string?)panel.Attribute("Background"));
        Assert.Equal("{DynamicResource OverlayEdgeBrush}", (string?)panel.Attribute("BorderBrush"));
        Assert.Equal("{DynamicResource OverlayShadowEffect}", (string?)panel.Attribute("Effect"));
        Assert.NotEmpty(overlay.Descendants(ns + "ScrollViewer"));
    }

    [Fact]
    public void WindowOverlayHostCompletesNestedPagesIndependentlyAndRestoresMainSurface()
    {
        RunOnSta(() =>
        {
            var owner = new Window { Width = 340, Height = 500 };
            Grid root = CreateOverlayRoot();
            owner.Content = root;
            using var host = new WindowOverlayHost(owner, root);
            var parent = new TestOverlayView();
            var child = new TestOverlayView();

            Task<bool?> parentTask = host.ShowAsync(parent, OverlaySize.Settings);
            Task<bool?> childTask = host.ShowAsync(child, OverlaySize.Update);
            Assert.Equal(2, host.Count);
            Assert.False(((Grid)root.FindName("MainSurface")!).IsEnabled);
            Assert.Equal(Visibility.Visible, ((Grid)root.FindName("OverlayLayer")!).Visibility);
            Assert.False(parentTask.IsCompleted);

            host.CompleteCurrent(true);
            Assert.True(childTask.IsCompleted);
            Assert.True(childTask.GetAwaiter().GetResult());
            Assert.True(child.DismissedResult);
            Assert.False(parentTask.IsCompleted);
            Assert.Equal(1, host.Count);

            host.CompleteCurrent(null);
            Assert.True(parentTask.IsCompleted);
            Assert.Null(parentTask.GetAwaiter().GetResult());
            Assert.Null(parent.DismissedResult);
            Assert.Equal(0, host.Count);
            Assert.True(((Grid)root.FindName("MainSurface")!).IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((Grid)root.FindName("OverlayLayer")!).Visibility);
            owner.Content = null;
        });
    }

    [Fact]
    public void WindowOverlayHostBlursMainSurfaceAndRestoresWindowInputGeometryStateAndEffect()
    {
        RunOnSta(() =>
        {
            const double originalLeft = 120;
            const double originalTop = 140;
            const double originalWidth = 340;
            const double originalHeight = 500;
            var owner = new Window
            {
                Width = originalWidth,
                Height = originalHeight,
                Left = originalLeft,
                Top = originalTop,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowState = WindowState.Normal,
                Topmost = false
            };
            Grid root = CreateOverlayRoot();
            var mainSurface = (Grid)root.FindName("MainSurface")!;
            var originalEffect = new DropShadowEffect { BlurRadius = 2, Opacity = 0.25 };
            mainSurface.Effect = originalEffect;
            owner.Content = root;
            using var host = new WindowOverlayHost(owner, root);

            Task<bool?> pending = host.ShowAsync(new TestOverlayView(), OverlaySize.Settings);

            BlurEffect blur = Assert.IsType<BlurEffect>(mainSurface.Effect);
            Assert.Equal(4, blur.Radius);
            Assert.Equal(RenderingBias.Performance, blur.RenderingBias);
            Assert.False(mainSurface.IsEnabled);
            Assert.False(mainSurface.IsHitTestVisible);

            owner.Topmost = true;
            owner.WindowState = WindowState.Maximized;
            host.CompleteCurrent(null);

            Assert.True(pending.IsCompleted);
            Assert.Null(pending.GetAwaiter().GetResult());
            Assert.True(mainSurface.IsEnabled);
            Assert.True(mainSurface.IsHitTestVisible);
            Assert.Same(originalEffect, mainSurface.Effect);
            Assert.Equal(originalLeft, owner.Left);
            Assert.Equal(originalTop, owner.Top);
            Assert.Equal(originalWidth, owner.Width);
            Assert.Equal(originalHeight, owner.Height);
            Assert.Equal(WindowState.Normal, owner.WindowState);
            Assert.False(owner.Topmost);
            Assert.Equal(Visibility.Collapsed, ((Grid)root.FindName("OverlayLayer")!).Visibility);
            owner.Content = null;
        });
    }

    [Fact]
    public void WindowOverlayHostRestoresKeyboardFocusAfterSession()
    {
        RunOnSta(() =>
        {
            var owner = new Window
            {
                Width = 340,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false
            };
            Grid root = CreateOverlayRoot();
            var mainSurface = (Grid)root.FindName("MainSurface")!;
            var input = new TextBox();
            mainSurface.Children.Add(input);
            owner.Content = root;
            owner.Show();
            owner.UpdateLayout();
            owner.Activate();
            Assert.True(input.Focus());
            Assert.Same(input, Keyboard.FocusedElement);

            using (var host = new WindowOverlayHost(owner, root))
            {
                host.ShowAsync(new TestOverlayView(), OverlaySize.Update);
                owner.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                host.CompleteCurrent(null);
                Assert.Same(input, Keyboard.FocusedElement);
            }

            owner.Close();
        });
    }

    [Fact]
    public void WindowOverlayHostDisposeCompletesPendingTasksAndRestoresMainSurface()
    {
        RunOnSta(() =>
        {
            var owner = new Window { Width = 340, Height = 500 };
            Grid root = CreateOverlayRoot();
            var mainSurface = (Grid)root.FindName("MainSurface")!;
            var originalEffect = new DropShadowEffect { BlurRadius = 2, Opacity = 0.25 };
            mainSurface.Effect = originalEffect;
            owner.Content = root;
            var host = new WindowOverlayHost(owner, root);
            Task<bool?> parentTask = host.ShowAsync(new TestOverlayView(), OverlaySize.Settings);
            Task<bool?> childTask = host.ShowAsync(new TestOverlayView(), OverlaySize.Update);

            host.Dispose();

            Assert.True(parentTask.IsCompleted);
            Assert.Null(parentTask.GetAwaiter().GetResult());
            Assert.True(childTask.IsCompleted);
            Assert.Null(childTask.GetAwaiter().GetResult());
            Assert.False(host.IsOpen);
            Assert.Equal(0, host.Count);
            Assert.True(mainSurface.IsEnabled);
            Assert.True(mainSurface.IsHitTestVisible);
            Assert.Same(originalEffect, mainSurface.Effect);
            Assert.Equal(Visibility.Collapsed, ((Grid)root.FindName("OverlayLayer")!).Visibility);
            Action showAfterDispose = () => { _ = host.ShowAsync(new TestOverlayView(), OverlaySize.Details); };
            Assert.Throws<ObjectDisposedException>(showAfterDispose);
            owner.Content = null;
        });
    }

    [Fact]
    public void WindowOverlayHostCloseAllResolvesPendingTasksAndIgnoresLatePageCallbacks()
    {
        RunOnSta(() =>
        {
            var owner = new Window { Width = 340, Height = 500 };
            Grid root = CreateOverlayRoot();
            owner.Content = root;
            using var host = new WindowOverlayHost(owner, root);
            var parent = new TestOverlayView();
            var child = new TestOverlayView();
            Task<bool?> parentTask = host.ShowAsync(parent, OverlaySize.Settings);
            Task<bool?> childTask = host.ShowAsync(child, OverlaySize.Update);
            Action<bool?>? lateChildCallback = child.CloseRequest;

            host.CloseAll(null);
            Task<bool?> nextTask = host.ShowAsync(new TestOverlayView(), OverlaySize.Details);
            lateChildCallback?.Invoke(true);

            Assert.True(parentTask.IsCompleted);
            Assert.Null(parentTask.GetAwaiter().GetResult());
            Assert.True(childTask.IsCompleted);
            Assert.Null(childTask.GetAwaiter().GetResult());
            Assert.Null(parent.DismissedResult);
            Assert.Null(child.DismissedResult);
            Assert.False(nextTask.IsCompleted);
            Assert.Equal(1, host.Count);
            host.CompleteCurrent(false);
            Assert.True(nextTask.IsCompleted);
            Assert.False(nextTask.GetAwaiter().GetResult());
            owner.Content = null;
        });
    }

    [Theory]
    [InlineData((int)OverlaySize.Settings, 510, 660)]
    [InlineData((int)OverlaySize.Update, 510, 510)]
    [InlineData((int)OverlaySize.Details, 800, 660)]
    public void OverlaySizePolicyUsesDesignedPageSizes(int size, double expectedWidth, double expectedHeight)
    {
        Assert.Equal(new Size(expectedWidth, expectedHeight), OverlaySizePolicy.GetDesiredSize((OverlaySize)size));
    }
}
