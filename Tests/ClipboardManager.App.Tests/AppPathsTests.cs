using ClipboardManager.App.Utilities;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void Paths_keep_the_legacy_history_theme_and_images_locations_under_the_app_data_root()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "synthetic-clipboard-root"));

        Assert.Equal(Path.Combine(paths.RootDirectory, "history.json"), paths.LegacyHistoryFilePath);
        Assert.Equal(Path.Combine(paths.RootDirectory, "theme.json"), paths.LegacyThemeFilePath);
        Assert.Equal(Path.Combine(paths.RootDirectory, "images"), paths.ImagesDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "history.db"), paths.DatabasePath);
        Assert.DoesNotContain(paths.RootDirectory + Path.DirectorySeparatorChar, paths.UpdateStagingRoot, StringComparison.OrdinalIgnoreCase);
    }
}
