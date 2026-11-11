using System.ComponentModel;
using ClipboardManager.Core.Clipboard;

namespace ClipboardManager.Windows.Clipboard;

/// <summary>Raises clipboard-change events only while the native format listener is registered.</summary>
public sealed class ClipboardChangeMonitor : IClipboardMonitor
{
    private const uint WmClipboardUpdate = 0x031D;
    private readonly WindowsMessageWindow _window;
    private bool _started;
    private bool _registered;
    private bool _disposed;

    public ClipboardChangeMonitor(WindowsMessageWindow window)
        => _window = window ?? throw new ArgumentNullException(nameof(window));

    public event EventHandler? ClipboardChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        if (_registered) Stop(); // Retry cleanup left by a previous partial or failed stop before re-registering.

        _window.MessageReceived += OnMessage;
        if (!_window.AddClipboardListener())
        {
            _window.MessageReceived -= OnMessage;
            throw new Win32Exception(_window.Native.ErrorCode, "Windows refused to register the clipboard listener.");
        }
        _registered = true;
        _started = true;
    }

    public void Stop()
    {
        if (!_registered) return;
        _started = false;
        _window.MessageReceived -= OnMessage;
        if (!_window.RemoveClipboardListener())
            throw new Win32Exception(_window.Native.ErrorCode, "Windows refused to remove the clipboard listener.");
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        ClipboardChanged = null;
    }

    private void OnMessage(object? sender, WindowsMessage message)
    {
        if (_started && message.Id == WmClipboardUpdate)
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
    }
}