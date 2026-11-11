using System.ComponentModel;
using ClipboardManager.Windows;

namespace ClipboardManager.Windows.Tray;

/// <summary>Optional notification-area icon and its open, pause/resume, and exit actions.</summary>
public sealed class SystemTrayService : IDisposable
{
    public const uint CallbackMessage = 0x8001;
    public const uint OpenCommand = 0x1002;
    public const uint PauseCommand = 0x1001;
    public const uint ExitCommand = 0x1003;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;

    private readonly WindowsMessageWindow _window;
    private bool _started;
    private bool _disposed;

    public SystemTrayService(WindowsMessageWindow window)
        => _window = window ?? throw new ArgumentNullException(nameof(window));

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? PauseChanged;
    public bool IsPaused { get; private set; }
    public bool IsEnabled => _started;

    public void Start(bool enabled = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!enabled || _started) return;
        _window.MessageReceived += OnMessage;
        if (!_window.AddTrayIcon(CallbackMessage))
        {
            _window.MessageReceived -= OnMessage;
            throw new Win32Exception(_window.Native.ErrorCode, "Could not create the notification-area icon.");
        }
        _started = true;
    }

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled) Start();
        else Stop();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _window.MessageReceived -= OnMessage;
        _window.RemoveTrayIcon();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        OpenRequested = null;
        ExitRequested = null;
        PauseChanged = null;
    }

    private void OnMessage(object? sender, WindowsMessage message)
    {
        if (!_started) return;
        if (message.Id == _window.Native.TaskbarCreatedMessage)
        {
            _window.AddTrayIcon(CallbackMessage);
            return;
        }
        if (message.Id != CallbackMessage) return;

        uint notification = unchecked((uint)message.LParam.ToInt64());
        if (notification == WmLeftButtonDoubleClick)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (notification is not (WmRightButtonUp or WmContextMenu)) return;

        switch (_window.ShowTrayMenu(IsPaused))
        {
            case OpenCommand:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case PauseCommand:
                IsPaused = !IsPaused;
                PauseChanged?.Invoke(this, IsPaused);
                break;
            case ExitCommand:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}