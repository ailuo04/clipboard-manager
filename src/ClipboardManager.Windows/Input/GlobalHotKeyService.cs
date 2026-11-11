using System.ComponentModel;
using ClipboardManager.Windows;

namespace ClipboardManager.Windows.Input;

public sealed class GlobalHotKeyService : IDisposable
{
    public const int HotKeyId = 0x434D;
    private const uint WmHotKey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;
    private const int ErrorHotKeyAlreadyRegistered = 1409;

    private readonly WindowsMessageWindow _window;
    private bool _registered;
    private bool _disposed;

    public GlobalHotKeyService(WindowsMessageWindow window)
        => _window = window ?? throw new ArgumentNullException(nameof(window));

    public event EventHandler? Activated;

    public bool TryStart(out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered) { error = null; return true; }
        _window.MessageReceived += OnMessage;
        if (_window.RegisterHotKey(HotKeyId, ModControl | ModShift | ModNoRepeat, VkSpace))
        {
            _registered = true;
            error = null;
            return true;
        }

        _window.MessageReceived -= OnMessage;
        error = _window.Native.ErrorCode == ErrorHotKeyAlreadyRegistered
            ? "快捷键 Ctrl+Shift+Space 已被其他程序占用。"
            : $"快捷键 Ctrl+Shift+Space 注册失败（Windows 错误码 {_window.Native.ErrorCode}）。";
        return false;
    }

    public void Stop()
    {
        if (!_registered) return;
        _registered = false;
        _window.MessageReceived -= OnMessage;
        _window.UnregisterHotKey(HotKeyId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        Activated = null;
    }

    private void OnMessage(object? sender, WindowsMessage message)
    {
        if (_registered && message.Id == WmHotKey && message.WParam.ToInt32() == HotKeyId)
            Activated?.Invoke(this, EventArgs.Empty);
    }
}