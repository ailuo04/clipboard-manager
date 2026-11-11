using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using ClipboardManager.Windows.Clipboard;
using ClipboardManager.Windows.Images;
using Xunit;

namespace ClipboardManager.Windows.Tests;

public sealed class NativeClipboardReaderTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    private const string OtherPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4AWP4z8DwHwAFAAH/e+m+7wAAAABJRU5ErkJggg==";

    [Fact]
    public async Task ReadAsync_retries_openclipboard_and_reads_unicode_text_through_fake_native_api()
    {
        var native = new FakeClipboardNativeApi { OpenFailuresRemaining = 2 };
        native.Available.Add(13);
        native.Data[13] = System.Text.Encoding.Unicode.GetBytes("测试文本\0");
        using var reader = new NativeClipboardReader(native);

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(3, native.OpenCalls);
        Assert.Equal("测试文本", result.Snapshot?.Text);
        Assert.Equal(77u, result.SequenceNumber);
        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    public async Task ReadAsync_ignores_own_clipboard_write_marker()
    {
        var native = new FakeClipboardNativeApi();
        using var reader = new NativeClipboardReader(native);
        await reader.WriteTextAsync("copied by this app", CancellationToken.None);

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Null(result.Snapshot);
        Assert.Equal(1, native.EmptyCalls);
        Assert.Contains(native.Writes, item => item.Format == 13);
        Assert.True(native.Writes.Count >= 2); // Unicode text and private origin marker.
    }

    [Fact]
    public async Task ReadAsync_tries_next_image_format_after_the_higher_priority_payload_is_corrupt()
    {
        var native = new FakeClipboardNativeApi();
        native.Available.UnionWith([100, 101]);
        native.Data[100] = [1, 2, 3, 4];
        native.Data[101] = Convert.FromBase64String(Png);
        using var reader = new NativeClipboardReader(native);

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(ClipboardContentKind.Image, result.Snapshot?.Kind);
        Assert.Equal("0E3AECC82B10453FED444E77469E02D8162846ACCDA693F65AEADFFBF6998BFE", result.Snapshot?.ImageContentHash);
        Assert.Equal([100u, 101u], native.ImagePayloadReadOrder);
    }

    [Fact]
    public async Task WriteImageAsync_publishes_png_and_dib_formats_and_private_origin_marker()
    {
        var native = new FakeClipboardNativeApi();
        using var reader = new NativeClipboardReader(native);
        string tempFile = Path.Combine(Path.GetTempPath(), $"cm-clipboard-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(tempFile, Convert.FromBase64String(Png));
            await reader.WriteImageAsync(tempFile, CancellationToken.None);

            Assert.Contains(native.Writes, item => item.Format == 100);
            Assert.Contains(native.Writes, item => item.Format == 101);
            Assert.Contains(native.Writes, item => item.Format == 17);
            Assert.Contains(native.Writes, item => item.Format == 8);
            Assert.Contains(native.Writes, item => item.Format == 999);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public async Task ReadAsync_makes_cf_bitmap_pixels_opaque_like_the_legacy_reader()
    {
        var codec = new WindowsImageCodec();
        var transparentPixel = WindowsImageCodec.FromBgraPixels(1, 1, [0, 0, 255, 0]);
        var native = new FakeClipboardNativeApi
        {
            BitmapDibV5 = codec.EncodeDibV5(transparentPixel)
        };
        native.Available.Add(2);
        using var reader = new NativeClipboardReader(native, codec);

        var result = await reader.ReadAsync(CancellationToken.None);

        var opaquePixel = WindowsImageCodec.FromBgraPixels(1, 1, [0, 0, 255, 255]);
        Assert.Equal(ClipboardContentKind.Image, result.Snapshot?.Kind);
        Assert.Equal(opaquePixel.ContentHash, result.Snapshot?.ImageContentHash);
    }
    [Fact]
    public async Task ReadAsync_makes_bi_rgb_dib_pixels_opaque_like_the_legacy_reader()
    {
        var codec = new WindowsImageCodec();
        var transparentPixel = WindowsImageCodec.FromBgraPixels(1, 1, [0, 0, 255, 0]);
        var native = new FakeClipboardNativeApi();
        native.Available.Add(8);
        native.Data[8] = codec.EncodeDib(transparentPixel);
        using var reader = new NativeClipboardReader(native, codec);

        var result = await reader.ReadAsync(CancellationToken.None);

        var opaquePixel = WindowsImageCodec.FromBgraPixels(1, 1, [0, 0, 255, 255]);
        Assert.Equal(ClipboardContentKind.Image, result.Snapshot?.Kind);
        Assert.Equal(opaquePixel.ContentHash, result.Snapshot?.ImageContentHash);
    }
    private sealed class FakeClipboardNativeApi : IClipboardNativeApi
    {
        private readonly Dictionary<string, uint> _registered = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ClipboardManager.PrivateOrigin.v1"] = 999,
            ["PNG"] = 100,
            ["image/png"] = 101,
            ["JFIF"] = 102,
            ["JPEG"] = 103,
            ["image/jpeg"] = 104,
            ["BMP"] = 105,
            ["image/bmp"] = 106
        };

       public HashSet<uint> Available { get; } = [];
       public Dictionary<uint, byte[]> Data { get; } = [];
       public List<(uint Format, byte[] Data)> Writes { get; } = [];
       public List<uint> ImagePayloadReadOrder { get; } = [];
       public int OpenFailuresRemaining { get; set; }
       public int OpenCalls { get; private set; }
       public int CloseCalls { get; private set; }
       public int EmptyCalls { get; private set; }
       public byte[]? BitmapDibV5 { get; init; }

       public bool OpenClipboard(IntPtr owner)
       {
           OpenCalls++;
           if (OpenFailuresRemaining > 0) { OpenFailuresRemaining--; return false; }
           return true;
       }

       public bool CloseClipboard() { CloseCalls++; return true; }
       public bool EmptyClipboard() { EmptyCalls++; Available.Clear(); Data.Clear(); return true; }
       public uint GetSequenceNumber() => 77;
       public uint RegisterClipboardFormat(string name) => _registered[name];
       public bool IsClipboardFormatAvailable(uint format) => Available.Contains(format);
       public IntPtr GetClipboardDataHandle(uint format) => format == 2 && BitmapDibV5 is not null ? new IntPtr(1) : IntPtr.Zero;

       public byte[] ReadGlobalBytes(uint format, int maxBytes)
       {
           if (format is >= 100 and <= 106) ImagePayloadReadOrder.Add(format);
           if (!Data.TryGetValue(format, out byte[]? bytes)) throw new InvalidDataException("format unavailable");
           if (bytes.Length > maxBytes) throw new InvalidDataException("payload too large");
           return bytes.ToArray();
       }

       public void WriteGlobalBytes(uint format, ReadOnlyMemory<byte> bytes)
       {
           byte[] copy = bytes.ToArray();
           Writes.Add((format, copy));
           Data[format] = copy;
           Available.Add(format);
       }

       public byte[] CopyBitmapAsDibV5(IntPtr bitmapHandle, long maxPixels) => BitmapDibV5 ?? throw new NotSupportedException();
       public IReadOnlyList<string> ReadDroppedFiles(IntPtr dropHandle, int maxFiles) => [];
   }
}

