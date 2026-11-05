using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using ClipboardManager.Models;
using ClipboardManager.Services;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class ThemeAndOverlayTests
{
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
