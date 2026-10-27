using System.Reflection;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

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

    public static T Load<T>(string resourcePath, string? themePath = null)
    {
        string name = "ClipboardManager." + resourcePath.Replace('/', '.');
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"找不到界面资源：{resourcePath}");
        if (themePath is null) return (T)XamlReader.Load(stream);
        string themeName = "ClipboardManager." + themePath.Replace('/', '.');
        using var themeStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(themeName)
            ?? throw new InvalidOperationException($"找不到界面资源：{themePath}");
        var document = XDocument.Load(stream);
        var root = document.Root!;
        XNamespace ns = root.Name.Namespace;
        var resources = root.Element(ns + (root.Name.LocalName + ".Resources"));
        if (resources is null)
        {
            resources = new XElement(ns + (root.Name.LocalName + ".Resources"));
            root.AddFirst(resources);
        }
        // Loose XAML resolves StaticResource during parsing, before the view has a window parent.
        var dictionary = new XElement(ns + "ResourceDictionary",
            new XElement(ns + "ResourceDictionary.MergedDictionaries", XDocument.Load(themeStream).Root));
        dictionary.Add(resources.Nodes().ToArray());
        resources.ReplaceNodes(dictionary);
        using var reader = document.CreateReader();
        return (T)XamlReader.Load(reader);
    }
}
