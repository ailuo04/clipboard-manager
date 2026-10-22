using System.Windows.Media.Imaging;
using ClipboardManager.Models;

namespace ClipboardManager.Services;

public sealed class ClipboardSnapshot
{
    private ClipboardSnapshot(ClipboardRecordKind kind, string? text, BitmapSource? image, string source)
    {
        Kind = kind;
        Text = text;
        Image = image;
        Source = source;
    }

    public ClipboardRecordKind Kind { get; }

    public string? Text { get; }

    public BitmapSource? Image { get; }

    public string Source { get; }

    public DateTime CapturedAtUtc { get; } = DateTime.UtcNow;

    public static ClipboardSnapshot FromText(string text, string source = "UnicodeText")
    {
        return new ClipboardSnapshot(ClipboardRecordKind.Text, text, null, source);
    }

    public static ClipboardSnapshot FromImage(BitmapSource image, string source)
    {
        if (!image.IsFrozen)
        {
            image.Freeze();
        }

        return new ClipboardSnapshot(ClipboardRecordKind.Image, null, image, source);
    }
}
