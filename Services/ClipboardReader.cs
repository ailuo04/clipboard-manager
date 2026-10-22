using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ClipboardManager.Utilities;

namespace ClipboardManager.Services;

public sealed class ClipboardReader : IAsyncDisposable
{
    public const int MaxClipboardBytes = 128 * 1024 * 1024;
    private const string OriginFormat = "ClipboardManager.PrivateOrigin.v1";
    private static readonly string[] ImageFormats = ["PNG", "image/png", "JFIF", "JPEG", "image/jpeg", "BMP", "image/bmp"];
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp" };
    private readonly byte[] _origin = Guid.NewGuid().ToByteArray();
    private readonly StaTaskRunner _worker = new();

    public Task<ClipboardReadResult> ReadAsync(CancellationToken cancellationToken)
        => _worker.RunAsync(() => Read(cancellationToken), cancellationToken);

    public Task WriteTextAsync(string text, IntPtr owner, CancellationToken cancellationToken)
    {
        return _worker.RunAsync(() =>
        {
            OpenWithRetry(owner, cancellationToken);
            try
            {
                NativeMethods.Check(NativeMethods.EmptyClipboard(), "清空剪贴板以写入文本失败");
                SetBytes(NativeMethods.CfUnicodeText, Encoding.Unicode.GetBytes(text + '\0'));
                SetOrigin();
                return true;
            }
            finally { Close(); }
        }, cancellationToken);
    }

