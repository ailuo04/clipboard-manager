using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClipboardManager.Services;

public static class UpdateCredentials
{
    private const string LegacyTarget = "ClipboardManager:GitHub:ailuo04/clipboard-manager";

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    public static void DeleteLegacy()
    {
        if (!CredDelete(LegacyTarget, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法清理旧版更新访问令牌。");
    }
}
