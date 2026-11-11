using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ClipboardManager.Windows.Images;

public enum ImageCodecError { InputTooLarge, TextTooLarge, InvalidDimensions, InvalidData }

public sealed class ImageCodecException : Exception
{
    public ImageCodecException(ImageCodecError error, string message, Exception? innerException = null)
        : base(message, innerException) => Error = error;
    public ImageCodecError Error { get; }
}

public sealed record CanonicalImage(byte[] PngBytes, string ContentHash, int Width, int Height, byte[] BgraPixels);

public sealed class WindowsImageCodec
{
    public const int MaxEncodedImageBytes = ClipboardManager.Windows.Clipboard.ClipboardFormatPolicy.MaxImageBytes;
    public const int MaxTextBytes = ClipboardManager.Windows.Clipboard.ClipboardFormatPolicy.MaxTextBytes;
    public const long MaxPixels = ClipboardManager.Windows.Clipboard.ClipboardFormatPolicy.MaxImagePixels;
    private const int BytesPerPixel = 4;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public async Task<CanonicalImage> DecodeAsync(ReadOnlyMemory<byte> encoded, CancellationToken cancellationToken, bool makeOpaque = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEncodedSize(encoded.Length);
        try
        {
            byte[] source = encoded.ToArray();
            if (!HasPngSignature(source) && !HasBmpSignature(source) && !HasJpegSignature(source))
                source = WrapDib(source);

           using var stream = new InMemoryRandomAccessStream();
           using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
           {
               writer.WriteBytes(source);
               await writer.StoreAsync();
               await writer.FlushAsync();
               writer.DetachStream();
           }
           stream.Seek(0);
           var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.OrientedPixelWidth > int.MaxValue || decoder.OrientedPixelHeight > int.MaxValue)
                throw new ImageCodecException(ImageCodecError.InvalidDimensions, "Image dimensions are invalid.");
            int width = checked((int)decoder.OrientedPixelWidth);
            int height = checked((int)decoder.OrientedPixelHeight);
            ValidateDimensions(width, height);
           cancellationToken.ThrowIfCancellationRequested();
           var transform = new BitmapTransform();
           var provider = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
               ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
           byte[] pixels = provider.DetachPixelData();
           int expectedLength = checked(width * height * BytesPerPixel);
           if (pixels.Length != expectedLength)
               throw new ImageCodecException(ImageCodecError.InvalidData, "Decoded pixel buffer has an invalid length.");
           if (makeOpaque)
               for (int index = 3; index < pixels.Length; index += BytesPerPixel) pixels[index] = 255;
            string hash = Convert.ToHexString(ComputeLegacyPixelHash(width, height, pixels));
           using var bitmap = CreateBitmap(width, height, pixels);
           byte[] png = await EncodeAsync(bitmap, BitmapEncoder.PngEncoderId, cancellationToken).ConfigureAwait(false);
           ValidateEncodedSize(png.LongLength);
           return new CanonicalImage(png, hash, width, height, pixels);
       }
       catch (OperationCanceledException) { throw; }
       catch (ImageCodecException) { throw; }
       catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
       {
           throw new ImageCodecException(ImageCodecError.InvalidData, "Image data is damaged or uses an unsupported format.", exception);
       }
   }

   public Task<byte[]> EncodePngAsync(CanonicalImage image, CancellationToken cancellationToken)
       => EncodeCanonicalAsync(image, BitmapEncoder.PngEncoderId, cancellationToken);

   public Task<byte[]> EncodeBmpAsync(CanonicalImage image, CancellationToken cancellationToken)
       => EncodeCanonicalAsync(image, BitmapEncoder.BmpEncoderId, cancellationToken);

   public byte[] EncodeDib(CanonicalImage image)
   {
       ValidateCanonicalImage(image);
       int pixelBytes = checked(image.Width * image.Height * BytesPerPixel);
       var dib = new byte[checked(40 + pixelBytes)];
       var span = dib.AsSpan();
       BinaryPrimitives.WriteInt32LittleEndian(span, 40);
       BinaryPrimitives.WriteInt32LittleEndian(span[4..], image.Width);
       BinaryPrimitives.WriteInt32LittleEndian(span[8..], -image.Height);
       BinaryPrimitives.WriteInt16LittleEndian(span[12..], 1);
       BinaryPrimitives.WriteInt16LittleEndian(span[14..], 32);
       BinaryPrimitives.WriteInt32LittleEndian(span[20..], pixelBytes);
       image.BgraPixels.CopyTo(span[40..]);
       return dib;
   }

   public byte[] EncodeDibV5(CanonicalImage image)
   {
       ValidateCanonicalImage(image);
       int pixelBytes = checked(image.Width * image.Height * BytesPerPixel);
       var dib = new byte[checked(124 + pixelBytes)];
       var span = dib.AsSpan();
       BinaryPrimitives.WriteInt32LittleEndian(span, 124);
       BinaryPrimitives.WriteInt32LittleEndian(span[4..], image.Width);
       BinaryPrimitives.WriteInt32LittleEndian(span[8..], -image.Height);
       BinaryPrimitives.WriteInt16LittleEndian(span[12..], 1);
       BinaryPrimitives.WriteInt16LittleEndian(span[14..], 32);
       BinaryPrimitives.WriteInt32LittleEndian(span[16..], 3); // BI_BITFIELDS
       BinaryPrimitives.WriteInt32LittleEndian(span[20..], pixelBytes);
       BinaryPrimitives.WriteUInt32LittleEndian(span[40..], 0x00FF0000);
       BinaryPrimitives.WriteUInt32LittleEndian(span[44..], 0x0000FF00);
       BinaryPrimitives.WriteUInt32LittleEndian(span[48..], 0x000000FF);
       BinaryPrimitives.WriteUInt32LittleEndian(span[52..], 0xFF000000);
       BinaryPrimitives.WriteUInt32LittleEndian(span[56..], 0x73524742); // LCS_sRGB
       image.BgraPixels.CopyTo(span[124..]);
       return dib;
   }

   public static CanonicalImage FromBgraPixels(int width, int height, byte[] pixels, bool makeOpaque = false)
   {
       ArgumentNullException.ThrowIfNull(pixels);
       ValidateDimensions(width, height);
       if (pixels.Length != checked(width * height * BytesPerPixel))
           throw new ImageCodecException(ImageCodecError.InvalidData, "Pixel buffer length does not match dimensions.");
       byte[] copy = pixels.ToArray();
       if (makeOpaque)
           for (int index = 3; index < copy.Length; index += BytesPerPixel) copy[index] = 255;
        return new CanonicalImage([], Convert.ToHexString(ComputeLegacyPixelHash(width, height, copy)), width, height, copy);
   }

   public static void ValidateEncodedSize(long length)
   {
       if (length <= 0 || length > MaxEncodedImageBytes)
           throw new ImageCodecException(ImageCodecError.InputTooLarge, "Clipboard image is empty or exceeds the 128 MiB limit.");
   }

   public static void ValidateTextSize(long length)
   {
       if (length < 0 || length > MaxTextBytes)
           throw new ImageCodecException(ImageCodecError.TextTooLarge, "Clipboard text exceeds the 8 MiB limit.");
   }

   public static void ValidateDimensions(int width, int height)
   {
       if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
           throw new ImageCodecException(ImageCodecError.InvalidDimensions, "Image dimensions are invalid or exceed 40 megapixels.");
   }

    public static bool HasSupportedSignature(ReadOnlySpan<byte> data)
        => HasPngSignature(data) || HasBmpSignature(data) || HasJpegSignature(data);

   private static async Task<byte[]> EncodeCanonicalAsync(CanonicalImage image, Guid encoderId, CancellationToken cancellationToken)
   {
       ValidateCanonicalImage(image);
       using var bitmap = CreateBitmap(image.Width, image.Height, image.BgraPixels);
       byte[] encoded = await EncodeAsync(bitmap, encoderId, cancellationToken).ConfigureAwait(false);
       ValidateEncodedSize(encoded.LongLength);
       return encoded;
   }

   private static void ValidateCanonicalImage(CanonicalImage image)
   {
       ArgumentNullException.ThrowIfNull(image);
       ValidateDimensions(image.Width, image.Height);
       if (image.BgraPixels.Length != checked(image.Width * image.Height * BytesPerPixel))
           throw new ImageCodecException(ImageCodecError.InvalidData, "Canonical pixel buffer length does not match dimensions.");
   }

    public static byte[] WrapDib(ReadOnlySpan<byte> dib)
   {
       if (dib.Length < 12) throw new ImageCodecException(ImageCodecError.InvalidData, "DIB header is incomplete.");
       uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
       int pixelOffset;
       int width;
       int height;
       int bitsPerPixel;
       if (headerSize == 12)
       {
           width = BinaryPrimitives.ReadUInt16LittleEndian(dib[4..]);
           height = BinaryPrimitives.ReadUInt16LittleEndian(dib[6..]);
           ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(dib[8..]);
           bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
           if (planes != 1 || bitsPerPixel is not (1 or 4 or 8 or 24))
               throw new ImageCodecException(ImageCodecError.InvalidData, "Unsupported BITMAPCOREHEADER format.");
           int paletteBytes = bitsPerPixel <= 8 ? (1 << bitsPerPixel) * 3 : 0;
           pixelOffset = checked((int)headerSize + paletteBytes);
       }
       else
       {
           if (headerSize < 40 || headerSize > dib.Length)
               throw new ImageCodecException(ImageCodecError.InvalidData, "DIB header length is invalid.");
           width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
           int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
           if (signedHeight == int.MinValue) throw new ImageCodecException(ImageCodecError.InvalidDimensions, "DIB height is invalid.");
           height = Math.Abs(signedHeight);
           ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(dib[12..]);
           bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
           uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
           uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
           if (planes != 1 || bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32) || compression is not (0 or 3 or 6))
               throw new ImageCodecException(ImageCodecError.InvalidData, "Unsupported DIB compression or bit depth.");
           uint colors = colorsUsed != 0 ? colorsUsed : bitsPerPixel <= 8 ? 1u << bitsPerPixel : 0;
           if (colors > 65536) throw new ImageCodecException(ImageCodecError.InvalidData, "DIB palette is too large.");
           int externalMasks = headerSize == 40 && compression is 3 or 6 ? compression == 3 ? 12 : 16 : 0;
           pixelOffset = checked((int)headerSize + externalMasks + (int)colors * 4);
       }

       ValidateDimensions(width, height);
       if (pixelOffset >= dib.Length)
           throw new ImageCodecException(ImageCodecError.InvalidData, "DIB pixel data is missing.");
       long stride = checked((((long)width * bitsPerPixel + 31) / 32) * 4);
       long pixelLength = checked(stride * height);
       if (pixelLength > dib.Length - pixelOffset)
           throw new ImageCodecException(ImageCodecError.InvalidData, "DIB pixel data is truncated.");

       var bmp = new byte[checked(dib.Length + 14)];
       bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
       BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
       BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), checked(pixelOffset + 14));
       dib.CopyTo(bmp.AsSpan(14));
       return bmp;
   }

   private static async Task<CanonicalImage> CanonicalizeAsync(ReadOnlyMemory<byte> pixels, int width, int height,
       CancellationToken cancellationToken)
   {
       ValidateDimensions(width, height);
       int expectedLength = checked(width * height * BytesPerPixel);
       if (pixels.Length != expectedLength)
           throw new ImageCodecException(ImageCodecError.InvalidData, "Decoded pixel buffer has an invalid length.");
       byte[] bgra = pixels.ToArray();
        string hash = Convert.ToHexString(ComputeLegacyPixelHash(width, height, bgra));
       using var bitmap = CreateBitmap(width, height, bgra);
       byte[] png = await EncodeAsync(bitmap, BitmapEncoder.PngEncoderId, cancellationToken).ConfigureAwait(false);
       ValidateEncodedSize(png.LongLength);
       return new CanonicalImage(png, hash, width, height, bgra);
   }

   private static byte[] ComputeLegacyPixelHash(int width, int height, byte[] bgraPixels)
   {
       using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
       Span<byte> dimensions = stackalloc byte[8];
       BinaryPrimitives.WriteInt32LittleEndian(dimensions, width);
       BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], height);
       hash.AppendData(dimensions);
       hash.AppendData(bgraPixels);
       return hash.GetHashAndReset();
   }

   private static SoftwareBitmap CreateBitmap(int width, int height, ReadOnlyMemory<byte> pixels)
   {
       ValidateDimensions(width, height);
       int length = checked(width * height * BytesPerPixel);
       if (pixels.Length != length)
           throw new ImageCodecException(ImageCodecError.InvalidData, "Canonical pixel buffer length does not match dimensions.");
       using var writer = new DataWriter();
       writer.WriteBytes(pixels.ToArray());
        IBuffer buffer = writer.DetachBuffer();
        return SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Straight);
   }

   private static async Task<byte[]> EncodeAsync(SoftwareBitmap bitmap, Guid encoderId, CancellationToken cancellationToken)
   {
       cancellationToken.ThrowIfCancellationRequested();
       using var stream = new InMemoryRandomAccessStream();
       var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
       encoder.SetSoftwareBitmap(bitmap);
       cancellationToken.ThrowIfCancellationRequested();
       await encoder.FlushAsync();
       cancellationToken.ThrowIfCancellationRequested();
       if (stream.Size > MaxEncodedImageBytes)
           throw new ImageCodecException(ImageCodecError.InputTooLarge, "Encoded image exceeds the 128 MiB limit.");
       stream.Seek(0);
       using var reader = new DataReader(stream.GetInputStreamAt(0));
       uint size = checked((uint)stream.Size);
       await reader.LoadAsync(size);
       var encoded = new byte[checked((int)size)];
       reader.ReadBytes(encoded);
       return encoded;
   }

   private static bool HasPngSignature(ReadOnlySpan<byte> data) => data.Length >= PngSignature.Length && data[..8].SequenceEqual(PngSignature);
   private static bool HasBmpSignature(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M';
   private static bool HasJpegSignature(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8;
}

