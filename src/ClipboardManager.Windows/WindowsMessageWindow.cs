using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClipboardManager.Windows;

public delegate IntPtr NativeWindowProcedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

public readonly record struct WindowsMessage(uint Id, IntPtr WParam, IntPtr LParam);

/// <summary>Injectable boundary for the hidden window, Win32 lifecycle notifications, tray, and hot key APIs.</summary>
public interface IWindowsMessageNativeApi
{
    int ErrorCode { get; }
    uint TaskbarCreatedMessage { get; }
    IntPtr CreateHiddenMessageWindow(NativeWindowProcedure procedure);
    bool DestroyMessageOnlyWindow(IntPtr handle);
    IntPtr DefWindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    void RunMessageLoop();
    void QuitMessageLoop();
    bool AddClipboardFormatListener(IntPtr handle);
    bool RemoveClipboardFormatListener(IntPtr handle);
    bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey);
    bool UnregisterHotKey(IntPtr handle, int id);
    bool AddTrayIcon(IntPtr handle, uint callbackMessage);
    bool RemoveTrayIcon(IntPtr handle);
    uint ShowTrayMenu(IntPtr handle, bool isPaused);
}

/// <summary>Owns a dedicated STA hidden window and routes native messages to subscribers.</summary>
public sealed class WindowsMessageWindow : IDisposable
{
    public const uint WmSettingChange = 0x001A;
    public const uint WmThemeChanged = 0x031A;

    private readonly IWindowsMessageNativeApi _native;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly Action<Action>? _dispatchToUi;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<IntPtr> _windowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _handle;
    private int _disposed;

    public WindowsMessageWindow(IWindowsMessageNativeApi? native = null, SynchronizationContext? synchronizationContext = null, Action<Action>? dispatchToUi = null)
    {
        _native = native ?? new WindowsMessageNativeApi();
        _synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
        _dispatchToUi = dispatchToUi;
        _thread = new Thread(RunMessageThread) { IsBackground = true, Name = "ClipboardManager Windows Message STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Handle = _windowReady.Task.GetAwaiter().GetResult();
    }

    public event EventHandler<WindowsMessage>? MessageReceived;

    public IntPtr Handle { get; private set; }

    internal IWindowsMessageNativeApi Native => _native;

    public bool AddClipboardListener() => _native.AddClipboardFormatListener(Handle);
    public bool RemoveClipboardListener() => _native.RemoveClipboardFormatListener(Handle);
    public bool RegisterHotKey(int id, uint modifiers, uint virtualKey) => _native.RegisterHotKey(Handle, id, modifiers, virtualKey);
    public bool UnregisterHotKey(int id) => _native.UnregisterHotKey(Handle, id);
    public bool AddTrayIcon(uint callbackMessage) => _native.AddTrayIcon(Handle, callbackMessage);
    public bool RemoveTrayIcon() => _native.RemoveTrayIcon(Handle);
    public uint ShowTrayMenu(bool isPaused) => _native.ShowTrayMenu(Handle, isPaused);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _native.QuitMessageLoop();
        if (Thread.CurrentThread != _thread) _thread.Join();
        MessageReceived = null;
    }

    private void RunMessageThread()
    {
        try
        {
            IntPtr handle = _native.CreateHiddenMessageWindow(DispatchNativeMessage);
            if (handle == IntPtr.Zero) throw new Win32Exception(_native.ErrorCode, "Could not create the hidden message window.");
            _handle = handle;
            Handle = handle;
            _windowReady.TrySetResult(handle);
            _native.RunMessageLoop();
        }
        catch (Exception exception)
        {
            _windowReady.TrySetException(exception);
        }
        finally
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            Handle = IntPtr.Zero;
            if (handle != IntPtr.Zero) _native.DestroyMessageOnlyWindow(handle);
        }
    }

    private IntPtr DispatchNativeMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Volatile.Read(ref _disposed) != 0) return _native.DefWindowProc(handle, message, wParam, lParam);
        var value = new WindowsMessage(message, wParam, lParam);
        void RaiseMessage()
        {
            if (Volatile.Read(ref _disposed) == 0)
                MessageReceived?.Invoke(this, value);
        }
        if (_dispatchToUi is not null)
        {
            try { _dispatchToUi(RaiseMessage); }
            catch (Exception) { /* Ignore a shutting-down UI dispatcher without terminating the native message loop. */ }
        }
        else if (_synchronizationContext is not null)
        {
            _synchronizationContext.Post(static state => ((Action)state!).Invoke(), (Action)RaiseMessage);
        }
        else
        {
            RaiseMessage();
        }
        return _native.DefWindowProc(handle, message, wParam, lParam);
    }
}

/// <summary>Win32 implementation. A hidden top-level window is used so Explorer broadcasts reach the tray service.</summary>
public sealed class WindowsMessageNativeApi : IWindowsMessageNativeApi
{
    private const uint WmQuit = 0x0012;
    private const uint WmNcCreate = 0x0081;
    private const uint WmNcDestroy = 0x0082;
    private const int GwlpUserData = -21;
    
    private readonly string _windowClassName = $"ClipboardManager.MessageWindow.{Guid.NewGuid():N}";
    private static readonly WindowProc StaticWindowProc = WindowProcThunk;
    private bool _classRegistered;
    private static uint _taskbarCreatedMessage;
    private uint _threadId;

    public int ErrorCode => Marshal.GetLastWin32Error();
    public uint TaskbarCreatedMessage => _taskbarCreatedMessage;

