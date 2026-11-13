using System.Reflection;
using System.Windows.Media.Imaging;

namespace ClipboardManager.Utilities;

public static class ViewLoader
{
    public static BitmapFrame LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClipboardManager.Assets.Clipboard.ico")
            ?? throw new InvalidOperationException("找不到应用图标资源。");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var icon = decoder.Frames.OrderByDescending(frame => (long)frame.PixelWidth * frame.PixelHeight).First();
        icon.Freeze();
        return icon;
    }
}
