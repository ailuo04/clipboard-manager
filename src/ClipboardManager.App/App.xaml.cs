using Microsoft.UI.Xaml;

namespace ClipboardManager.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window ??= new Window();
        _window.Activate();
    }
}
