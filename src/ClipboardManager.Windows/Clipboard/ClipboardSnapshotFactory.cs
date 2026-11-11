using System.Text;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using ClipboardManager.Windows.Images;

namespace ClipboardManager.Windows.Clipboard;

public static class ClipboardSnapshotFactory
{
    public static ClipboardSnapshot FromImage(CanonicalImage image, DateTime capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (capturedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Capture time must be UTC.", nameof(capturedAtUtc));
        return new ClipboardSnapshot(ClipboardContentKind.Image, null, image.PngBytes, capturedAtUtc)
        {
            ImageContentHash = image.ContentHash,
            ImageWidth = image.Width,
            ImageHeight = image.Height
        };
    }

    public static ClipboardSnapshot? FromText(string? text, DateTime capturedAtUtc)
    {
        if (capturedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Capture time must be UTC.", nameof(capturedAtUtc));
        if (string.IsNullOrEmpty(text)) return null;
        WindowsImageCodec.ValidateTextSize(Encoding.Unicode.GetByteCount(text));
        return new ClipboardSnapshot(ClipboardContentKind.Text, text, ReadOnlyMemory<byte>.Empty, capturedAtUtc);
    }
}
