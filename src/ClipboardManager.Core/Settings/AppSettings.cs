namespace ClipboardManager.Core.Settings;

public sealed record AppSettings(
    int MaxRecords,
    int MaxStorageMb,
    bool AlwaysOnTop,
    bool AutoCheckUpdates,
    bool MinimizeToTray,
    bool HideAfterCopy,
    string ThemePreference)
{
    public AppSettings Normalize() => this with
    {
        MaxRecords = Math.Clamp(MaxRecords, 50, 5000),
        MaxStorageMb = Math.Clamp(MaxStorageMb, 64, 4096)
    };
}
