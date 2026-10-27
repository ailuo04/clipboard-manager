using System.Runtime.InteropServices;

namespace ClipboardManager.Services;

internal static class TrayNativeMethods
{
    internal const uint NimAdd = 0x00000000;
    internal const uint NimModify = 0x00000001;
    internal const uint NimDelete = 0x00000002;
    internal const uint NifMessage = 0x00000001;
    internal const uint NifIcon = 0x00000002;
    internal const uint NifTip = 0x00000004;
    internal const int WmLeftButtonUp = 0x0202;
    internal const int WmLeftButtonDoubleClick = 0x0203;
    internal const int WmRightButtonUp = 0x0205;
    internal const int WmContextMenu = 0x007B;
    internal const uint IconResourceVersion = 0x00030000;
    internal const int TrayCallbackMessage = 0x8001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        internal uint Size;
        internal IntPtr WindowHandle;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid GuidItem;
        internal IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateIconFromResourceEx(
        [In] byte[] resourceBits, uint resourceSize, [MarshalAs(UnmanagedType.Bool)] bool isIcon,
        uint version, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr iconHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr windowHandle);
}
