using System.Windows;

namespace ClipboardManager.Views;

public partial class SharedResources : ResourceDictionary
{
    private static SharedResources? _instance;

    public SharedResources() => InitializeComponent();

    public static ResourceDictionary Instance => _instance ??= new SharedResources();
}
