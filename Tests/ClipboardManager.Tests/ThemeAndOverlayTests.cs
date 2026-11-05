using System.IO;
using System.Text.Json;
using System.Windows;
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

        Assert.Equal(Keys(Path.Combine(root, "LightGlass.xaml"), x),
            Keys(Path.Combine(root, "LiquidGlass.xaml"), x));
    }
}
