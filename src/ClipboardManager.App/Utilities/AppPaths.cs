using System.Diagnostics;

namespace ClipboardManager.App.Utilities;

/// <summary>Provides the stable per-user data paths, including the legacy JSON migration inputs.</summary>
public sealed class AppPaths
{
    private readonly string? _updateStagingRootOverride;

    public AppPaths(string? rootDirectory = null, string? updateStagingRoot = null)
    {
        string root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardManager");
        RootDirectory = Path.GetFullPath(root);
        _updateStagingRootOverride = updateStagingRoot is null ? null : Path.GetFullPath(updateStagingRoot);
    }

    public string RootDirectory { get; }
    public string ImagesDirectory => Path.Combine(RootDirectory, "images");
    public string DatabasePath => Path.Combine(RootDirectory, "history.db");
    public string LegacyHistoryFilePath => Path.Combine(RootDirectory, "history.json");
    public string LegacyThemeFilePath => Path.Combine(RootDirectory, "theme.json");
    public string LogFilePath => Path.Combine(RootDirectory, "app.log");
    public string MigrationBackupDirectory => Path.Combine(RootDirectory, ".migration-backups");
    public string MigrationJournalPath => Path.Combine(RootDirectory, ".migration-journal.json");
    public string MigrationCompletionPath => Path.Combine(RootDirectory, ".migration-complete.json");
    /// <summary>Update payloads are staged outside the application data directory.</summary>
    public string UpdateStagingRoot => _updateStagingRootOverride ?? Path.Combine(Path.GetDirectoryName(RootDirectory) ?? RootDirectory, "ClipboardManager.Update");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ImagesDirectory);
    }

    public void OpenDataDirectory()
    {
        EnsureDirectories();
        Process.Start(new ProcessStartInfo(RootDirectory) { UseShellExecute = true });
    }

    public void OpenLog()
    {
        if (!File.Exists(LogFilePath))
            throw new FileNotFoundException("The application log does not exist yet.", LogFilePath);
        Process.Start(new ProcessStartInfo(LogFilePath) { UseShellExecute = true });
    }
}



