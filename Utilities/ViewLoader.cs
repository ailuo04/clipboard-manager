using System.Reflection;
using System.Windows.Markup;
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

    public static T Load<T>(string resourcePath)
    {
        string name = "ClipboardManager." + resourcePath.Replace('/', '.');
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"找不到界面资源：{resourcePath}");
        return (T)XamlReader.Load(stream);
    }
}
