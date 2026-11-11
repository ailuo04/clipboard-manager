namespace ClipboardManager.Windows.Clipboard;

/// <summary>Injectable boundary for Win32 clipboard and global-memory ownership operations.</summary>
public interface IClipboardNativeApi
{
    bool OpenClipboard(IntPtr owner);
    bool CloseClipboard();
    bool EmptyClipboard();
    uint GetSequenceNumber();
    uint RegisterClipboardFormat(string name);
    bool IsClipboardFormatAvailable(uint format);
    IntPtr GetClipboardDataHandle(uint format);
    byte[] ReadGlobalBytes(uint format, int maxBytes);
    void WriteGlobalBytes(uint format, ReadOnlyMemory<byte> bytes);
    byte[] CopyBitmapAsDibV5(IntPtr bitmapHandle, long maxPixels);
    IReadOnlyList<string> ReadDroppedFiles(IntPtr dropHandle, int maxFiles);
}
