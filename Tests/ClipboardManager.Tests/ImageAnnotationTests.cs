using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Models;
using ClipboardManager.Services;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class ImageAnnotationTests(SettingsOverlayWpfFixture wpf)
{
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void PrimaryButtonHasNoBrightInsetStripe(AppTheme theme)
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(theme);
            var button = new Button
            {
                Style = (Style)Application.Current.FindResource("PrimaryButton"),
                Width = 224, Height = 30, MinHeight = 30, Padding = new Thickness(10, 5, 10, 5)
            };
            button.Measure(new Size(224, 30));
            button.Arrange(new Rect(0, 0, 224, 30));
            button.UpdateLayout();
            var rendered = new RenderTargetBitmap(224, 30, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(button);
            Color upper = Pixel(rendered, 112, 7);
            Color lower = Pixel(rendered, 112, 22);
            Assert.True(upper.R < 90, $"An artificial white stripe remains: {upper}.");
            Assert.InRange(Math.Abs(upper.R - lower.R), 0, 40);
        });
    }

    [Fact]
    public void RenderKeepsOriginalDimensionsAndTransparencyAndAddsColoredArrow()
    {
        wpf.Run(() =>
        {
            BitmapSource source = TransparentImage(160, 100);
            var arrow = new ArrowImageAnnotation(new Point(20, 25), new Point(130, 25), Colors.Blue, 4);
            BitmapSource result = ImageAnnotationRenderer.Render(source, [arrow]);
            Assert.Equal(160, result.PixelWidth);
            Assert.Equal(100, result.PixelHeight);
            Assert.Equal(Colors.Blue, Pixel(result, 60, 25));
            Assert.Equal(Colors.Blue, Pixel(result, 126, 25));
            Assert.Equal(0, Pixel(result, 10, 80).A);
            Assert.Equal(0, Pixel(source, 60, 25).A);
            Assert.True(result.IsFrozen);
        });
    }

    [Fact]
    public void TextIsRenderedInItsOwnColorAtTheRequestedPixelPosition()
    {
        wpf.Run(() =>
        {
            BitmapSource source = TransparentImage(300, 160);
            var text = new TextImageAnnotation("自定义文字", new Point(40, 55), Colors.Red, 26);
            BitmapSource result = ImageAnnotationRenderer.Render(source, [text]);
            bool foundRed = false;
            for (int y = 55; y < 110; y++)
                for (int x = 40; x < 210; x++)
                {
                    Color pixel = Pixel(result, x, y);
                    if (pixel.A > 100 && pixel.R > 100 && pixel.G == 0 && pixel.B == 0) foundRed = true;
                }
            Assert.True(foundRed, "Text was not drawn in red at the requested position.");
            Assert.Equal(0, Pixel(result, 10, 10).A);
            Assert.Equal(0, Pixel(source, 60, 70).A);
        });
    }

    [Fact]
    public async Task SavingAnAnnotatedImageCreatesANewHistoryRecordAndLeavesOriginalFileUnchanged()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClipboardManager.AnnotationTests", Guid.NewGuid().ToString("N"));
        try
        {
            BitmapSource? source = null;
            BitmapSource? edited = null;
            wpf.Run(() =>
            {
                source = TransparentImage(160, 100);
                edited = ImageAnnotationRenderer.Render(source,
                    [new ArrowImageAnnotation(new Point(20, 25), new Point(130, 25), Colors.Green, 4)]);
            });
            var history = new HistoryService(new HistoryStore(root));
            await history.LoadAsync();
            var original = await history.AddAsync(ClipboardSnapshot.FromImage(source!, "test"));
            string originalPath = history.GetImagePath(original.Record!);
            byte[] originalBytes = await File.ReadAllBytesAsync(originalPath);
            var saved = await history.AddAsync(ClipboardSnapshot.FromImage(edited!, "ImageAnnotation"));
            Assert.NotEqual(original.Record!.Id, saved.Record!.Id);
            Assert.Equal(2, history.Records.Count);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalPath));
            var reloaded = new HistoryService(new HistoryStore(root));
            await reloaded.LoadAsync();
            Assert.Equal(2, reloaded.Records.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static BitmapSource TransparentImage(int width, int height)
    {
        var source = BitmapSource.Create(width, height, 192, 192, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        source.Freeze();
        return source;
    }

    private static Color Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
