namespace ClipboardManager.Models;

public sealed class AppSettings
{
    public int MaxRecords { get; set; } = 500;

    public int MaxStorageMb { get; set; } = 512;

    public bool AlwaysOnTop { get; set; } = false;

    public bool AutoCheckUpdates { get; set; } = true;

    public bool MinimizeToTray { get; set; } = false;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            MaxRecords = MaxRecords,
            MaxStorageMb = MaxStorageMb,
            AlwaysOnTop = AlwaysOnTop,
            AutoCheckUpdates = AutoCheckUpdates,
            MinimizeToTray = MinimizeToTray
        };
    }

    public void Normalize()
    {
        MaxRecords = Math.Clamp(MaxRecords, 50, 5000);
        MaxStorageMb = Math.Clamp(MaxStorageMb, 64, 4096);
    }
}
