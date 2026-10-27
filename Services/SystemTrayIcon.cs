using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using ClipboardManager.Utilities;

namespace ClipboardManager.Services;

internal sealed class SystemTrayIcon : IDisposable
{
    private static readonly uint TaskbarCreatedMessage = TrayNativeMethods.RegisterWindowMessage("TaskbarCreated");
    private const uint IconId = 1;

    private readonly Window _owner;
    private readonly ContextMenu _menu = new();
    private readonly MenuItem _pauseItem = new();
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private bool _isEnabled;
    private bool _isPaused;
    private bool _disposed;

    public SystemTrayIcon(Window owner)
    {
        _owner = owner;
        var openItem = new MenuItem { Header = "打开剪贴板" };
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _pauseItem.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(openItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exitItem);
        _menu.Opened += (_, _) => UpdatePauseItem();
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? ExitRequested;
    public bool IsEnabled => _isEnabled;

    public void SetEnabled(bool enabled, bool isPaused)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _isPaused = isPaused;
        if (enabled == _isEnabled)
        {
            if (_isEnabled) ModifyIcon();
            return;
        }

        if (enabled) AddIcon();
        else RemoveIcon();
    }

    public void UpdatePausedState(bool isPaused)
    {
        _isPaused = isPaused;
        UpdatePauseItem();
        if (_isEnabled) ModifyIcon();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveIcon();
        _menu.IsOpen = false;
    }

    private void AddIcon()
    {
        _windowHandle = new WindowInteropHelper(_owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException("无法创建系统托盘窗口句柄。");
        _source.AddHook(WndProc);
        try
        {
            _iconHandle = LoadIcon();
            var data = CreateData();
            if (!TrayNativeMethods.ShellNotifyIcon(TrayNativeMethods.NimAdd, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "添加系统托盘图标失败。");
            _isEnabled = true;
        }
        catch
        {
            _source.RemoveHook(WndProc);
            _source = null;
            _windowHandle = IntPtr.Zero;
            ReleaseIcon();
            throw;
        }
    }

    private void RemoveIcon()
    {
        if (_isEnabled && _windowHandle != IntPtr.Zero)
        {
            var data = CreateData();
            if (!TrayNativeMethods.ShellNotifyIcon(TrayNativeMethods.NimDelete, ref data))
                AppLogger.LogException("移除系统托盘图标失败", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        _source?.RemoveHook(WndProc);
        _source = null;
        _windowHandle = IntPtr.Zero;
        _isEnabled = false;
        ReleaseIcon();
    }

    private void ModifyIcon()
    {
        var data = CreateData();
        if (!TrayNativeMethods.ShellNotifyIcon(TrayNativeMethods.NimModify, ref data))
            AppLogger.LogException("更新系统托盘图标状态失败", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private TrayNativeMethods.NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<TrayNativeMethods.NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = IconId,
        Flags = TrayNativeMethods.NifMessage | TrayNativeMethods.NifIcon | TrayNativeMethods.NifTip,
        CallbackMessage = TrayNativeMethods.TrayCallbackMessage,
        IconHandle = _iconHandle,
        Tip = _isPaused ? "剪贴板 - 监听已暂停" : "剪贴板 - 正在后台监听",
        State = 0,
        StateMask = 0,
        Info = string.Empty,
        TimeoutOrVersion = 0,
        InfoTitle = string.Empty,
        InfoFlags = 0,
        GuidItem = Guid.Empty,
        BalloonIcon = IntPtr.Zero
    };

    private void UpdatePauseItem() => _pauseItem.Header = _isPaused ? "恢复监听" : "暂停监听";

    private void ShowMenu()
    {
        TrayNativeMethods.SetForegroundWindow(_windowHandle);
        _menu.PlacementTarget = _owner;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == TaskbarCreatedMessage && _isEnabled)
        {
            var data = CreateData();
            if (!TrayNativeMethods.ShellNotifyIcon(TrayNativeMethods.NimAdd, ref data))
                AppLogger.LogException("Windows 重启任务栏后恢复系统托盘图标失败", new Win32Exception(Marshal.GetLastWin32Error()));
            handled = true;
        }
        else if (message == TrayNativeMethods.TrayCallbackMessage)
        {
            switch (lParam.ToInt32())
            {
                case TrayNativeMethods.WmLeftButtonUp:
                case TrayNativeMethods.WmLeftButtonDoubleClick:
                    handled = true;
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case TrayNativeMethods.WmRightButtonUp:
                case TrayNativeMethods.WmContextMenu:
                    handled = true;
                    ShowMenu();
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private static IntPtr LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClipboardManager.Assets.Clipboard.ico")
            ?? throw new InvalidOperationException("找不到系统托盘图标资源。");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] iconFile = buffer.ToArray();
        if (iconFile.Length < 22 || BitConverter.ToUInt16(iconFile, 0) != 0 || BitConverter.ToUInt16(iconFile, 2) != 1)
            throw new InvalidDataException("系统托盘图标文件格式不正确。");

        int count = BitConverter.ToUInt16(iconFile, 4);
        int selectedOffset = -1;
        int selectedSize = 0;
        int selectedScore = int.MaxValue;
        for (int index = 0; index < count; index++)
        {
            int entry = 6 + index * 16;
            if (entry + 16 > iconFile.Length) break;
            int width = iconFile[entry] == 0 ? 256 : iconFile[entry];
            int height = iconFile[entry + 1] == 0 ? 256 : iconFile[entry + 1];
            int score = Math.Abs(width - 32) + Math.Abs(height - 32);
            if (score >= selectedScore) continue;
            int size = checked((int)BitConverter.ToUInt32(iconFile, entry + 8));
            int offset = checked((int)BitConverter.ToUInt32(iconFile, entry + 12));
            if (size <= 0 || offset < 0 || offset > iconFile.Length - size) continue;
            selectedOffset = offset;
            selectedSize = size;
            selectedScore = score;
        }
        if (selectedOffset < 0) throw new InvalidDataException("系统托盘图标不包含有效图像。");

        byte[] image = iconFile.AsSpan(selectedOffset, selectedSize).ToArray();
        IntPtr icon = TrayNativeMethods.CreateIconFromResourceEx(image, (uint)image.Length, true,
            TrayNativeMethods.IconResourceVersion, 0, 0, 0);
        if (icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "读取系统托盘图标失败。");
        return icon;
    }

    private void ReleaseIcon()
    {
        if (_iconHandle == IntPtr.Zero) return;
        if (!TrayNativeMethods.DestroyIcon(_iconHandle))
            AppLogger.LogException("释放系统托盘图标资源失败", new Win32Exception(Marshal.GetLastWin32Error()));
        _iconHandle = IntPtr.Zero;
    }
}
