using System.Runtime.InteropServices;
using System.Text;
using ClipboardManager.Windows.Images;

namespace ClipboardManager.Windows.Clipboard;

public sealed class WindowsClipboardNativeApi : IClipboardNativeApi
{
    private const uint CfBitmap = 2;
    private const uint CfHdrop = 15;
    private const uint DibRgbColors = 0;

    public bool OpenClipboard(IntPtr owner) => Native.OpenClipboard(owner);
    public bool CloseClipboard() => Native.CloseClipboard();
    public bool EmptyClipboard() => Native.EmptyClipboard();
    public uint GetSequenceNumber() => Native.GetClipboardSequenceNumber();
    public uint RegisterClipboardFormat(string name) => Native.RegisterClipboardFormat(name);
    public bool IsClipboardFormatAvailable(uint format) => Native.IsClipboardFormatAvailable(format);
    public IntPtr GetClipboardDataHandle(uint format) => Native.GetClipboardData(format);

    public byte[] ReadGlobalBytes(uint format, int maxBytes)
    {
        IntPtr handle = GetClipboardDataHandle(format);
        if (handle == IntPtr.Zero) throw LastError($"GetClipboardData({format})");
        ulong size = Native.GlobalSize(handle).ToUInt64();
        if (size == 0 || size > (ulong)maxBytes)
            throw new ImageCodecException(ImageCodecError.InputTooLarge, $"Clipboard format {format} exceeds its {maxBytes}-byte limit.");
        IntPtr pointer = Native.GlobalLock(handle);
        if (pointer == IntPtr.Zero) throw LastError("GlobalLock");
        try
        {
            var bytes = new byte[checked((int)size)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Native.GlobalUnlock(handle); }
    }

    public void WriteGlobalBytes(uint format, ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty) throw new ArgumentException("Clipboard payload must not be empty.", nameof(bytes));
        IntPtr allocation = Native.GlobalAlloc(0x0042, (UIntPtr)bytes.Length);
        if (allocation == IntPtr.Zero) throw LastError("GlobalAlloc");
        try
        {
            IntPtr pointer = Native.GlobalLock(allocation);
            if (pointer == IntPtr.Zero) throw LastError("GlobalLock");
            try { Marshal.Copy(bytes.ToArray(), 0, pointer, bytes.Length); }
            finally { Native.GlobalUnlock(allocation); }
            if (Native.SetClipboardData(format, allocation) == IntPtr.Zero) throw LastError($"SetClipboardData({format})");
            allocation = IntPtr.Zero;
        }
        finally { if (allocation != IntPtr.Zero) Native.GlobalFree(allocation); }
    }

    public byte[] CopyBitmapAsDibV5(IntPtr bitmapHandle, long maxPixels)
    {
        if (bitmapHandle == IntPtr.Zero) throw new ArgumentException("Bitmap handle is null.", nameof(bitmapHandle));
        int objectSize = Marshal.SizeOf<NativeBitmap>();
        if (Native.GetObject(bitmapHandle, objectSize, out NativeBitmap bitmap) != objectSize)
            throw LastError("GetObjectW");
        int width = bitmap.Width;
        int height = Math.Abs(bitmap.Height);
        WindowsImageCodec.ValidateDimensions(width, height);
        if ((long)width * height > maxPixels)
            throw new ImageCodecException(ImageCodecError.InvalidDimensions, "CF_BITMAP exceeds the configured pixel limit.");

       int stride = checked(width * 4);
       int imageBytes = checked(stride * height);
       var header = new NativeBitmapInfoHeader
       {
           Size = (uint)Marshal.SizeOf<NativeBitmapInfoHeader>(),
           Width = width,
           Height = -height,
           Planes = 1,
           BitCount = 32,
           Compression = 0,
           SizeImage = (uint)imageBytes
       };
       var info = new NativeBitmapInfo { Header = header };
       byte[] pixels = new byte[imageBytes];
       IntPtr dc = Native.GetDC(IntPtr.Zero);
       if (dc == IntPtr.Zero) throw LastError("GetDC");
       try
       {
           int copied = Native.GetDIBits(dc, bitmapHandle, 0, (uint)height, pixels, ref info, DibRgbColors);
           if (copied != height) throw LastError("GetDIBits");
       }
       finally { Native.ReleaseDC(IntPtr.Zero, dc); }

       var dib = new byte[checked(Marshal.SizeOf<NativeBitmapInfoHeader>() + pixels.Length)];
       MemoryMarshal.Write(dib.AsSpan(), in header);
       pixels.CopyTo(dib, Marshal.SizeOf<NativeBitmapInfoHeader>());
       return dib;
   }

   public IReadOnlyList<string> ReadDroppedFiles(IntPtr dropHandle, int maxFiles)
   {
       if (dropHandle == IntPtr.Zero || maxFiles <= 0) return [];
       uint count = Math.Min(Native.DragQueryFile(dropHandle, uint.MaxValue, null, 0), (uint)maxFiles);
       var paths = new List<string>(checked((int)count));
       for (uint index = 0; index < count; index++)
       {
           uint length = Native.DragQueryFile(dropHandle, index, null, 0);
           var buffer = new StringBuilder(checked((int)length + 1));
           uint copied = Native.DragQueryFile(dropHandle, index, buffer, length + 1);
           if (copied != 0) paths.Add(buffer.ToString());
       }
       return paths;
   }

   private static Exception LastError(string operation)
       => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), operation);

   [StructLayout(LayoutKind.Sequential)]
   private struct NativeBitmap
   {
       public int Type;
       public int Width;
       public int Height;
       public int WidthBytes;
       public ushort Planes;
       public ushort BitsPixel;
       public IntPtr Bits;
   }

   [StructLayout(LayoutKind.Sequential)]
   private struct NativeBitmapInfoHeader
   {
       public uint Size;
       public int Width;
       public int Height;
       public ushort Planes;
       public ushort BitCount;
       public uint Compression;
       public uint SizeImage;
       public int XPelsPerMeter;
       public int YPelsPerMeter;
       public uint ClrUsed;
       public uint ClrImportant;
   }

   [StructLayout(LayoutKind.Sequential)]
   private struct NativeBitmapInfo
   {
       public NativeBitmapInfoHeader Header;
       public uint Color;
   }

   private static class Native
   {
       [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
       internal static extern bool OpenClipboard(IntPtr owner);
       [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
       internal static extern bool CloseClipboard();
       [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
       internal static extern bool EmptyClipboard();
       [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
       [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
       internal static extern uint RegisterClipboardFormat(string formatName);
       [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
       internal static extern bool IsClipboardFormatAvailable(uint format);
       [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetClipboardData(uint format);
       [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetClipboardData(uint format, IntPtr memory);
       [DllImport("kernel32.dll", SetLastError = true)] internal static extern UIntPtr GlobalSize(IntPtr memory);
       [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GlobalLock(IntPtr memory);
       [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
       internal static extern bool GlobalUnlock(IntPtr memory);
       [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
       [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr memory);
       [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
       internal static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);
       [DllImport("gdi32.dll", SetLastError = true)]
       internal static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref NativeBitmapInfo info, uint usage);
       [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
       [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
       [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
       internal static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? fileName, uint size);
   }
}

