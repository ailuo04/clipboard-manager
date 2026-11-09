using System.Xml.Linq;
using Xunit;

namespace ClipboardManager.Architecture.Tests;

public sealed class ArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Core_targets_net10()
    {
        var project = LoadProject("src/ClipboardManager.Core/ClipboardManager.Core.csproj");

        Assert.Equal("net10.0", Property(project, "TargetFramework"));
    }

    [Fact]
    public void Core_and_storage_do_not_enable_wpf_windows_forms_or_xaml_references()
    {
        foreach (var relativePath in new[]
                 {
                     "src/ClipboardManager.Core/ClipboardManager.Core.csproj",
                     "src/ClipboardManager.Storage/ClipboardManager.Storage.csproj"
                 })
        {
            var projectText = File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

            Assert.DoesNotContain("<UseWPF>true</UseWPF>", projectText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<UseWindowsForms>true</UseWindowsForms>", projectText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PresentationFramework", projectText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Windows.UI.Xaml", projectText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void App_is_winui_unpacked_and_self_contained_for_win_x64()
    {
        var project = LoadProject("src/ClipboardManager.App/ClipboardManager.App.csproj");

        Assert.Equal("true", Property(project, "UseWinUI"));
        Assert.Equal("None", Property(project, "WindowsPackageType"));
        Assert.Equal("true", Property(project, "SelfContained"));
        Assert.Equal("win-x64", Property(project, "RuntimeIdentifier"));
        var windowsAppSdkSelfContained = project.Descendants("WindowsAppSDKSelfContained").Single();
        Assert.Equal("true", windowsAppSdkSelfContained.Value.Trim());
        Assert.Equal("'$(WindowsAppSDKSelfContained)' == ''", windowsAppSdkSelfContained.Parent?.Attribute("Condition")?.Value.Trim());
        Assert.Contains(
            project.Descendants("PackageReference").Select(reference => (string?)reference.Attribute("Include")),
            include => string.Equals(include, "Microsoft.WindowsAppSDK", StringComparison.Ordinal));
    }

    [Fact]
    public void Windows_adapter_targets_windows_and_uses_windows_app_sdk()
    {
        var project = LoadProject("src/ClipboardManager.Windows/ClipboardManager.Windows.csproj");

        Assert.StartsWith("net10.0-windows", Property(project, "TargetFramework"), StringComparison.Ordinal);
        Assert.Contains(
            project.Descendants("PackageReference").Select(reference => (string?)reference.Attribute("Include")),
            include => string.Equals(include, "Microsoft.WindowsAppSDK", StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_build_and_package_settings_exclude_the_legacy_wpf_test_project()
    {
        foreach (var fileName in new[] { "Directory.Build.props", "Directory.Packages.props" })
        {
            var text = File.ReadAllText(Path.Combine(RepositoryRoot, fileName));

            Assert.Contains("'$(MSBuildProjectName)' != 'ClipboardManager.Tests'", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Build_files_are_scoped_to_the_modern_src_and_tests_trees()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        Assert.Contains("\\src\\", props, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\tests\\", props, StringComparison.OrdinalIgnoreCase);

        var targets = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.targets"));
        Assert.Contains("'$(ModernSolutionProject)' == 'true'", targets, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_wpf_project_is_not_part_of_the_new_solution()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot, "ClipboardManager.sln"));

        Assert.DoesNotContain("ClipboardManager.csproj", solution, StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument LoadProject(string relativePath)
    {
        var absolutePath = Path.Combine(RepositoryRoot, relativePath);
        Assert.True(File.Exists(absolutePath), $"Expected project file to exist: {relativePath}");
        return XDocument.Load(absolutePath);
    }

    private static string Property(XDocument project, string propertyName)
    {
        var value = project.Descendants(propertyName).FirstOrDefault()?.Value.Trim();
        Assert.False(string.IsNullOrWhiteSpace(value), $"Expected project property: {propertyName}");
        return value!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if ((Directory.Exists(Path.Combine(directory.FullName, "tests")) &&
                 Directory.Exists(Path.Combine(directory.FullName, ".git"))) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
