namespace ClipboardManager.Models;

public sealed class AppSettings
{
    public int MaxRecords { get; set; } = 500;

    public int MaxStorageMb { get; set; } = 512;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            MaxRecords = MaxRecords,
            MaxStorageMb = MaxStorageMb
        };
    }

    public void Normalize()
    {
        MaxRecords = Math.Clamp(MaxRecords, 50, 5000);
        MaxStorageMb = Math.Clamp(MaxStorageMb, 64, 4096);
    }
}
