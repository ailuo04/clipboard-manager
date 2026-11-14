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
public sealed class RectangleAnnotationTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public void RectangleRendersItsColoredBorderAndLeavesTheInteriorTransparent()
    {
        wpf.Run(() =>
        {
            BitmapSource source = Image();
            var rectangle = new RectangleImageAnnotation(new Rect(40, 30, 160, 100), Colors.Red, 4);
            BitmapSource rendered = ImageAnnotationRenderer.Render(source, [rectangle]);
            Assert.Equal(Colors.Red, Pixel(rendered, 40, 70));
            Assert.Equal(Colors.Red, Pixel(rendered, 100, 30));
            Assert.Equal(Colors.Red, Pixel(rendered, 200, 70));
            Assert.Equal(0, Pixel(rendered, 100, 80).A);
            Assert.Equal(0, Pixel(rendered, 20, 20).A);
            Assert.Equal(0, Pixel(source, 40, 70).A);
            Assert.Equal(300, rendered.PixelWidth);
            Assert.Equal(200, rendered.PixelHeight);
        });
    }

    [Theory]
    [InlineData(190, 120, 40, 20, 40, 20, 150, 100)]
    [InlineData(-20, -10, 350, 240, 0, 0, 300, 200)]
    public void RectangleDrawingNormalizesBothDirectionsAndClampsToTheImage(
        double x1, double y1, double x2, double y2, double left, double top, double width, double height)
    {
        wpf.Run(() =>
        {
            var canvas = Canvas();
            Assert.True(Add(canvas, new Point(x1, y1), new Point(x2, y2)));
            var rectangle = Assert.IsType<RectangleImageAnnotation>(Assert.Single(canvas.Annotations));
            Assert.Equal(new Rect(left, top, width, height), rectangle.Bounds);
            Assert.Equal(Colors.Blue, rectangle.Color);
            Assert.Equal(4, rectangle.Thickness);
            Assert.Equal(0, canvas.SelectedIndex);
        });
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(30, 0)]
    [InlineData(1, 1)]
    public void NegligibleRectangleDragsDoNotCreateAnAnnotation(int width, int height)
    {
        wpf.Run(() =>
        {
            var canvas = Canvas();
            Assert.False(Add(canvas, new Point(30, 30), new Point(30 + width, 30 + height)));
            Assert.Empty(canvas.Annotations);
        });
    }

    [Fact]
    public void RectangleHitTestingUsesTheBorderSoInteriorAnnotationsRemainAccessible()
    {
        wpf.Run(() =>
        {
            var canvas = Canvas();
            canvas.Annotations.Add(new RectangleImageAnnotation(new Rect(40, 30, 160, 100), Colors.Blue));
            Assert.Equal(0, canvas.HitTestAnnotation(new Point(40, 80)));
            Assert.Equal(-1, canvas.HitTestAnnotation(new Point(100, 80)));
            canvas.AddText("文字", new Point(70, 60), Colors.Red, 20);
            Assert.Equal(1, canvas.HitTestAnnotation(new Point(75, 65)));
        });
    }

    [Fact]
    public void MovingRectangleKeepsItsSizeAndStyleAndClampsItsPosition()
    {
        wpf.Run(() =>
        {
            var canvas = Canvas();
            canvas.Annotations.Add(new RectangleImageAnnotation(new Rect(40, 30, 160, 100), Colors.Blue, 6));
            canvas.SelectedIndex = 0;
            canvas.MoveSelection(new Vector(500, -100));
            var rectangle = Assert.IsType<RectangleImageAnnotation>(canvas.SelectedAnnotation);
            Assert.Equal(new Rect(140, 0, 160, 100), rectangle.Bounds);
            Assert.Equal(Colors.Blue, rectangle.Color);
            Assert.Equal(6, rectangle.Thickness);
        });
    }

    [Fact]
    public void CornerResizeKeepsTheOppositeCornerFixedEvenWhenDraggingAcrossIt()
    {
        wpf.Run(() =>
        {
            var canvas = SelectedRectangle();
            Assert.Equal("BottomRight", Resize(canvas, "BottomRight", new Point(260, 170)));
            Assert.Equal(new Rect(40, 30, 220, 140), Assert.IsType<RectangleImageAnnotation>(canvas.SelectedAnnotation).Bounds);
            string active = Resize(canvas, "TopLeft", new Point(280, 190));
            Assert.Equal("BottomRight", active);
            Assert.Equal(new Rect(260, 170, 20, 20), Assert.IsType<RectangleImageAnnotation>(canvas.SelectedAnnotation).Bounds);
            Resize(canvas, active, new Point(290, 195));
            Assert.Equal(new Rect(260, 170, 30, 25), Assert.IsType<RectangleImageAnnotation>(canvas.SelectedAnnotation).Bounds);
        });
    }

    [Fact]
    public void CornerResizeRejectsCollapsedFramesAndClampsAtTheImageBoundary()
    {
        wpf.Run(() =>
        {
            var canvas = SelectedRectangle();
            var original = Assert.IsType<RectangleImageAnnotation>(canvas.SelectedAnnotation);
            canvas.ResizeRectangleCorner(RectangleCorner.BottomRight, new Point(40, 150));
            Assert.Equal(original, canvas.SelectedAnnotation);
            canvas.ResizeRectangleCorner(RectangleCorner.TopLeft, new Point(-60, -20));
            Assert.Equal(new Rect(0, 0, 200, 130), Assert.IsType<RectangleImageAnnotation>(canvas.SelectedAnnotation).Bounds);
        });
    }

    [Fact]
    public void CornerHitAreasRemainUsableInAScaledPreviewAndRequireSelection()
    {
        wpf.Run(() =>
        {
            var canvas = SelectedRectangle();
            canvas.DisplayScale = 0.25;
            Assert.Equal("TopLeft", HitCorner(canvas, new Point(40, 58)));
            Assert.Equal("BottomRight", HitCorner(canvas, new Point(228, 130)));
            Assert.Equal("None", HitCorner(canvas, new Point(40, 70)));
            canvas.SelectedIndex = -1;
            Assert.Equal("None", HitCorner(canvas, new Point(40, 30)));
        });
    }

    [Fact]
    public void RectangleColorAndLineWidthCanBeEditedAndSavedWithoutControlHandles()
    {
        wpf.Run(() =>
        {
            BitmapSource? saved = null;
            var editor = new ImageEditorWindow(Image(), image => { saved = image; return Task.CompletedTask; });
            editor.ImageCanvas.Annotations.Add(new RectangleImageAnnotation(new Rect(40, 30, 160, 100), Colors.Blue));
            editor.ImageCanvas.SelectedIndex = 0;
            Assert.True(editor.ArrowThicknessBox.IsEnabled);
            editor.ColorBox.Text = "#12AB34";
            editor.ArrowThicknessBox.Text = "10";
            var rectangle = Assert.IsType<RectangleImageAnnotation>(editor.ImageCanvas.SelectedAnnotation);
            Assert.Equal(Color.FromRgb(18, 171, 52), rectangle.Color);
            Assert.Equal(10, rectangle.Thickness);
            Assert.True(editor.SaveButton.IsEnabled);
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(saved);
            Assert.Equal(Color.FromRgb(18, 171, 52), Pixel(saved, 40, 70));
            Assert.Equal(0, Pixel(saved, 100, 80).A);
            Assert.Equal(0, Pixel(saved, 30, 70).A);
        });
    }

    [Fact]
    public void RectangleToolEnablesShapeControlsAndOnlyAllowsSavingAfterDrawing()
    {
        wpf.Run(() =>
        {
            var editor = new ImageEditorWindow(Image(), _ => Task.CompletedTask);
            try
            {
                var button = Assert.IsType<Button>(editor.FindName("RectangleToolButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(editor.ArrowThicknessBox.IsEnabled);
                Assert.False(editor.FontSizeBox.IsEnabled);
                Assert.False(editor.SaveButton.IsEnabled);
                Add(editor.ImageCanvas, new Point(30, 30), new Point(200, 150));
                Assert.True(editor.SaveButton.IsEnabled);
            }
            finally { editor.Close(); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Light, 944, 660)]
    [InlineData(AppTheme.Dark, 944, 660)]
    [InlineData(AppTheme.Light, 604, 400)]
    [InlineData(AppTheme.Dark, 604, 400)]
    public void RectangleControlsFitBothThemesAndNarrowWindows(AppTheme theme, int width, int height)
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(theme);
            var editor = new ImageEditorWindow(Image(), _ => Task.CompletedTask);
            try
            {
                editor.ImageCanvas.AddText("矩形标注", new Point(60, 60), Colors.Red, 20);
                editor.ImageCanvas.Annotations.Add(new RectangleImageAnnotation(new Rect(40, 40, 210, 120), Colors.Orange, 4));
                editor.ImageCanvas.SelectedIndex = 1;
                editor.SelectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var content = (FrameworkElement)editor.Content;
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                var rectangleTool = Assert.IsType<Button>(editor.FindName("RectangleToolButton"));
                foreach (FrameworkElement control in new FrameworkElement[] { rectangleTool, editor.DeleteButton, editor.ArrowThicknessBox, editor.SaveButton })
                {
                    Point origin = control.TranslatePoint(new Point(), content);
                    Assert.True(origin.X >= 0 && origin.Y >= 0 && origin.X + control.ActualWidth <= width + 1 && origin.Y + control.ActualHeight <= height + 1,
                        $"Rectangle control {control.Name} is clipped.");
                }
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
                    using var stream = File.Create(Path.Combine(output, $"rectangle-{theme}-{width}.png"));
                    encoder.Save(stream);
                }
            }
            finally { editor.Close(); }
        });
    }

    private static bool Add(ImageAnnotationCanvas canvas, Point start, Point end) => canvas.AddRectangle(start, end, Colors.Blue, 4);
    private static string HitCorner(ImageAnnotationCanvas canvas, Point point) => canvas.HitTestRectangleCorner(point).ToString();
    private static string Resize(ImageAnnotationCanvas canvas, string corner, Point point) =>
        canvas.ResizeRectangleCorner(Enum.Parse<RectangleCorner>(corner), point).ToString();

    private static ImageAnnotationCanvas Canvas() { var canvas = new ImageAnnotationCanvas(); canvas.SetImage(Image()); return canvas; }
    private static ImageAnnotationCanvas SelectedRectangle()
    {
        var canvas = Canvas();
        canvas.Annotations.Add(new RectangleImageAnnotation(new Rect(40, 30, 160, 100), Colors.Blue));
        canvas.SelectedIndex = 0;
        return canvas;
    }
    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(300, 200, 96, 96, PixelFormats.Bgra32, null, new byte[300 * 200 * 4], 1200);
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
