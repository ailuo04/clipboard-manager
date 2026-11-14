using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class EditorControlPolishTests(SettingsOverlayWpfFixture wpf)
{
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void InputsHaveOpaqueContrastingBordersAndConsistentHeights(AppTheme theme)
    {
        wpf.Run(() => WithEditor(theme, editor =>
        {
            Layout(editor, 944, 660);
            foreach (Control input in new Control[] { editor.FontFamilyBox, editor.ColorBox })
            {
                var fill = Assert.IsType<SolidColorBrush>(input.Background).Color;
                Assert.Equal(255, fill.A);
                Assert.InRange(input.ActualHeight, 31, 33);
            }
            var stepper = Assert.IsType<Border>(editor.FindName("FontSizeStepper"));
            var border = Assert.IsType<SolidColorBrush>(stepper.BorderBrush).Color;
            var background = Assert.IsType<SolidColorBrush>(stepper.Background).Color;
            Assert.Equal(255, border.A);
            Assert.True(Difference(border, background) >= 20, "The input border is indistinguishable from its fill.");
            Assert.InRange(stepper.ActualHeight, 31, 33);
            Assert.Equal(0, editor.FontSizeBox.BorderThickness.Left);
            foreach (FrameworkElement part in new FrameworkElement[] { editor.DecreaseFontSizeButton, editor.FontSizeBox, editor.IncreaseFontSizeButton })
            {
                Rect bounds = part.TransformToAncestor(stepper).TransformBounds(new Rect(part.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= stepper.ActualWidth + 1);
            }
        }));
    }

    [Fact]
    public void LiveThemeSwitchUpdatesOnlyTheEditorPalette()
    {
        wpf.Run(() => WithEditor(AppTheme.Light, editor =>
        {
            var otherInput = new TextBox { Text = "其他窗口的输入框", Style = (Style)Application.Current.FindResource(typeof(TextBox)) };
            otherInput.Measure(new Size(200, 32));
            otherInput.Arrange(new Rect(0, 0, 200, 32));
            Assert.IsType<LinearGradientBrush>(otherInput.Background);
            Color light = Assert.IsType<SolidColorBrush>(editor.ColorBox.Background).Color;
            ThemeManager.Apply(AppTheme.Dark);
            Color dark = Assert.IsType<SolidColorBrush>(editor.ColorBox.Background).Color;
            Assert.True(light.R > 200 && dark.R < 100);
            Assert.IsType<LinearGradientBrush>(otherInput.Background);
            ThemeManager.Apply(AppTheme.Light);
            Assert.Equal(light, Assert.IsType<SolidColorBrush>(editor.ColorBox.Background).Color);
        }));
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void SelectedToolsRemainDistinctAndSaveHasNoArtificialHighlight(AppTheme theme)
    {
        wpf.Run(() => WithEditor(theme, editor =>
        {
            Color active = Assert.IsType<SolidColorBrush>(editor.TextToolButton.Background).Color;
            Color normal = Assert.IsType<SolidColorBrush>(editor.SelectToolButton.Background).Color;
            Assert.True(Difference(active, normal) >= 15);
            editor.ArrowToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(active, Assert.IsType<SolidColorBrush>(editor.ArrowToolButton.Background).Color);
            Assert.Equal(normal, Assert.IsType<SolidColorBrush>(editor.TextToolButton.Background).Color);
            editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(20, 50), new Point(250, 50), Colors.Red));
            Layout(editor, 944, 660);
            Assert.True(editor.SaveButton.IsEnabled);
            var content = (FrameworkElement)editor.Content;
            var image = Render(content);
            Point origin = editor.SaveButton.TranslatePoint(new Point(), content);
            Color upper = Pixel(image, (int)origin.X + 12, (int)origin.Y + 9);
            Color lower = Pixel(image, (int)origin.X + 12, (int)(origin.Y + editor.SaveButton.ActualHeight) - 10);
            Assert.Equal(upper, lower);
            Assert.True(upper.G > upper.R, "The primary button lost its accent color.");
        }));
    }

    [Fact]
    public void DisabledSaveCaptionRemainsReadableOnTheLightBackground()
    {
        wpf.Run(() => WithEditor(AppTheme.Light, editor =>
        {
            Layout(editor, 944, 660);
            Assert.False(editor.SaveButton.IsEnabled);
            var content = (FrameworkElement)editor.Content;
            var image = Render(content);
            Point origin = editor.SaveButton.TranslatePoint(new Point(), content);
            int darkPixels = 0;
            for (int y = 6; y < editor.SaveButton.ActualHeight - 6; y++)
                for (int x = 6; x < editor.SaveButton.ActualWidth - 6; x++)
                {
                    Color pixel = Pixel(image, (int)origin.X + x, (int)origin.Y + y);
                    if (pixel.R < 180 && pixel.G < 180 && pixel.B < 180) darkPixels++;
                }
            Assert.True(darkPixels >= 15, "The disabled save caption disappears into its background.");
            string? directory = Environment.GetEnvironmentVariable("CLIPBOARD_ANNOTATION_QA_DIR");
            if (!string.IsNullOrEmpty(directory)) Save(content, Path.Combine(directory, "polished-empty-Light.png"));
        }));
    }

    [Theory]
    [InlineData(AppTheme.Light, 944, 660)]
    [InlineData(AppTheme.Dark, 944, 660)]
    [InlineData(AppTheme.Light, 604, 400)]
    [InlineData(AppTheme.Dark, 604, 400)]
    public void PolishedControlsStayAlignedAndReadableInNarrowWindows(AppTheme theme, int width, int height)
    {
        wpf.Run(() => WithEditor(theme, editor =>
        {
            editor.PlaceText(new Point(170, 160));
            editor.AnnotationTextBox.Text = "图片上的文字\r\n直接输入和修改";
            editor.FontSizeBox.Text = "48";
            editor.ColorBox.Text = "#E5484D";
            Layout(editor, width, height);
            var content = (FrameworkElement)editor.Content;
            foreach (FrameworkElement control in new FrameworkElement[] { editor.FontFamilyBox, editor.ColorBox, editor.DeleteButton, editor.SaveButton })
            {
                Rect bounds = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width + 1 && bounds.Bottom <= height + 1, $"{control.Name} is clipped.");
            }
            Assert.True(editor.Toolbar.ActualHeight <= 122);
            Assert.True(editor.Viewport.ActualHeight >= height * .40);
            Assert.True(editor.ColorBox.ExtentWidth <= editor.ColorBox.ViewportWidth + 1);
            string? directory = Environment.GetEnvironmentVariable("CLIPBOARD_ANNOTATION_QA_DIR");
            if (!string.IsNullOrEmpty(directory)) Save(content, Path.Combine(directory, $"polished-inline-{theme}-{width}.png"));
            editor.ArrowToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(editor, width, height);
            var shapeStepper = Assert.IsType<Border>(editor.FindName("ArrowThicknessStepper"));
            Assert.InRange(shapeStepper.ActualHeight, 31, 33);
            if (!string.IsNullOrEmpty(directory)) Save(content, Path.Combine(directory, $"polished-shape-{theme}-{width}.png"));
        }));
    }

    private static int Difference(Color first, Color second) => Math.Max(Math.Abs(first.R - second.R), Math.Max(Math.Abs(first.G - second.G), Math.Abs(first.B - second.B)));
    private static void WithEditor(AppTheme theme, Action<ImageEditorWindow> action)
    {
        ThemeManager.Apply(theme);
        var editor = new ImageEditorWindow(Image(), _ => Task.CompletedTask);
        try { action(editor); }
        finally { editor.Close(); }
    }
    private static void Layout(ImageEditorWindow editor, int width, int height)
    {
        var content = (FrameworkElement)editor.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
    }
    private static RenderTargetBitmap Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }
    private static Color Pixel(BitmapSource source, int x, int y)
    {
        byte[] pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
    private static void Save(FrameworkElement element, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(element)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(1280, 900, 96, 96, PixelFormats.Bgra32, null, new byte[1280 * 900 * 4], 1280 * 4);
        image.Freeze();
        return image;
    }
}
