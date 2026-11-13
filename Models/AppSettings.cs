namespace ClipboardManager.Models;

public sealed class AppSettings
{
    public int MaxRecords { get; set; } = 500;

    public int MaxStorageMb { get; set; } = 512;

    public bool AutoCheckUpdates { get; set; } = true;

    public bool MinimizeToTray { get; set; } = false;

    public bool HideCapsulePreview { get; set; } = false;

    public double? WidgetLeft { get; set; }

    public double? WidgetTop { get; set; }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            MaxRecords = MaxRecords,
            MaxStorageMb = MaxStorageMb,
            AutoCheckUpdates = AutoCheckUpdates,
            MinimizeToTray = MinimizeToTray,
            HideCapsulePreview = HideCapsulePreview,
            WidgetLeft = WidgetLeft,
            WidgetTop = WidgetTop
        };
    }

    public void Normalize()
    {
        MaxRecords = Math.Clamp(MaxRecords, 50, 5000);
        MaxStorageMb = Math.Clamp(MaxStorageMb, 64, 4096);
    }
}
