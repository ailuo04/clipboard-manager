namespace ClipboardManager.Utilities;

public static class AppPaths
{
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipboardManager");

    public static string ImagesDirectory => Path.Combine(RootDirectory, "images");

    public static string HistoryFilePath => Path.Combine(RootDirectory, "history.json");

    public static string ThemeFilePath => Path.Combine(RootDirectory, "theme.json");

    public static string LogFilePath => Path.Combine(RootDirectory, "app.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ImagesDirectory);
    }
}
