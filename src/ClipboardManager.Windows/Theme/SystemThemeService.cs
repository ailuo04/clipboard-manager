using Microsoft.Win32;
using ClipboardManager.Windows;

namespace ClipboardManager.Windows.Theme;

/// <summary>Observes the user's Windows app theme preference without coupling Core to the registry.</summary>
public sealed class SystemThemeService : IDisposable
{
    private readonly WindowsMessageWindow _window;
    private readonly Func<bool> _readIsLightTheme;
    private bool _started;
    private bool _disposed;

    public SystemThemeService(WindowsMessageWindow window, Func<bool>? readIsLightTheme = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _readIsLightTheme = readIsLightTheme ?? ReadIsLightTheme;
    }

    public event EventHandler<bool>? ThemeChanged;
    public bool? IsLightTheme { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        _window.MessageReceived += OnMessage;
        RefreshTheme(forcePublish: true);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _window.MessageReceived -= OnMessage;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        ThemeChanged = null;
    }

    private void OnMessage(object? sender, WindowsMessage message)
    {
        if (_started && message.Id is WindowsMessageWindow.WmSettingChange or WindowsMessageWindow.WmThemeChanged)
            RefreshTheme(forcePublish: false);
    }

    private void RefreshTheme(bool forcePublish)
    {
        bool isLight;
        try { isLight = _readIsLightTheme(); }
        catch (UnauthorizedAccessException) { return; }
        catch (System.Security.SecurityException) { return; }
        if (!forcePublish && IsLightTheme == isLight) return;
        IsLightTheme = isLight;
        ThemeChanged?.Invoke(this, isLight);
    }

    private static bool ReadIsLightTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}