using ClipboardManager.Windows.Clipboard;
using ClipboardManager.Windows.Images;
using Xunit;

namespace ClipboardManager.Windows.Tests;

public sealed class ImageCodecTests
{
    private const string RedPixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    private const string RedPixelAlternatePng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4AWP4z8DwHwAFAAH/e+m+7wAAAABJRU5ErkJggg==";

    [Fact]
    public async Task DecodeAsync_normalizes_equivalent_png_encodings_to_legacy_bgra_hash()
    {
        var codec = new WindowsImageCodec();
        var first = await codec.DecodeAsync(Convert.FromBase64String(RedPixelPng), CancellationToken.None);
        var second = await codec.DecodeAsync(Convert.FromBase64String(RedPixelAlternatePng), CancellationToken.None);

        Assert.Equal(1, first.Width);
        Assert.Equal(1, first.Height);
        Assert.Equal([0, 0, 255, 255], first.BgraPixels.ToArray());
        Assert.Equal("0E3AECC82B10453FED444E77469E02D8162846ACCDA693F65AEADFFBF6998BFE", first.ContentHash);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.BgraPixels.ToArray(), second.BgraPixels.ToArray());
    }

    [Fact]
    public async Task DecodeAsync_png_output_round_trips_with_identical_pixels()
    {
        var codec = new WindowsImageCodec();
        var original = await codec.DecodeAsync(Convert.FromBase64String(RedPixelPng), CancellationToken.None);

        byte[] reencoded = await codec.EncodePngAsync(original, CancellationToken.None);
        var decodedAgain = await codec.DecodeAsync(reencoded, CancellationToken.None);

        Assert.Equal(original.Width, decodedAgain.Width);
        Assert.Equal(original.Height, decodedAgain.Height);
        Assert.Equal(original.ContentHash, decodedAgain.ContentHash);
        Assert.Equal(original.BgraPixels.ToArray(), decodedAgain.BgraPixels.ToArray());
    }

    [Fact]
    public async Task DecodeAsync_dib_and_dibv5_round_trip_dimensions_and_pixels()
    {
        var codec = new WindowsImageCodec();
        var original = await codec.DecodeAsync(Convert.FromBase64String(RedPixelPng), CancellationToken.None);

        var dib = codec.EncodeDib(original);
        var dibV5 = codec.EncodeDibV5(original);
        var fromDib = await codec.DecodeAsync(dib, CancellationToken.None);
        var fromDibV5 = await codec.DecodeAsync(dibV5, CancellationToken.None);

        Assert.Equal(original.ContentHash, fromDib.ContentHash);
        Assert.Equal(original.ContentHash, fromDibV5.ContentHash);
        Assert.Equal(40 + 4, dib.Length);
        Assert.Equal(124 + 4, dibV5.Length);
    }

    [Fact]
    public async Task DecodeAsync_rejects_corrupt_input_with_a_classified_error()
    {
        var codec = new WindowsImageCodec();

        var error = await Assert.ThrowsAsync<ImageCodecException>(() => codec.DecodeAsync(new byte[] { 1, 2, 3, 4 }, CancellationToken.None));

        Assert.Equal(ImageCodecError.InvalidData, error.Error);
    }

    [Fact]
    public void Size_limits_reject_oversized_clipboard_images_and_more_than_40_megapixels()
    {
        Assert.Throws<ImageCodecException>(() => WindowsImageCodec.ValidateEncodedSize(ClipboardFormatPolicy.MaxImageBytes + 1L));
        Assert.Throws<ImageCodecException>(() => WindowsImageCodec.ValidateDimensions(8000, 5001));
        Assert.Throws<ImageCodecException>(() => WindowsImageCodec.ValidateTextSize(ClipboardFormatPolicy.MaxTextBytes + 1L));
    }
}
