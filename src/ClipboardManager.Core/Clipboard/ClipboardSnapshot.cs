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

    /// <summary>
    /// Optional legacy-compatible SHA256 of little-endian Int32 width/height followed by normalized BGRA32 pixels.
    /// Supplying it keeps deduplication stable across different PNG encodings.
    /// </summary>
    public string? ImageContentHash { get; init; }

    /// <summary>Pixel dimensions reported by the canonical image codec; zero means derive them from IHDR.</summary>
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }

    private static DateTime ValidateUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Timestamp must be UTC.", nameof(CapturedAtUtc));
        return value;
    }
}
