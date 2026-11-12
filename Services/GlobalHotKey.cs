using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClipboardManager.Utilities;

namespace ClipboardManager.Services;

internal sealed class GlobalHotKey : IDisposable
{
    private const int HotKeyId = 0x434D;
    private const int WmHotKey = 0x0312;
    private const uint Modifiers = 0x0002 | 0x0004 | 0x4000; // Control, Shift, no repeat.
    private const uint VkSpace = 0x20;
    private readonly Window _window;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;
    private bool _disposed;

    public GlobalHotKey(Window window) => _window = window;

    public event EventHandler? Pressed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered) return;
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("无法获取全局快捷键窗口句柄。");
        _source.AddHook(WndProc);
        try
        {
            NativeMethods.Check(NativeMethods.RegisterHotKey(_handle, HotKeyId, Modifiers, VkSpace), "注册 Ctrl+Shift+Space 失败");
            _registered = true;
        }
        catch
        {
            _source.RemoveHook(WndProc);
            _source = null;
            _handle = IntPtr.Zero;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered && !NativeMethods.UnregisterHotKey(_handle, HotKeyId))
            AppLogger.LogException("注销全局快捷键失败", new Win32Exception(Marshal.GetLastWin32Error()));
        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = IntPtr.Zero;
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }
}
