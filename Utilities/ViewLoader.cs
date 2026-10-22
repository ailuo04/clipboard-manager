using System.Reflection;
using System.Windows.Markup;

namespace ClipboardManager.Utilities;

public static class ViewLoader
{
    public static T Load<T>(string resourcePath)
    {
        string name = "ClipboardManager." + resourcePath.Replace('/', '.');
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"找不到界面资源：{resourcePath}");
        return (T)XamlReader.Load(stream);
    }
}
