using System.Windows;

namespace ClipboardManager.Views;

public partial class SharedResources : ResourceDictionary
{
    public SharedResources() => InitializeComponent();

    /// <summary>
    /// Creates a fresh instance per call. WPF ResourceDictionary is thread-affine, so a
    /// shared static instance would throw when touched from a second UI thread.
    /// </summary>
    public static ResourceDictionary Create() => new SharedResources();
}
