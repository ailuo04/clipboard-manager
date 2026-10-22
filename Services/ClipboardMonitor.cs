using System.Windows;
using System.Windows.Interop;
using ClipboardManager.Utilities;

namespace ClipboardManager.Services;

public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _isStarted;
    private bool _isWaiting;
    private bool _disposed;
    private IntPtr _handle;

    public ClipboardMonitor(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public event EventHandler? ClipboardChanged;

    public bool IsStarted => _isStarted;

    public IntPtr Handle => _handle;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isStarted || _isWaiting)
        {
            return;
        }

        if (new WindowInteropHelper(_window).Handle == IntPtr.Zero)
        {
            _isWaiting = true;
            _window.SourceInitialized += OnWindowSourceInitialized;
            return;
        }

        Attach();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnWindowSourceInitialized;
        _isWaiting = false;
        Attach();
    }

    private void Attach()
    {
        if (_isStarted)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            throw new InvalidOperationException("无法创建剪贴板监听窗口句柄。");
        }

        NativeMethods.Check(NativeMethods.AddClipboardFormatListener(handle), "注册剪贴板监听失败");
        _source.AddHook(WndProc);
        _handle = handle;
        _isStarted = true;
    }

    private void Stop()
    {
        if (!_isStarted)
        {
            _window.SourceInitialized -= OnWindowSourceInitialized;
            _isWaiting = false;
            return;
        }

        _source?.RemoveHook(WndProc);
        if (!NativeMethods.RemoveClipboardFormatListener(_handle))
            AppLogger.LogException("注销剪贴板监听失败", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        _source = null;
        _handle = IntPtr.Zero;
        _isStarted = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate)
        {
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

}
