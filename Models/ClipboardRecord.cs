namespace ClipboardManager.Models;

public sealed record ClipboardRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public ClipboardRecordKind Kind { get; set; }

    public string? Text { get; set; }

    public string? ImageFileName { get; set; }

    public long ByteLength { get; set; }

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public DateTime CopiedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsPinned { get; set; }
}
