using System.Text;
using System.Runtime.InteropServices;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using ClipboardManager.Windows.Images;

namespace ClipboardManager.Windows.Clipboard;

public sealed class NativeClipboardReader : IClipboardReader, IClipboardWriter, IDisposable
{
    private const uint CfBitmap = 2;
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint CfHdrop = 15;
    private const uint CfDibV5 = 17;
    private const string PrivateOriginFormat = "ClipboardManager.PrivateOrigin.v1";
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp" };

    private readonly IClipboardNativeApi _native;
    private readonly WindowsImageCodec _codec;
    private readonly StaClipboardWorker _worker;
    private readonly bool _ownsWorker;
    private readonly byte[] _origin = Guid.NewGuid().ToByteArray();

    public NativeClipboardReader(IClipboardNativeApi? native = null, WindowsImageCodec? codec = null, StaClipboardWorker? worker = null)
    {
        _native = native ?? new WindowsClipboardNativeApi();
        _codec = codec ?? new WindowsImageCodec();
        _ownsWorker = worker is null;
        _worker = worker ?? new StaClipboardWorker();
    }

    public Task<ClipboardReadResult> ReadAsync(CancellationToken cancellationToken)
        => _worker.RunAsync(() => Read(cancellationToken), cancellationToken);

    public Task WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        WindowsImageCodec.ValidateTextSize(Encoding.Unicode.GetByteCount(text));
        return _worker.RunAsync(() =>
        {
            OpenWithRetry(IntPtr.Zero, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_native.EmptyClipboard()) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed.");
                byte[] textBytes = Encoding.Unicode.GetBytes(text + '\0');
                _native.WriteGlobalBytes(CfUnicodeText, textBytes);
                _native.WriteGlobalBytes(Register(PrivateOriginFormat), _origin);
               return true;
           }
           finally { _native.CloseClipboard(); }
       }, cancellationToken);
   }

   public Task WriteImageAsync(string imagePath, CancellationToken cancellationToken)
   {
       ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
       return _worker.RunAsync(() =>
       {
           cancellationToken.ThrowIfCancellationRequested();
           var info = new FileInfo(imagePath);
           WindowsImageCodec.ValidateEncodedSize(info.Length);
           byte[] source = File.ReadAllBytes(imagePath);
           CanonicalImage image = _codec.DecodeAsync(source, cancellationToken).GetAwaiter().GetResult();
           byte[] dib = _codec.EncodeDib(image);
           byte[] dibV5 = _codec.EncodeDibV5(image);
           uint pngFormat = Register("PNG");
           uint imagePngFormat = Register("image/png");
           OpenWithRetry(IntPtr.Zero, cancellationToken);
           try
           {
               cancellationToken.ThrowIfCancellationRequested();
               if (!_native.EmptyClipboard()) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed.");
               _native.WriteGlobalBytes(pngFormat, image.PngBytes);
               if (imagePngFormat != pngFormat) _native.WriteGlobalBytes(imagePngFormat, image.PngBytes);
               _native.WriteGlobalBytes(CfDib, dib);
               _native.WriteGlobalBytes(CfDibV5, dibV5);
               _native.WriteGlobalBytes(Register(PrivateOriginFormat), _origin);
               return true;
           }
           finally { _native.CloseClipboard(); }
       }, cancellationToken);
   }

    public void Dispose()
    {
        if (_ownsWorker) _worker.Dispose();
    }

   private ClipboardReadResult Read(CancellationToken cancellationToken)
   {
       var errors = new List<string>();
       var imageCandidates = new List<(byte[] Bytes, string Name)>();
       string? text = null;
       string? droppedImagePath = null;
       bool hasBitmap = false;
        bool hasDib = false;
       uint sequence;

       OpenWithRetry(IntPtr.Zero, cancellationToken);
       try
       {
           sequence = _native.GetSequenceNumber();
           uint marker = Register(PrivateOriginFormat);
           if (_native.IsClipboardFormatAvailable(marker))
           {
               try
               {
                   if (_native.ReadGlobalBytes(marker, _origin.Length).AsSpan().SequenceEqual(_origin))
                       return new ClipboardReadResult(sequence, null, null);
               }
               catch (Exception exception) when (IsReadFailure(exception)) { errors.Add("origin marker unreadable"); }
           }

           var registered = RegisterImageFormats();
           var available = registered.Values.Where(_native.IsClipboardFormatAvailable).ToHashSet();
           foreach (uint format in ClipboardFormatPolicy.ImageFormatPriority(registered))
           {
               if (!available.Contains(format)) continue;
                try
                {
                    byte[] payload = _native.ReadGlobalBytes(format, ClipboardFormatPolicy.MaxImageBytes);
                    if (!WindowsImageCodec.HasSupportedSignature(payload))
                    {
                        errors.Add($"format:{format} signature invalid");
                        continue;
                    }
                    imageCandidates.Add((payload, $"format:{format}"));
                    break;
                }
               catch (Exception exception) when (IsReadFailure(exception)) { errors.Add($"format:{format} read failed"); }
           }

            hasDib = _native.IsClipboardFormatAvailable(CfDibV5) || _native.IsClipboardFormatAvailable(CfDib);
           hasBitmap = _native.IsClipboardFormatAvailable(CfBitmap);

           if (_native.IsClipboardFormatAvailable(CfHdrop))
           {
               IntPtr drop = IntPtr.Zero;
               try
               {
                    drop = _native.GetClipboardDataHandle(CfHdrop);
                   droppedImagePath = _native.ReadDroppedFiles(drop, 100).FirstOrDefault(path => ImageExtensions.Contains(Path.GetExtension(path)));
               }
               catch (Exception exception) when (IsReadFailure(exception)) { errors.Add("CF_HDROP read failed"); }
           }

           if (_native.IsClipboardFormatAvailable(CfUnicodeText))
           {
               try { text = DecodeUnicodeText(_native.ReadGlobalBytes(CfUnicodeText, ClipboardFormatPolicy.MaxTextBytes)); }
               catch (Exception exception) when (IsReadFailure(exception)) { errors.Add("Unicode text read failed"); }
           }
       }
       finally { _native.CloseClipboard(); }

       foreach (var candidate in imageCandidates)
       {
           try
           {
               CanonicalImage image = _codec.DecodeAsync(candidate.Bytes, cancellationToken).GetAwaiter().GetResult();
               return new ClipboardReadResult(sequence, ClipboardSnapshotFactory.FromImage(image, DateTime.UtcNow), errors.Count == 0 ? null : JoinErrors(errors));
           }
           catch (Exception exception) when (IsReadFailure(exception)) { errors.Add($"{candidate.Name} decode failed"); }
       }

        if (hasDib && sequence == _native.GetSequenceNumber())
        {
            try
            {
                byte[]? dib = null;
                string dibName = "DIB";
                OpenWithRetry(IntPtr.Zero, cancellationToken);
                try
                {
                    if (sequence == _native.GetSequenceNumber())
                    {
                        foreach (uint format in new[] { CfDibV5, CfDib })
                        {
                            if (!_native.IsClipboardFormatAvailable(format)) continue;
                            try
                            {
                                dib = WindowsImageCodec.WrapDib(_native.ReadGlobalBytes(format, ClipboardFormatPolicy.MaxImageBytes));
                                dibName = format == CfDibV5 ? "DIBV5" : "DIB";
                                break;
                            }
                            catch (Exception exception) when (IsReadFailure(exception)) { errors.Add($"DIB:{format} read failed"); }
                        }
                    }
                }
                finally { _native.CloseClipboard(); }
                if (dib is not null)
                {
                    CanonicalImage image = _codec.DecodeAsync(dib, cancellationToken).GetAwaiter().GetResult();
                    return new ClipboardReadResult(sequence, ClipboardSnapshotFactory.FromImage(image, DateTime.UtcNow), errors.Count == 0 ? null : JoinErrors(errors));
                }
            }
            catch (Exception exception) when (IsReadFailure(exception)) { errors.Add("DIB decode failed"); }
        }

        if (hasBitmap && sequence == _native.GetSequenceNumber())
        {
            try
            {
                OpenWithRetry(IntPtr.Zero, cancellationToken);
                try
                {
                    if (sequence == _native.GetSequenceNumber())
                    {
                        IntPtr bitmap = _native.GetClipboardDataHandle(CfBitmap);
                        if (bitmap != IntPtr.Zero)
                        {
                            byte[] dib = _native.CopyBitmapAsDibV5(bitmap, WindowsImageCodec.MaxPixels);
                            CanonicalImage image = _codec.DecodeAsync(dib, cancellationToken, makeOpaque: true).GetAwaiter().GetResult();
                            return new ClipboardReadResult(sequence, ClipboardSnapshotFactory.FromImage(image, DateTime.UtcNow), errors.Count == 0 ? null : JoinErrors(errors));
                        }
                    }
                }
                finally { _native.CloseClipboard(); }
            }
            catch (Exception exception) when (IsReadFailure(exception)) { errors.Add("CF_BITMAP decode failed"); }
        }

       if (droppedImagePath is not null)
       {
           try
           {
               var info = new FileInfo(droppedImagePath);
               WindowsImageCodec.ValidateEncodedSize(info.Length);
               byte[] bytes = File.ReadAllBytes(droppedImagePath);
               ReadOnlyMemory<byte> source = Path.GetExtension(droppedImagePath).Equals(".dib", StringComparison.OrdinalIgnoreCase)
                   ? WindowsImageCodec.WrapDib(bytes) : bytes;
               CanonicalImage image = _codec.DecodeAsync(source, cancellationToken).GetAwaiter().GetResult();
               return new ClipboardReadResult(sequence, ClipboardSnapshotFactory.FromImage(image, DateTime.UtcNow), errors.Count == 0 ? null : JoinErrors(errors));
           }
           catch (Exception exception) when (IsReadFailure(exception)) { errors.Add("image file decode failed"); }
       }

       ClipboardSnapshot? textSnapshot = ClipboardSnapshotFactory.FromText(text, DateTime.UtcNow);
       return new ClipboardReadResult(sequence, textSnapshot, errors.Count == 0 ? null : JoinErrors(errors));
   }

   private Dictionary<string, uint> RegisterImageFormats()
   {
       var formats = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
       foreach (string name in new[] { "PNG", "image/png", "JFIF", "JPEG", "image/jpeg", "BMP", "image/bmp" })
       {
           uint format = Register(name);
           formats[name] = format;
       }
       return formats;
   }

   private uint Register(string name)
   {
       uint format = _native.RegisterClipboardFormat(name);
       if (format == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"RegisterClipboardFormat failed: {name}");
       return format;
   }


   private static string? DecodeUnicodeText(byte[] bytes)
   {
       if ((bytes.Length & 1) != 0) throw new InvalidDataException("Unicode clipboard payload has an odd byte count.");
       int length = 0;
       while (length + 1 < bytes.Length && (bytes[length] != 0 || bytes[length + 1] != 0)) length += 2;
       string text = Encoding.Unicode.GetString(bytes, 0, length);
       WindowsImageCodec.ValidateTextSize(Encoding.Unicode.GetByteCount(text));
       return string.IsNullOrEmpty(text) ? null : text;
   }

    private void OpenWithRetry(IntPtr owner, CancellationToken cancellationToken)
   {
       for (int attempt = 0; attempt < 7; attempt++)
       {
           cancellationToken.ThrowIfCancellationRequested();
            if (_native.OpenClipboard(owner)) return;
           if (attempt == 6) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Clipboard remained busy after bounded retries.");
           if (cancellationToken.WaitHandle.WaitOne(40 + attempt * 40)) cancellationToken.ThrowIfCancellationRequested();
       }
   }

   private static bool IsReadFailure(Exception exception)
       => exception is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException;

   private static string JoinErrors(IEnumerable<string> errors) => "Some clipboard formats could not be read: " + string.Join(", ", errors.Take(3));

}

