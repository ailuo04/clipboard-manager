using System.Runtime.InteropServices;

namespace ClipboardManager.UpdateWorker;

internal static class WindowsApplicationEventLog
{
    private const ushort EventTypeError = 0x0001;

    public static void TryWrite(string message, Guid correlationId)
    {
        if (!OperatingSystem.IsWindows()) return;
        IntPtr source = IntPtr.Zero;
        try
        {
            source = RegisterEventSource(null, "ClipboardManager");
            if (source == IntPtr.Zero) return;
            string safe = $"{message} CorrelationId={correlationId:N}";
            string[] strings = [safe];
            ReportEvent(source, EventTypeError, 0, 0xE0000001, IntPtr.Zero, 1, 0, strings, IntPtr.Zero);
        }
        catch { }
        finally { if (source != IntPtr.Zero) DeregisterEventSource(source); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterEventSource(string? serverName, string sourceName);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeregisterEventSource(IntPtr eventLogHandle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReportEvent(IntPtr eventLogHandle, ushort type, ushort category, uint eventId,
        IntPtr userSid, ushort numStrings, uint dataSize, string[] strings, IntPtr rawData);
}
