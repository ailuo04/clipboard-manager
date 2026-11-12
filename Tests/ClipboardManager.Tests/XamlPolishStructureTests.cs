using System.IO;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class XamlPolishStructureTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Shell = "clr-namespace:System.Windows.Shell;assembly=PresentationFramework";

    [Fact]
    public void OverlayLayerIsHitTestableInWindowChrome()
    {
        XElement root = LoadView("WindowFrame.xaml");
        XElement overlay = root.Descendants(Presentation + "Grid")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "OverlayLayer");

        Assert.Equal("True", (string?)overlay.Attribute(Shell + "WindowChrome.IsHitTestVisibleInChrome"));
    }

    [Theory]
    [InlineData("SettingsView.xaml", "设置", "ScrollViewer", true)]
    [InlineData("UpdateView.xaml", "应用更新", "ScrollViewer", true)]
    [InlineData("DetailsView.xaml", "详情", "Grid", false)]
    public void OverlayPagesKeepAccessibleNameBodyAndFooter(
        string fileName, string pageName, string bodyElementName, bool footerHasActions)
    {
        XElement root = LoadView(fileName);
        XElement[] rows = root.Element(Presentation + "Grid.RowDefinitions")!
            .Elements(Presentation + "RowDefinition").ToArray();
        XElement[] topLevel = root.Elements().ToArray();
        XElement body = topLevel.First(element => GridRow(element) == "1" && element.Name == Presentation + bodyElementName);
        XElement footer = topLevel.Single(element => GridRow(element) == "2");

        Assert.Equal(pageName, (string?)root.Attribute("AutomationProperties.Name"));
        Assert.Equal(new[] { "Auto", "*", "Auto" }, rows.Select(row => (string?)row.Attribute("Height")));
        Assert.Equal(Presentation + bodyElementName, body.Name);
        // The page header and its close affordance live in WindowFrame's overlay card.
        Assert.DoesNotContain(root.Descendants(Presentation + "Button"),
            button => ((string?)button.Attribute(Xaml + "Name"))?.StartsWith("Close", StringComparison.Ordinal) == true);
        if (footerHasActions) Assert.NotEmpty(footer.Descendants(Presentation + "Button"));
    }

    [Fact]
    public void WindowFrameHostsPageTitleAndBackAffordance()
    {
        XElement root = LoadView("WindowFrame.xaml");
        XElement[] named = root.Descendants().ToArray();
        XElement title = named.Single(element => (string?)element.Attribute(Xaml + "Name") == "PageTitle");
        XElement back = named.Single(element => (string?)element.Attribute(Xaml + "Name") == "BackButton");

        XElement panel = named.Single(element => (string?)element.Attribute(Xaml + "Name") == "OverlayPanel");
        Assert.Contains(title, panel.Descendants());
        Assert.Contains(back, panel.Descendants());
        Assert.Equal("Collapsed", (string?)back.Attribute("Visibility"));
        Assert.Equal("返回", (string?)back.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void SettingsKeepsAppearanceAsAContentSectionUnderTheNewPageHeader()
    {
        XElement root = LoadView("SettingsView.xaml");
        XElement body = root.Elements(Presentation + "ScrollViewer").Single(element => GridRow(element) == "1");

        Assert.Contains(body.Descendants(Presentation + "TextBlock"), text => (string?)text.Attribute("Text") == "外观");
    }

    [Theory]
    [InlineData("LightGlass.xaml", "#FFFFFFFF")]
    [InlineData("LiquidGlass.xaml", "#FF101014")]
    public void SelectedFilterAndErrorTextKeepReadableContrast(string fileName, string canvasHex)
    {
        XElement root = LoadView(fileName);
        Color canvas = ParseColor(canvasHex);
        Color selectedForeground = SolidBrushColor(root, "FilterSelectedForegroundBrush");
        Color errorForeground = SolidBrushColor(root, "ErrorTextBrush");
        Color[] windows = BrushColors(root, "WindowBackgroundBrush");
        Color[] filterPanels = BrushColors(root, "FilterPanelBrush");
        Color[] selections = BrushColors(root, "SelectionBrush");

        double selectedMinimum = windows.SelectMany(window => filterPanels.SelectMany(panel => selections.Select(selection =>
        {
            Color background = Composite(Composite(Composite(window, canvas), panel), selection);
            return ContrastRatio(selectedForeground, background);
        }))).Min();
        double errorMinimum = windows.Select(window => ContrastRatio(errorForeground, Composite(window, canvas))).Min();

        Assert.True(selectedMinimum >= 4.5, $"Selected filter text contrast was {selectedMinimum:F2}:1.");
        Assert.True(errorMinimum >= 4.5, $"Error text contrast was {errorMinimum:F2}:1.");
    }

    private static XElement LoadView(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Xaml", "Views", fileName)).Root!;

    private static string? GridRow(XElement element) => (string?)element.Attribute("Grid.Row") ?? "0";

    private static XElement Resource(XElement root, string key) => root.DescendantsAndSelf()
        .Single(element => (string?)element.Attribute(Xaml + "Key") == key);

    private static Color SolidBrushColor(XElement root, string key) =>
        ParseColor((string)Resource(root, key).Attribute("Color")!);

    private static Color[] BrushColors(XElement root, string key)
    {
        XElement brush = Resource(root, key);
        if (brush.Name == Presentation + "SolidColorBrush")
            return new[] { ParseColor((string)brush.Attribute("Color")!) };
        return brush.Descendants(Presentation + "GradientStop")
            .Select(stop => ParseColor((string)stop.Attribute("Color")!)).ToArray();
    }

    private static Color ParseColor(string value) => (Color)ColorConverter.ConvertFromString(value)!;

    private static Color Composite(Color foreground, Color background)
    {
        double alpha = foreground.A / 255d;
        byte Blend(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return Color.FromRgb(Blend(foreground.R, background.R), Blend(foreground.G, background.G), Blend(foreground.B, background.B));
    }

    private static double ContrastRatio(Color foreground, Color background)
    {
        double first = RelativeLuminance(foreground);
        double second = RelativeLuminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}
