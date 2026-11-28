using System.Windows;
using System.Windows.Media.Imaging;
using ClipboardManager.Services;

namespace ClipboardManager.Utilities;

internal static class RecordDragData
{
    internal static DataObject CreateText(string text, byte[]? qqRichText = null)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        if (qqRichText is { Length: > 0 })
            data.SetData(QqClipboardText.RichTextFormat, new MemoryStream(qqRichText, writable: false), autoConvert: false);
        return data;
    }

    internal static DataObject CreateImage(string imagePath, BitmapSource? bitmap)
    {
        var data = new DataObject();
        if (bitmap is not null) data.SetData(DataFormats.Bitmap, bitmap);
        data.SetData(DataFormats.FileDrop, new[] { imagePath });
        return data;
    }

    internal static bool HasExceededThreshold(Point start, Point current, double minimumHorizontal, double minimumVertical) =>
        Math.Abs(current.X - start.X) >= minimumHorizontal
        || Math.Abs(current.Y - start.Y) >= minimumVertical;
}
