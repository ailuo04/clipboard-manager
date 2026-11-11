using ClipboardManager.Windows.Clipboard;
using Xunit;

namespace ClipboardManager.Windows.Tests;

public sealed class ClipboardFormatTests
{
    [Fact]
    public void Image_format_priority_matches_legacy_png_then_jpeg_then_bmp_order()
    {
        var formats = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["PNG"] = 100,
            ["image/png"] = 101,
            ["JFIF"] = 102,
            ["JPEG"] = 103,
            ["image/jpeg"] = 104,
            ["BMP"] = 105,
            ["image/bmp"] = 106
        };

        Assert.Equal([100u, 101u, 102u, 103u, 104u, 105u, 106u], ClipboardFormatPolicy.ImageFormatPriority(formats));
    }

    [Fact]
    public void Duplicate_registered_format_ids_are_considered_once_at_first_priority()
    {
        var formats = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["PNG"] = 100,
            ["image/png"] = 100,
            ["JFIF"] = 200,
            ["JPEG"] = 200,
            ["image/jpeg"] = 300,
            ["BMP"] = 400,
            ["image/bmp"] = 400
        };

        Assert.Equal([100u, 200u, 300u, 400u], ClipboardFormatPolicy.ImageFormatPriority(formats));
    }

    [Fact]
    public void SelectBestAvailable_returns_first_format_in_defined_priority()
    {
        uint? selected = ClipboardFormatPolicy.SelectBestAvailable([101u, 105u, 13u], new HashSet<uint> { 101u, 105u });

        Assert.Equal(101u, selected);
    }

    [Fact]
    public void Clipboard_limits_match_data_boundary_contract()
    {
        Assert.Equal(128 * 1024 * 1024, ClipboardFormatPolicy.MaxImageBytes);
        Assert.Equal(8 * 1024 * 1024, ClipboardFormatPolicy.MaxTextBytes);
        Assert.Equal(40_000_000, ClipboardFormatPolicy.MaxImagePixels);
    }
}
