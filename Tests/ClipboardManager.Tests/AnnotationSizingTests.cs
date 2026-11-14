using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Controls;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class AnnotationSizingTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public void FontSizeInputResizesOnlyTheSelectedText()
    {
        wpf.Run(() =>
        {
            var editor = Editor();
            try
            {
                editor.ImageCanvas.AddText("字号", new Point(30, 30), Colors.Red, 20);
                editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(20, 150), new Point(220, 150), Colors.Blue));
                Rect before = ImageAnnotationRenderer.GetBounds(editor.ImageCanvas.Annotations[0]);
                Box(editor, "FontSizeBox").Text = "48";
                var text = Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.Annotations[0]);
                Assert.Equal(48, text.FontSize);
                Assert.Equal(Colors.Red, text.Color);
                Assert.Equal(new Point(30, 30), text.Position);
                Assert.True(ImageAnnotationRenderer.GetBounds(text).Height > before.Height * 2);
                Assert.Equal(4, Assert.IsType<ArrowImageAnnotation>(editor.ImageCanvas.Annotations[1]).Thickness);
            }
            finally { editor.Close(); }
        });
    }

    [Fact]
    public void ArrowThicknessInputResizesTheSelectedArrowWithoutMovingItsEndpoints()
    {
        wpf.Run(() =>
        {
            var editor = Editor();
            try
            {
                var original = new ArrowImageAnnotation(new Point(20, 100), new Point(240, 100), Colors.Blue, 4);
                editor.ImageCanvas.Annotations.Add(original);
                editor.ImageCanvas.SelectedIndex = 0;
                Box(editor, "ArrowThicknessBox").Text = "12";
                var resized = Assert.IsType<ArrowImageAnnotation>(Assert.Single(editor.ImageCanvas.Annotations));
                Assert.Equal(12, resized.Thickness);
                Assert.Equal(original.Start, resized.Start);
                Assert.Equal(original.End, resized.End);
                Assert.Equal(original.Color, resized.Color);
                Assert.True(ImageAnnotationRenderer.GetBounds(resized).Height > ImageAnnotationRenderer.GetBounds(original).Height * 2);
            }
            finally { editor.Close(); }
        });
    }

    [Fact]
    public void SizeButtonsAdjustTheSelectedAnnotationAndSelectionRestoresItsSize()
    {
        wpf.Run(() =>
        {
            var editor = Editor();
            try
            {
                editor.ImageCanvas.AddText("大小", new Point(20, 20), Colors.Red, 20);
                Click(editor, "IncreaseFontSizeButton");
                Assert.Equal(22, Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.SelectedAnnotation).FontSize);
                Click(editor, "DecreaseFontSizeButton");
                Assert.Equal(20, Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.SelectedAnnotation).FontSize);
                editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(20, 120), new Point(200, 120), Colors.Blue, 7));
                editor.ImageCanvas.SelectedIndex = 1;
                Assert.Equal("7", Box(editor, "ArrowThicknessBox").Text);
                Click(editor, "IncreaseArrowThicknessButton");
                Assert.Equal(8, Assert.IsType<ArrowImageAnnotation>(editor.ImageCanvas.SelectedAnnotation).Thickness);
                Click(editor, "DecreaseArrowThicknessButton");
                Assert.Equal(7, Assert.IsType<ArrowImageAnnotation>(editor.ImageCanvas.SelectedAnnotation).Thickness);
                editor.ImageCanvas.SelectedIndex = 0;
                Assert.Equal("20", Box(editor, "FontSizeBox").Text);
            }
            finally { editor.Close(); }
        });
    }

    [Theory]
    [InlineData("FontSizeBox", "0")]
    [InlineData("FontSizeBox", "NaN")]
    [InlineData("FontSizeBox", "9999")]
    [InlineData("ArrowThicknessBox", "-1")]
    [InlineData("ArrowThicknessBox", "Infinity")]
    [InlineData("ArrowThicknessBox", "9999")]
    public void InvalidSizeBlocksSaveAndCorrectionKeepsTheAnnotation(string name, string invalid)
    {
        wpf.Run(() =>
        {
            var editor = Editor();
            try
            {
                if (name == "FontSizeBox") editor.ImageCanvas.AddText("测试", new Point(20, 20), Colors.Red, 20);
                else
                {
                    editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(20, 100), new Point(200, 100), Colors.Blue));
                    editor.ImageCanvas.SelectedIndex = 0;
                }
                ImageAnnotation before = editor.ImageCanvas.SelectedAnnotation!;
                Box(editor, name).Text = invalid;
                Assert.False(editor.SaveButton.IsEnabled);
                Assert.Equal(before, Assert.Single(editor.ImageCanvas.Annotations));
                Box(editor, name).Text = name == "FontSizeBox" ? "32" : "6";
                Assert.True(editor.SaveButton.IsEnabled);
                Assert.Single(editor.ImageCanvas.Annotations);
            }
            finally { editor.Close(); }
        });
    }

    [Fact]
    public void MovingArrowEndpointsChangesLengthAndDirectionAndClampsToTheImage()
    {
        wpf.Run(() =>
        {
            var canvas = Canvas();
            MoveEndpoint(canvas, "End", new Point(250, 160));
            var changed = Assert.IsType<ArrowImageAnnotation>(canvas.SelectedAnnotation);
            Assert.Equal(new Point(20, 40), changed.Start);
            Assert.Equal(new Point(250, 160), changed.End);
            MoveEndpoint(canvas, "Start", new Point(280, 180));
            changed = Assert.IsType<ArrowImageAnnotation>(canvas.SelectedAnnotation);
            Assert.Equal(new Point(280, 180), changed.Start);
            Assert.Equal(new Point(250, 160), changed.End);
            Assert.Equal(Colors.Green, changed.Color);
            Assert.Equal(4, changed.Thickness);
            MoveEndpoint(canvas, "End", new Point(-50, 500));
            changed = Assert.IsType<ArrowImageAnnotation>(canvas.SelectedAnnotation);
            Assert.Equal(new Point(0, 200), changed.End);
        });
    }

    [Fact]
    public void ArrowHandlesHaveConsistentScreenHitAreasInScaledPreviews()
    {
        wpf.Run(() =>
        {
            var canvas = Canvas();
            canvas.DisplayScale = 0.25;
            Assert.Equal("Start", HitEndpoint(canvas, new Point(20, 68)));
            Assert.Equal("End", HitEndpoint(canvas, new Point(180, 68)));
            Assert.Equal("None", HitEndpoint(canvas, new Point(20, 80)));
            canvas.SelectedIndex = -1;
            Assert.Equal("None", HitEndpoint(canvas, new Point(20, 40)));
        });
    }

    [Fact]
    public void AnEndpointCannotCollapseTheArrowToZeroLength()
    {
        wpf.Run(() =>
        {
            var canvas = Canvas();
            ImageAnnotation original = canvas.SelectedAnnotation!;
            MoveEndpoint(canvas, "End", new Point(20, 40));
            Assert.Equal(original, canvas.SelectedAnnotation);
            Assert.NotNull(ImageAnnotationRenderer.Render(Image(), canvas.Annotations));
        });
    }

    [Fact]
    public void SavedImageUsesTheAdjustedDirectionAndThicknessAndOmitsEditHandles()
    {
        wpf.Run(() =>
        {
            BitmapSource? saved = null;
            var editor = new ImageEditorWindow(Image(), image => { saved = image; return Task.CompletedTask; });
            editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(100, 40), new Point(240, 40), Colors.Green));
            editor.ImageCanvas.SelectedIndex = 0;
            MoveEndpoint(editor.ImageCanvas, "End", new Point(100, 170));
            Box(editor, "ArrowThicknessBox").Text = "12";
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(saved);
            Assert.Equal(300, saved.PixelWidth);
            Assert.Equal(200, saved.PixelHeight);
            Assert.Equal(Colors.Green, Pixel(saved, 105, 90));
            Assert.Equal(0, Pixel(saved, 180, 40).A);
            Assert.Equal(Colors.Green, Pixel(saved, 100, 40));
            Assert.Equal(0, Pixel(saved, 100, 31).A);
        });
    }

    [Theory]
    [InlineData(AppTheme.Light, 944, 660)]
    [InlineData(AppTheme.Dark, 944, 660)]
    [InlineData(AppTheme.Light, 604, 400)]
    [InlineData(AppTheme.Dark, 604, 400)]
    public void SizingControlsAndSaveRemainUsableInBothThemesAndNarrowWindows(AppTheme theme, int width, int height)
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(theme);
            var source = BitmapSource.Create(1280, 900, 96, 96, PixelFormats.Bgra32, null, new byte[1280 * 900 * 4], 1280 * 4);
            source.Freeze();
            var editor = new ImageEditorWindow(source, _ => Task.CompletedTask);
            try
            {
                editor.ImageCanvas.AddText("字号可调整", new Point(100, 120), Colors.Red, 60);
                editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(180, 400), new Point(900, 650), Colors.Orange, 8));
                editor.ImageCanvas.SelectedIndex = 1;
                editor.SelectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var content = (FrameworkElement)editor.Content;
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                foreach (FrameworkElement control in new FrameworkElement[] { Box(editor, "FontSizeBox"), Box(editor, "ArrowThicknessBox"), editor.SaveButton })
                {
                    Point origin = control.TranslatePoint(new Point(), content);
                    Assert.True(origin.X >= 0 && origin.Y >= 0 && origin.X + control.ActualWidth <= width + 1 && origin.Y + control.ActualHeight <= height + 1,
                        $"Sizing control {control.Name} is clipped in {width} x {height}.");
                }
                Assert.True(Box(editor, "FontSizeBox").ExtentWidth <= Box(editor, "FontSizeBox").ViewportWidth + 1);
                Assert.True(Box(editor, "ArrowThicknessBox").ExtentWidth <= Box(editor, "ArrowThicknessBox").ViewportWidth + 1);
                Assert.True(editor.SaveButton.IsEnabled);
                Assert.True(editor.ImageCanvas.DisplayScale > 0);
                string? output = Environment.GetEnvironmentVariable("CLIPBOARD_ANNOTATION_QA_DIR");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rendered));
                    using var stream = File.Create(Path.Combine(output, $"sizing-{theme}-{width}.png"));
                    encoder.Save(stream);
                }
            }
            finally { editor.Close(); }
        });
    }

    private static ImageEditorWindow Editor() => new(Image(), _ => Task.CompletedTask);
    private static TextBox Box(ImageEditorWindow editor, string name) => Assert.IsType<TextBox>(editor.FindName(name));
    private static void Click(ImageEditorWindow editor, string name) => Assert.IsType<Button>(editor.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static ImageAnnotationCanvas Canvas()
    {
        var canvas = new ImageAnnotationCanvas();
        canvas.SetImage(Image());
        canvas.Annotations.Add(new ArrowImageAnnotation(new Point(20, 40), new Point(180, 40), Colors.Green));
        canvas.SelectedIndex = 0;
        return canvas;
    }

    private static void MoveEndpoint(ImageAnnotationCanvas canvas, string endpoint, Point position) =>
        canvas.MoveArrowEndpoint(Enum.Parse<ArrowEndpoint>(endpoint), position);

    private static string HitEndpoint(ImageAnnotationCanvas canvas, Point point) => canvas.HitTestArrowEndpoint(point).ToString();

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(300, 200, 96, 96, PixelFormats.Bgra32, null, new byte[300 * 200 * 4], 300 * 4);
        image.Freeze();
        return image;
    }

    private static Color Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
}
