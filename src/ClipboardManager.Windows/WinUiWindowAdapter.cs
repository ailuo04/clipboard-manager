using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ClipboardManager.Windows;

public readonly record struct WindowSizeDip(double Width, double Height);

public enum WindowBackdropStyle
{
    None = 1,
    Mica = 2,
    Acrylic = 3,
    Tabbed = 4
}

/// <summary>Small HWND-only WinUI shell adapter for window state, backdrop, and DPI-aware sizing.</summary>
public sealed class WinUiWindowAdapter
{
    private const uint DwmwaSystemBackdropType = 38;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    public WinUiWindowAdapter(Window window) : this(WindowNative.GetWindowHandle(window)) { }

    public WinUiWindowAdapter(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new ArgumentException("A valid window handle is required.", nameof(handle));
        Handle = handle;
    }

    public IntPtr Handle { get; }

    public void SetAlwaysOnTop(bool enabled)
    {
        if (!SetWindowPos(Handle, enabled ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate))
            throw LastError("Could not change the window's topmost state.");
    }

    public void Minimize() => ShowWindow(Handle, 6);
    public void Hide() => ShowWindow(Handle, 0);
    public void Restore() => ShowWindow(Handle, 9);

    public WindowSizeDip GetWindowSizeDip()
    {
        if (!GetWindowRect(Handle, out NativeRect rect)) throw LastError("Could not read the window size.");
        double scale = 96d / GetDpi();
        return new WindowSizeDip((rect.Right - rect.Left) * scale, (rect.Bottom - rect.Top) * scale);
    }

    public void SetWindowSizeDip(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Window dimensions must be finite positive DIP values.");
        uint dpi = GetDpi();
        int pixelWidth = checked((int)Math.Round(width * dpi / 96d, MidpointRounding.AwayFromZero));
        int pixelHeight = checked((int)Math.Round(height * dpi / 96d, MidpointRounding.AwayFromZero));
        if (!SetWindowPos(Handle, IntPtr.Zero, 0, 0, pixelWidth, pixelHeight,
                SwpNoMove | SwpNoActivate))
            throw LastError("Could not resize the window.");
    }

    public bool TrySetBackdrop(WindowBackdropStyle style)
    {
        int value = (int)style;
        return DwmSetWindowAttribute(Handle, DwmwaSystemBackdropType, ref value, sizeof(int)) == 0;
    }

    private uint GetDpi()
    {
        uint dpi = GetDpiForWindow(Handle);
        return dpi == 0 ? 96 : dpi;
    }

    private static System.ComponentModel.Win32Exception LastError(string message)
        => new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("dwmapi.dll", SetLastError = true)] private static extern int DwmSetWindowAttribute(IntPtr handle, uint attribute, ref int value, int size);
}