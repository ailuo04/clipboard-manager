using System.Reflection;
using System.Windows.Media.Imaging;

namespace ClipboardManager.Utilities;

public static class ViewLoader
{
    public static BitmapFrame LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClipboardManager.Assets.Clipboard.ico")
            ?? throw new InvalidOperationException("找不到应用图标资源。");
        var icon = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        icon.Freeze();
        return icon;
    }
}