    public Task WriteImageAsync(string path, IntPtr owner, CancellationToken cancellationToken)
    {
        return _worker.RunAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var png = File.ReadAllBytes(path);
            var bitmap = ImageProcessor.Decode(png);
            var dib = ImageProcessor.EncodeDib(bitmap);
            OpenWithRetry(owner, cancellationToken);
            try
            {
                NativeMethods.Check(NativeMethods.EmptyClipboard(), "清空剪贴板以写入图片失败");
                SetBytes(RegisterFormat("PNG"), png);
                SetBytes(NativeMethods.CfDib, dib);
                SetOrigin();
                return true;
            }
            finally { Close(); }
        }, cancellationToken);
    }

    public ValueTask DisposeAsync() => _worker.DisposeAsync();

    private ClipboardReadResult Read(CancellationToken cancellationToken)
    {
        BitmapSource? bitmap = null;
        var candidates = new List<(byte[] Bytes, string Format, bool Opaque)>();
        bool hasBitmap = false;
        string? imagePath = null;
        string? text = null;
        string source = string.Empty;
        var errors = new List<string>();
        uint sequence;

        OpenWithRetry(IntPtr.Zero, cancellationToken);
        try
        {
            sequence = NativeMethods.GetClipboardSequenceNumber();
            var marker = RegisterFormat(OriginFormat);
            if (NativeMethods.IsClipboardFormatAvailable(marker) && ReadBytes(marker).SequenceEqual(_origin))
                return new ClipboardReadResult(sequence, null, null);

            foreach (var name in ImageFormats)
            {
                uint format = RegisterFormat(name);
                if (!NativeMethods.IsClipboardFormatAvailable(format)) continue;
                try
                {
                    var bytes = ReadBytes(format);
                    if (!ImageProcessor.HasImageSignature(bytes)) throw new InvalidDataException("图像文件头不合法。");
                    candidates.Add((bytes, name, false));
                    break;
                }
                catch (Exception exception) when (IsExpectedReadError(exception))
                {
                    LogFormatError(name, exception, errors);
                }
            }

            if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfDibV5)
                || NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfDib))
            {
                foreach (uint format in new[] { NativeMethods.CfDibV5, NativeMethods.CfDib })
                {
                    if (!NativeMethods.IsClipboardFormatAvailable(format)) continue;
                    try
                    {
                        var bytes = ImageProcessor.WrapDib(ReadBytes(format));
                        candidates.Add((bytes, format == NativeMethods.CfDibV5 ? "DIBV5" : "DIB", ImageProcessor.IsOpaqueDib(bytes)));
                        break;
                    }
                    catch (Exception exception) when (IsExpectedReadError(exception))
                    {
                        LogFormatError($"DIB {format}", exception, errors);
                    }
                }
            }

            hasBitmap = NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfBitmap);
            if (candidates.Count == 0 && hasBitmap)
            {
                try
                {
                    var handle = NativeMethods.GetClipboardData(NativeMethods.CfBitmap);
                    if (handle != IntPtr.Zero)
                    {
                        var shared = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        ImageProcessor.ValidateDimensions(shared.PixelWidth, shared.PixelHeight);
                        // Copy pixels before CloseClipboard: the HBITMAP belongs to Windows.
                        bitmap = ImageProcessor.Normalize(shared, makeOpaque: true);
                        source = "Bitmap";
                    }
                }
                catch (Exception exception) when (IsExpectedReadError(exception))
                {
                    LogFormatError("Bitmap", exception, errors);
                }
            }

            if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfHdrop))
            {
                var drop = NativeMethods.GetClipboardData(NativeMethods.CfHdrop);
                uint count = NativeMethods.DragQueryFile(drop, uint.MaxValue, null, 0);
                for (uint index = 0; index < Math.Min(count, 100u); index++)
                {
                    uint length = NativeMethods.DragQueryFile(drop, index, null, 0);
                    var name = new StringBuilder(checked((int)length + 1));
                    NativeMethods.DragQueryFile(drop, index, name, length + 1);
                    if (ImageExtensions.Contains(Path.GetExtension(name.ToString())))
                    {
                        imagePath = name.ToString();
                        break;
                    }
                }
            }

            if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfUnicodeText))
            {
                var bytes = ReadBytes(NativeMethods.CfUnicodeText, 8 * 1024 * 1024);
                int length = 0;
                while (length + 1 < bytes.Length && (bytes[length] != 0 || bytes[length + 1] != 0)) length += 2;
                text = Encoding.Unicode.GetString(bytes, 0, length);
            }
        }
        finally { Close(); }

        // Decode after releasing the system clipboard so other applications are not held up.
        foreach (var candidate in candidates)
        {
            try
            {
                bitmap = ImageProcessor.Decode(candidate.Bytes, candidate.Opaque);
                source = candidate.Format;
                break;
            }
            catch (Exception exception) when (IsExpectedReadError(exception)) { LogFormatError(candidate.Format, exception, errors); }
        }

        if (bitmap is null && hasBitmap && candidates.Count > 0)
        {
            OpenWithRetry(IntPtr.Zero, cancellationToken);
            try
            {
                if (sequence == NativeMethods.GetClipboardSequenceNumber())
                {
                    var handle = NativeMethods.GetClipboardData(NativeMethods.CfBitmap);
                    if (handle != IntPtr.Zero)
                    {
                        bitmap = ImageProcessor.Normalize(Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero,
                            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()), makeOpaque: true);
                        source = "Bitmap";
                    }
                }
            }
            catch (Exception exception) when (IsExpectedReadError(exception)) { LogFormatError("Bitmap 回退", exception, errors); }
            finally { Close(); }
        }

        if (bitmap is null && imagePath is not null)
        {
            try
            {
                var info = new FileInfo(imagePath);
                if (info.Length > MaxClipboardBytes) throw new InvalidDataException("图片文件超过 128 MB 的单次读取上限。");
                var bytes = File.ReadAllBytes(imagePath);
                bitmap = ImageProcessor.Decode(Path.GetExtension(imagePath).Equals(".dib", StringComparison.OrdinalIgnoreCase)
                    ? ImageProcessor.WrapDib(bytes) : bytes);
                source = "ImageFile";
            }
            catch (Exception exception) when (IsExpectedReadError(exception)) { LogFormatError("图片文件", exception, errors); }
        }

        if (bitmap is not null) return new ClipboardReadResult(sequence, ClipboardSnapshot.FromImage(bitmap, source), null);
        var warning = errors.Count == 0 ? null : "剪贴板图片无法读取：" + string.Join("；", errors.Distinct().Take(3));
        return new ClipboardReadResult(sequence, string.IsNullOrEmpty(text) ? null : ClipboardSnapshot.FromText(text), warning);
    }

    private void SetOrigin() => SetBytes(RegisterFormat(OriginFormat), _origin);

    private static uint RegisterFormat(string name)
    {
        var format = NativeMethods.RegisterClipboardFormat(name);
        NativeMethods.Check(format != 0, $"注册剪贴板格式 {name} 失败");
        return format;
    }

    private static void OpenWithRetry(IntPtr owner, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 7; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.OpenClipboard(owner)) return;
            if (attempt == 6) NativeMethods.Check(false, "剪贴板被其他程序占用，重试后仍无法打开");
            if (cancellationToken.WaitHandle.WaitOne(40 + attempt * 40)) cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void Close()
    {
        if (!NativeMethods.CloseClipboard())
            AppLogger.LogException("关闭系统剪贴板失败", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static byte[] ReadBytes(uint format, int limit = MaxClipboardBytes)
    {
        var handle = NativeMethods.GetClipboardData(format);
        NativeMethods.Check(handle != IntPtr.Zero, $"获取剪贴板格式 {format} 失败");
        ulong size = NativeMethods.GlobalSize(handle).ToUInt64();
        if (size == 0 || size > (ulong)limit) throw new InvalidDataException($"剪贴板数据长度不合法或超过 {limit / 1024 / 1024} MB 上限。");
        var pointer = NativeMethods.GlobalLock(handle);
        NativeMethods.Check(pointer != IntPtr.Zero, "锁定剪贴板内存失败");
        try
        {
            var bytes = new byte[checked((int)size)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { NativeMethods.GlobalUnlock(handle); }
    }

    private static void SetBytes(uint format, byte[] bytes)
    {
        var memory = NativeMethods.GlobalAlloc(0x0042, (UIntPtr)bytes.Length);
        NativeMethods.Check(memory != IntPtr.Zero, "分配剪贴板内存失败");
        try
        {
            var pointer = NativeMethods.GlobalLock(memory);
            NativeMethods.Check(pointer != IntPtr.Zero, "锁定待写入剪贴板内存失败");
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { NativeMethods.GlobalUnlock(memory); }
            NativeMethods.Check(NativeMethods.SetClipboardData(format, memory) != IntPtr.Zero, $"写入剪贴板格式 {format} 失败");
            memory = IntPtr.Zero; // Windows owns the allocation after a successful write.
        }
        finally { if (memory != IntPtr.Zero) NativeMethods.GlobalFree(memory); }
    }

    private static bool IsExpectedReadError(Exception exception) => exception is not OperationCanceledException and not OutOfMemoryException;

    private static void LogFormatError(string format, Exception exception, List<string> errors)
    {
        AppLogger.LogException($"读取剪贴板格式 {format} 失败", exception);
        errors.Add($"{format}：{exception.Message}");
    }
}

public sealed record ClipboardReadResult(uint Sequence, ClipboardSnapshot? Snapshot, string? Warning);
