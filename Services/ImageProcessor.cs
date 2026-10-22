using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipboardManager.Services;

public static class ImageProcessor
{
    public const long MaxPixels = 40_000_000;

    public static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new InvalidDataException("图片尺寸不合法或超过 4000 万像素的安全上限。");
    }

    public static BitmapSource Decode(byte[] bytes, bool makeOpaque = false)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("图像数据没有可解码的帧。");
        return Normalize(decoder.Frames[0], makeOpaque);
    }

    public static BitmapSource Normalize(BitmapSource bitmap, bool makeOpaque = false)
    {
        ValidateDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32
            ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        if (makeOpaque)
            for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var normalized = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        normalized.Freeze();
        return normalized;
    }

    public static EncodedImage Prepare(BitmapSource image)
    {
        var normalized = Normalize(image);
        int stride = checked(normalized.PixelWidth * 4);
        var pixels = new byte[checked(stride * normalized.PixelHeight)];
        normalized.CopyPixels(pixels, stride, 0);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> dimensions = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, normalized.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], normalized.PixelHeight);
        hasher.AppendData(dimensions);
        hasher.AppendData(pixels);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(normalized));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new EncodedImage(stream.ToArray(), Convert.ToHexString(hasher.GetHashAndReset()), normalized.PixelWidth, normalized.PixelHeight);
    }

    public static byte[] EncodeDib(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        int stride = checked(source.PixelWidth * 4);
        var dib = new byte[checked(40 + stride * source.PixelHeight)];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), source.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), -source.PixelHeight);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), stride * source.PixelHeight);
        source.CopyPixels(System.Windows.Int32Rect.Empty, dib, stride, 40);
        return dib;
    }

    public static byte[] WrapDib(byte[] dib)
    {
        if (dib.Length < 12) throw new InvalidDataException("DIB 头不完整。");
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        int offset;
        if (headerSize == 12)
        {
            int width = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(4));
            int height = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(6));
            ValidateDimensions(width, height);
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(10));
            if (bits is not (1 or 4 or 8 or 24)) throw new InvalidDataException("不支持的 DIB 位深。");
            offset = checked(12 + (bits <= 8 ? (1 << bits) * 3 : 0));
        }
        else
        {
            if (headerSize < 40 || headerSize > dib.Length) throw new InvalidDataException("DIB 头长度无效。");
            int width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
            int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
            if (signedHeight == int.MinValue) throw new InvalidDataException("DIB 高度无效。");
            ValidateDimensions(width, Math.Abs(signedHeight));
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
            if (bits is not (1 or 4 or 8 or 16 or 24 or 32)) throw new InvalidDataException("不支持的 DIB 位深。");
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
            uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
            uint colors = colorsUsed != 0 ? colorsUsed : bits <= 8 ? 1u << bits : 0;
            if (colors > 65536) throw new InvalidDataException("DIB 调色板过大。");
            int masks = headerSize == 40 ? compression == 3 ? 12 : compression == 6 ? 16 : 0 : 0;
            offset = checked((int)headerSize + (int)colors * 4 + masks);
        }
        if (offset >= dib.Length) throw new InvalidDataException("DIB 像素数据不完整。");
        var bmp = new byte[checked(dib.Length + 14)];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), offset + 14);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    public static bool IsOpaqueDib(byte[] bmp)
        => bmp.Length >= 54 && BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(14)) >= 40
            && BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(30)) == 0;

    public static bool HasImageSignature(byte[] bytes)
        => bytes.Length >= 8 &&
            (bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G'
             || bytes[0] == 0xff && bytes[1] == 0xd8
             || bytes[0] == (byte)'B' && bytes[1] == (byte)'M');
}

public sealed record EncodedImage(byte[] Bytes, string Hash, int Width, int Height);
