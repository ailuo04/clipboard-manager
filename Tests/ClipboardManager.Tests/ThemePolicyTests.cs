using System.IO;
using System.Xaml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using ClipboardManager.Models;
using ClipboardManager.Services;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class ThemePolicyTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex DynamicResourcePattern = new(@"\{DynamicResource\s+([^,}\s]+)", RegexOptions.Compiled);

    [Theory]
    [InlineData(null, AppTheme.Light)]
    [InlineData(AppTheme.Light, AppTheme.Light)]
    [InlineData(AppTheme.Dark, AppTheme.Dark)]
    [InlineData(AppTheme.System, AppTheme.System)]
    public void ResolvePreference_ReturnsExpectedPreference(AppTheme? savedPreference, AppTheme expected)
    {
        Assert.Equal(expected, ThemeManager.ResolvePreference(savedPreference));
    }

    [Fact]
    public void ResolvePreference_UsesLightForInvalidSavedPreference()
    {
        Assert.Equal(AppTheme.Light, ThemeManager.ResolvePreference((AppTheme)int.MaxValue));
    }

    [Fact]
    public void LightAndLiquidGlassThemes_HaveMatchingResourceKeys()
    {
        var lightKeys = LoadThemeKeys("LightGlass.xaml");
        var liquidKeys = LoadThemeKeys("LiquidGlass.xaml");

        Assert.Equal(lightKeys.OrderBy(key => key, StringComparer.Ordinal),
                     liquidKeys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void XamlFiles_ParseAndDynamicResourcesExistInThemeKeys()
    {
        string xamlRoot = Path.Combine(AppContext.BaseDirectory, "Xaml");
        string[] xamlFiles = Directory.GetFiles(xamlRoot, "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(xamlFiles);

        var themeKeys = LoadThemeKeys("LightGlass.xaml").ToHashSet(StringComparer.Ordinal);
        foreach (string file in xamlFiles)
        {
            using (var stream = File.OpenRead(file))
            using (var reader = new XamlXmlReader(stream))
            {
                while (reader.Read()) { }
            }

            XDocument document = XDocument.Load(file);
            foreach (string value in document.Descendants().Attributes().Select(attribute => attribute.Value))
            {
                foreach (Match match in DynamicResourcePattern.Matches(value))
                {
                    string resourceKey = match.Groups[1].Value;
                    Assert.True(resourceKey == "AppIcon" || themeKeys.Contains(resourceKey),
                        $"DynamicResource '{resourceKey}' in '{Path.GetRelativePath(xamlRoot, file)}' is not defined by either theme.");
                }
            }
        }
    }

    [Fact]
    public void MainView_ShadowColorsUseThemePalette()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views", "MainView.xaml");
        XDocument document = XDocument.Load(path);
        var shadows = document.Descendants().Where(element => element.Name.LocalName == "DropShadowEffect").ToArray();

        Assert.NotEmpty(shadows);
        Assert.Contains(shadows, shadow => (string?)shadow.Attribute("Color") == "{DynamicResource CardShadowColor}");
        Assert.All(shadows, shadow => Assert.StartsWith("{DynamicResource ", (string?)shadow.Attribute("Color")));
    }

    [Fact]
    public void AuxiliaryViews_KeepCodeBehindControlNamesAndTypes()
    {
        AssertControls("DetailsView.xaml",
            ("CopyButton", "Button"), ("FullImage", "Image"), ("ImageScroll", "ScrollViewer"),
            ("FitCheckBox", "CheckBox"), ("ZoomSlider", "Slider"), ("ZoomLabel", "TextBlock"),
            ("StatusLabel", "TextBlock"), ("CloseButton", "Button"));
        AssertControls("SettingsView.xaml",
            ("ThemeDarkRadio", "RadioButton"), ("ThemeLightRadio", "RadioButton"), ("ThemeSystemRadio", "RadioButton"),
            ("MaxRecordsBox", "TextBox"), ("MaxStorageBox", "TextBox"), ("AlwaysOnTopCheckBox", "CheckBox"),
            ("MinimizeToTrayCheckBox", "CheckBox"), ("AutoCheckUpdatesCheckBox", "CheckBox"),
            ("OpenDirectoryButton", "Button"), ("OpenLogButton", "Button"), ("OpenUpdatesButton", "Button"),
            ("SettingsVersionText", "TextBlock"), ("ValidationText", "TextBlock"),
            ("CancelButton", "Button"), ("SaveButton", "Button"));
        AssertControls("UpdateView.xaml",
            ("CurrentVersionText", "TextBlock"), ("CheckUpdateButton", "Button"),
            ("UpdateStatusText", "TextBlock"), ("UpdateDetailsText", "TextBlock"),
            ("UpdateProgressBar", "ProgressBar"), ("CancelDownloadButton", "Button"),
            ("UpdateTokenBox", "PasswordBox"), ("SaveUpdateTokenButton", "Button"),
            ("DeleteUpdateTokenButton", "Button"), ("UpdateCredentialText", "TextBlock"),
            ("CloseUpdateButton", "Button"), ("InstallUpdateButton", "Button"));
    }

    private static void AssertControls(string fileName, params (string Name, string Element)[] expectedControls)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views", fileName);
        var actualControls = XDocument.Load(path).Descendants()
            .Where(element => element.Attribute(XamlNamespace + "Name") is not null)
            .ToDictionary(
                element => (string)element.Attribute(XamlNamespace + "Name")!,
                element => element.Name.LocalName,
                StringComparer.Ordinal);

        foreach (var (name, expectedElement) in expectedControls)
        {
            Assert.True(actualControls.TryGetValue(name, out string? actualElement), $"Missing named control '{name}' in {fileName}.");
            Assert.Equal(expectedElement, actualElement);
        }
    }
    private static string[] LoadThemeKeys(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views", fileName);
        return XDocument.Load(path)
            .Descendants()
            .Attributes(XamlNamespace + "Key")
            .Select(attribute => attribute.Value)
            .ToArray();
    }
}
