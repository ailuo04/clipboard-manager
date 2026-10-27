using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipboardManager.Services;

public static class UpdateCredentials
{
    private const string Target = "ClipboardManager:GitHub:ailuo04/clipboard-manager";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);

    public static string? Read()
    {
        if (!CredRead(Target, 1, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "无法读取更新凭据。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally { CredFree(pointer); }
    }

    public static void Save(string token)
    {
        if (token.Length is < 10 or > 255 || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("访问令牌格式不正确。");
        IntPtr blob = Marshal.StringToCoTaskMemUni(token);
        try
        {
            var credential = new Credential { Type = 1, TargetName = Target, UserName = "GitHub", Persist = 2,
                CredentialBlob = blob, CredentialBlobSize = checked((uint)Encoding.Unicode.GetByteCount(token)) };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存更新凭据。");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    public static void Delete()
    {
        if (!CredDelete(Target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法删除更新凭据。");
    }
}
