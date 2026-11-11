using System.Diagnostics;
using ClipboardManager.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ClipboardManager.App;

public interface IAppWindowService
{
    Task ShowMainWindowAsync(CancellationToken cancellationToken);
    Task WaitUntilLoadedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    void ApplySettings(AppSettings settings, bool isLightTheme);
    void ApplyTheme(string preference, bool isLightTheme);
    void HideAfterCopyIfEnabled();
    void NotifyHistoryChanged();
    Task OpenDataDirectoryAsync(string path, CancellationToken cancellationToken);
    void RequestExit();
}

public sealed class WinUiAppWindowService : IAppWindowService
{
    private readonly Func<Window> _createWindow;
    private readonly DispatcherQueue? _dispatcher;
    private Window? _window;
    private AppSettings _settings = new(500, 512, false, true, false, true, "System");
    private bool _isLightTheme = true;
    private IntPtr _previousForegroundWindow;

    public WinUiAppWindowService(Func<Window> createWindow)
    {
        _createWindow = createWindow ?? throw new ArgumentNullException(nameof(createWindow));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public Task ShowMainWindowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr foreground = GetForegroundWindow();
        if (_window is null)
        {
            if (foreground != IntPtr.Zero) _previousForegroundWindow = foreground;
            _window = _createWindow();
            ApplySettings(_settings, _isLightTheme);
        }
        else
        {
            IntPtr ownHandle = WindowNative.GetWindowHandle(_window);
            if (foreground != IntPtr.Zero && foreground != ownHandle) _previousForegroundWindow = foreground;
        }
        if (_window is MainWindow mainWindow) mainWindow.ShowWindow();
        _window.Activate();
        return Task.CompletedTask;
    }

    public Task WaitUntilLoadedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_window is not MainWindow mainWindow)
            throw new InvalidOperationException("The main window must be shown before startup health can be confirmed.");
        return mainWindow.WaitUntilLoadedAsync().WaitAsync(cancellationToken);
    }
    public void ApplySettings(AppSettings settings, bool isLightTheme)
    {
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();
        _isLightTheme = isLightTheme;
        ApplyTheme(_settings.ThemePreference, isLightTheme);
        if (_window is MainWindow mainWindow) mainWindow.ApplyWindowSettings(_settings);
    }

    public void ApplyTheme(string preference, bool isLightTheme)
    {
        _settings = _settings with { ThemePreference = preference };
        _isLightTheme = isLightTheme;
        ElementTheme requestedTheme = preference switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        if (_window?.Content is FrameworkElement root) root.RequestedTheme = requestedTheme;
    }

    public void HideAfterCopyIfEnabled()
    {
        if (!_settings.HideAfterCopy || _window is not MainWindow mainWindow) return;
        IntPtr ownHandle = WindowNative.GetWindowHandle(_window);
        mainWindow.HideWindow();
        if (_previousForegroundWindow != IntPtr.Zero && _previousForegroundWindow != ownHandle)
            SetForegroundWindow(_previousForegroundWindow);
    }

    public void NotifyHistoryChanged()
    {
        void Refresh()
        {
            if (_window is MainWindow mainWindow) mainWindow.RefreshCurrentHistory();
        }
        if (_dispatcher is null) return;
        if (_dispatcher.HasThreadAccess) Refresh();
        else _dispatcher.TryEnqueue(Refresh);
    }

    public Task OpenDataDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        _ = Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true })
            ?? throw new InvalidOperationException("The data directory could not be opened.");
        return Task.CompletedTask;
    }

    public void RequestExit()
    {
        if (_window is MainWindow mainWindow) mainWindow.RequestExit();
        else _window?.Close();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}