    public IntPtr CreateHiddenMessageWindow(NativeWindowProcedure procedure)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        EnsureWindowClass();
        _threadId = GetCurrentThreadId();
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Ensure a thread queue exists before WM_QUIT is posted.
        var callback = GCHandle.Alloc(procedure);
        const uint WsPopup = 0x80000000;
        const uint WsExToolWindow = 0x00000080;
        const uint WsExNoActivate = 0x08000000;
        IntPtr handle = CreateWindowEx(WsExToolWindow | WsExNoActivate, _windowClassName, string.Empty, WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), GCHandle.ToIntPtr(callback));
        if (handle == IntPtr.Zero)
        {
            if (callback.IsAllocated) callback.Free();
            return IntPtr.Zero;
        }
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMessage == 0)
        {
            int error = Marshal.GetLastWin32Error();
            DestroyWindow(handle);
            throw new Win32Exception(error, "Could not register the Explorer restart message.");
        }
        return handle;
    }

    public bool DestroyMessageOnlyWindow(IntPtr handle) => DestroyWindow(handle);

    public IntPtr DefWindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
        => DefWindowProcNative(handle, message, wParam, lParam);

    public void RunMessageLoop()
    {
        _threadId = GetCurrentThreadId();
        while (true)
        {
            int result = GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0);
            if (result == 0) return;
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The hidden window message loop failed.");
            TranslateMessage(in message);
            DispatchMessage(in message);
        }
    }

    public void QuitMessageLoop()
    {
        uint threadId = Volatile.Read(ref _threadId);
        if (threadId != 0) PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    public bool AddClipboardFormatListener(IntPtr handle) => NativeAddClipboardFormatListener(handle);
    public bool RemoveClipboardFormatListener(IntPtr handle) => NativeRemoveClipboardFormatListener(handle);
    public bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey) => NativeRegisterHotKey(handle, id, modifiers, virtualKey);
    public bool UnregisterHotKey(IntPtr handle, int id) => NativeUnregisterHotKey(handle, id);

    public bool AddTrayIcon(IntPtr handle, uint callbackMessage)
    {
        var data = CreateNotifyIconData(handle, callbackMessage);
        bool added = ShellNotifyIcon(0, ref data);
        if (!added) return false;
        data.Version = 4;
        ShellNotifyIcon(4, ref data);
        return true;
    }

    public bool RemoveTrayIcon(IntPtr handle)
    {
        var data = CreateNotifyIconData(handle, 0);
        return ShellNotifyIcon(2, ref data);
    }

    public uint ShowTrayMenu(IntPtr handle, bool isPaused)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the tray menu.");
        try
        {
            AppendMenu(menu, 0, 0x1001, isPaused ? "恢复采集" : "暂停采集");
            AppendMenu(menu, 0, 0x1002, "打开主窗口");
            AppendMenu(menu, 0x0800, 0, string.Empty);
            AppendMenu(menu, 0, 0x1003, "退出");
            GetCursorPos(out NativePoint point);
            SetForegroundWindow(handle);
            return TrackPopupMenu(menu, 0x0100 | 0x0002 | 0x0004, point.X, point.Y, 0, handle, IntPtr.Zero);
        }
        finally { DestroyMenu(menu); }
    }

    private void EnsureWindowClass()
    {
        if (_classRegistered) return;
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(StaticWindowProc),
            Instance = GetModuleHandle(null),
            ClassName = _windowClassName
        };
        if (RegisterClassEx(ref windowClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the hidden window class.");
        _classRegistered = true;
    }

    private static IntPtr WindowProcThunk(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        GCHandle callback = default;
        if (message == WmNcCreate)
        {
            var create = Marshal.PtrToStructure<CreateStruct>(lParam);
            callback = GCHandle.FromIntPtr(create.CreateParameters);
            SetWindowLongPtr(handle, GwlpUserData, create.CreateParameters);
        }
        else
        {
            IntPtr pointer = GetWindowLongPtr(handle, GwlpUserData);
            if (pointer != IntPtr.Zero) callback = GCHandle.FromIntPtr(pointer);
        }

        try
        {
            if (callback.IsAllocated && callback.Target is NativeWindowProcedure procedure)
                return procedure(handle, message, wParam, lParam);
            return DefWindowProcNative(handle, message, wParam, lParam);
        }
        finally
        {
            if (message == WmNcDestroy)
            {
                IntPtr pointer = GetWindowLongPtr(handle, GwlpUserData);
                SetWindowLongPtr(handle, GwlpUserData, IntPtr.Zero);
                if (pointer != IntPtr.Zero)
                {
                    var root = GCHandle.FromIntPtr(pointer);
                    if (root.IsAllocated) root.Free();
                }
            }
        }
    }

    private static NotifyIconData CreateNotifyIconData(IntPtr handle, uint callbackMessage)
        => new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = handle,
            Id = 1,
            Flags = 0x0001 | 0x0002 | 0x0004,
            CallbackMessage = callbackMessage,
            Icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)),
            Tip = "剪贴板管理器"
        };

    private delegate IntPtr WindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateStruct
    {
        public IntPtr CreateParameters;
        public IntPtr Instance;
        public IntPtr Menu;
        public IntPtr Parent;
        public int Height;
        public int Width;
        public int Y;
        public int X;
        public int Style;
        public IntPtr Name;
        public IntPtr Class;
        public uint ExStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProcNative(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out NativeMessage message, IntPtr handle, uint min, uint max);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, IntPtr handle, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(in NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(in NativeMessage message);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool NativeAddClipboardFormatListener(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool NativeRemoveClipboardFormatListener(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool NativeRegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool NativeUnregisterHotKey(IntPtr handle, int id);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint action, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr handle, IntPtr rect);
}