namespace ClipboardManager.Models;

public sealed class HistoryDocument
{
    public int Version { get; set; } = 1;

    public List<ClipboardRecord> Records { get; set; } = [];

    public AppSettings Settings { get; set; } = new();

    public List<string> PendingImageDeletes { get; set; } = [];
}
