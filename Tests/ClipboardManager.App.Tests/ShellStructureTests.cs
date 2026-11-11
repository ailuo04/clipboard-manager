using System.Xml.Linq;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class ShellStructureTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MainWindow_has_one_NavigationView_with_the_four_primary_navigation_items()
    {
        XDocument document = XDocument.Load(SourcePath("MainWindow.xaml"));
        Assert.Equal("Window", document.Root?.Name.LocalName);
        Assert.Equal("ClipboardManager.App.MainWindow", (string?)document.Root?.Attribute(Xaml + "Class"));
        XElement navigation = Assert.Single(document.Descendants(), element => element.Name.LocalName == "NavigationView");
        string[] labels = navigation.Descendants().Where(element => element.Name.LocalName == "NavigationViewItem")
            .Select(element => (string?)element.Attribute("Content")).Where(value => value is not null).Cast<string>().ToArray();
        Assert.Equal(["历史", "收藏", "更新中心", "设置"], labels);
    }

    [Fact]
    public void App_uses_one_cached_main_window_and_no_WPF_or_legacy_overlay()
    {
        string appDirectory = Path.GetDirectoryName(SourcePath("App.xaml"))!;
        string[] sourceFiles = Directory.GetFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)).ToArray();
        string source = string.Join(Environment.NewLine, sourceFiles.Select(File.ReadAllText));
        string[] windowXamls = Directory.GetFiles(appDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .Where(path => XDocument.Load(path).Root?.Name.LocalName == "Window").ToArray();

        Assert.Single(windowXamls);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"new MainWindow\(").Cast<System.Text.RegularExpressions.Match>());
        Assert.Contains("if (_window is not null) return _window;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Window()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowOverlayHost", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Light_dark_and_high_contrast_dictionaries_have_the_same_brush_tokens()
    {
        XDocument document = XDocument.Load(SourcePath(Path.Combine("Resources", "ThemeResources.xaml")));
        XElement[] dictionaries = document.Descendants().Where(element => element.Name.LocalName == "ResourceDictionary"
            && element.Attribute(Xaml + "Key") is not null).ToArray();
        string[] Keys(string key)
        {
            XElement dictionary = Assert.Single(dictionaries, element => (string?)element.Attribute(Xaml + "Key") == key);
            return dictionary.Elements().Select(element => (string?)element.Attribute(Xaml + "Key"))
                .Where(value => value is not null).Cast<string>().Order().ToArray();
        }
        string[] light = Keys("Light");
        Assert.Equal(light, Keys("Dark"));
        Assert.Equal(light, Keys("HighContrast"));
        Assert.Contains("PageBackgroundBrush", light);
        Assert.Contains("CardBackgroundBrush", light);
        Assert.Contains("AccentBrush", light);
        XElement highContrast = Assert.Single(dictionaries, element => (string?)element.Attribute(Xaml + "Key") == "HighContrast");
        Assert.All(highContrast.Elements(), element => Assert.Contains("SystemColor", (string?)element.Attribute("Color"), StringComparison.Ordinal));
    }

    [Fact]
    public void Shell_has_responsive_thresholds_dpi_aware_minimum_size_and_actual_accessible_navigation_names()
    {
        XDocument document = XDocument.Load(SourcePath("MainWindow.xaml"));
        string source = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        string shell = File.ReadAllText(SourcePath(Path.Combine("Views", "ShellPage.xaml")));

        Assert.Contains("900", source, StringComparison.Ordinal);
        Assert.Contains("760", source, StringComparison.Ordinal);
        Assert.Contains("720 * scale", source, StringComparison.Ordinal);
        Assert.Contains("520 * scale", source, StringComparison.Ordinal);
        Assert.Contains("1000 * initialScale", source, StringComparison.Ordinal);
        Assert.Contains("UpdateResponsiveLayout(_windowWidth)", source, StringComparison.Ordinal);
        string[] names = document.Descendants().Where(element => element.Name.LocalName == "NavigationViewItem")
            .Select(element => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "AutomationProperties.Name"))
            .Where(attribute => attribute is not null).Select(attribute => attribute!.Value).ToArray();
        Assert.Equal(["历史", "收藏", "更新中心", "设置"], names);
        Assert.DoesNotContain("AutomationProperties.Name=\"页面标题\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"空状态标题\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_center_uses_a_dedicated_page_with_explicit_controls_and_non_blocking_notice()
    {
        XDocument updates = XDocument.Load(SourcePath(Path.Combine("Views", "UpdatesPage.xaml")));
        string xaml = updates.ToString();
        string window = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        string windowXaml = File.ReadAllText(SourcePath("MainWindow.xaml"));

        Assert.Equal("Page", updates.Root?.Name.LocalName);
        Assert.DoesNotContain("Window", updates.Descendants().Select(element => element.Name.LocalName));
        Assert.Contains("ViewModel.CurrentVersion", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.AvailableVersion", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ReleaseNotes", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ProgressPercent", xaml, StringComparison.Ordinal);
        Assert.Contains("自动检查只获取版本信息", xaml, StringComparison.Ordinal);
        Assert.Contains("检查更新", xaml, StringComparison.Ordinal);
        Assert.Contains("下载并更新", xaml, StringComparison.Ordinal);
        Assert.Contains("取消下载", xaml, StringComparison.Ordinal);
        Assert.Contains("new UpdatesPage(_createUpdatesViewModel())", window, StringComparison.Ordinal);
        Assert.Contains("UpdateNoticeButton", windowXaml, StringComparison.Ordinal);
        Assert.Contains("有新版本", windowXaml, StringComparison.Ordinal);
    }
    [Fact]
    public void App_merges_WinUI_control_resources_before_app_design_resources()
    {
        XDocument document = XDocument.Load(SourcePath("App.xaml"));
        string[] resources = document.Descendants()
            .Where(element => element.Name.LocalName == "XamlControlsResources"
                || element.Name.LocalName == "ResourceDictionary")
            .Select(element => (string?)element.Attribute("Source") ?? element.Name.LocalName).ToArray();
        Assert.True(Array.IndexOf(resources, "XamlControlsResources") >= 0);
        int stylesIndex = Array.FindIndex(resources, resource => resource.EndsWith("/Resources/Styles.xaml", StringComparison.Ordinal));
        Assert.True(stylesIndex > Array.IndexOf(resources, "XamlControlsResources"));
    }

    [Fact]
    public void History_header_places_record_count_beside_title_without_overlapping()
    {
        XDocument document = XDocument.Load(SourcePath(Path.Combine("Views", "HistoryPage.xaml")));
        XElement header = Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "HeaderGrid");
        XElement columns = Assert.Single(header.Elements(), element => element.Name.LocalName == "Grid.ColumnDefinitions");
        Assert.Equal(2, columns.Elements().Count(element => element.Name.LocalName == "ColumnDefinition"));
        XElement title = Assert.Single(header.Descendants(), element => element.Name.LocalName == "TextBlock" && (string?)element.Attribute(Xaml + "Name") == "PageTitle");
        XElement count = Assert.Single(header.Descendants(), element => element.Name.LocalName == "TextBlock" && (string?)element.Attribute(Xaml + "Name") == "RecordCountText");
        Assert.Equal("0", (string?)title.Parent?.Attribute("Grid.Column"));
        Assert.Equal("1", (string?)count.Attribute("Grid.Column"));
    }
    [Fact]
    public void Settings_page_exposes_all_required_persisted_options_and_retention_warning()
    {
        XDocument document = XDocument.Load(SourcePath(Path.Combine("Views", "SettingsPage.xaml")));
        string xaml = document.ToString();
        string window = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        string[] requiredBindings = [
            "ViewModel.ThemePreference", "ViewModel.MaxRecordsText", "ViewModel.MaxStorageMbText",
            "ViewModel.AlwaysOnTop", "ViewModel.AutoCheckUpdates", "ViewModel.MinimizeToTray",
            "ViewModel.HideAfterCopy", "ViewModel.DataDirectoryPath", "ViewModel.CapacityWarning"
        ];

        Assert.All(requiredBindings, binding => Assert.Contains(binding, xaml, StringComparison.Ordinal));
        Assert.Contains("确认降低历史容量？", File.ReadAllText(SourcePath(Path.Combine("Views", "SettingsPage.xaml.cs"))), StringComparison.Ordinal);
        Assert.Contains("new SettingsPage(_createSettingsViewModel())", window, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", window, StringComparison.Ordinal);
        Assert.Contains("_settings.MinimizeToTray && _isTrayEnabled() && !_exitRequested", window, StringComparison.Ordinal);
        Assert.DoesNotContain("自启动", xaml, StringComparison.Ordinal);
    }
    private static string SourcePath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClipboardManager.sln")))
                return Path.Combine(directory.FullName, "src", "ClipboardManager.App", relativePath);
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the source workspace from the test output directory.");
    }
}
