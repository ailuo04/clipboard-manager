using ClipboardManager.Core.History;

namespace ClipboardManager.Core.Clipboard;

public sealed record ClipboardSnapshot(
    ClipboardContentKind Kind,
    string? Text,
    ReadOnlyMemory<byte> PngBytes,
    DateTime CapturedAtUtc)
{
    private DateTime _capturedAtUtc = ValidateUtc(CapturedAtUtc);

    public DateTime CapturedAtUtc
    {
        get => _capturedAtUtc;
        init => _capturedAtUtc = ValidateUtc(value);
    }

    private static DateTime ValidateUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Timestamp must be UTC.", nameof(CapturedAtUtc));
        return value;
    }
}
