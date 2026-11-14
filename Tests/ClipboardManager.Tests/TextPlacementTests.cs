using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class TextPlacementTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public void FontSelectorOffersInstalledCommonChineseAndLatinFonts()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            ComboBox fonts = FontsBox(editor);
            foreach (string family in new[] { "SimSun", "Microsoft YaHei", "SimHei", "KaiTi", "Times New Roman", "Arial" })
            {
                fonts.SelectedValue = family;
                Assert.Equal(family, fonts.SelectedValue);
            }
            Assert.False(fonts.IsEditable);
        }));
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void FontSelectorDisplaysTheSelectedFontName(AppTheme theme)
    {
        wpf.Run(() => WithEditor(editor =>
        {
            ThemeManager.Apply(theme);
            ComboBox fonts = FontsBox(editor);
            fonts.SelectedValue = "Times New Roman";
            var content = (FrameworkElement)editor.Content;
            content.Measure(new Size(944, 660));
            content.Arrange(new Rect(0, 0, 944, 660));
            content.UpdateLayout();
            Assert.Contains(VisualChildren(fonts).OfType<TextBlock>(), label => label.Text == "Times New Roman");
        }));
    }
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void FontDropdownRendersReadableChoiceLabels(AppTheme theme)
    {
        wpf.Run(() => WithEditor(editor =>
        {
            ThemeManager.Apply(theme);
            ComboBox fonts = FontsBox(editor);
            fonts.Measure(new Size(176, 48));
            fonts.Arrange(new Rect(0, 0, 176, 48));
            fonts.ApplyTemplate();
            var popup = Assert.IsType<Popup>(fonts.Template.FindName("PART_Popup", fonts));
            var choices = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            choices.Measure(new Size(220, 300));
            choices.Arrange(new Rect(new Point(), choices.DesiredSize));
            choices.UpdateLayout();
            var labels = VisualChildren(choices).OfType<TextBlock>().ToArray();
            Assert.Contains(labels, label => label.Text == "宋体 (SimSun)");
            Assert.Contains(labels, label => label.Text == "Times New Roman");
            string? output = Environment.GetEnvironmentVariable("CLIPBOARD_ANNOTATION_QA_DIR");
            if (!string.IsNullOrEmpty(output)) SaveVisual(choices, Path.Combine(output, $"font-dropdown-{theme}.png"));
        }));
    }
    [Fact]
    public void FontSelectionUpdatesOnlySelectedTextAndIsRestoredOnReselection()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.ImageCanvas.AddText("Sample", new Point(20, 20), Colors.Red, 30);
            editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(10, 170), new Point(210, 170), Colors.Blue));
            ImageAnnotation arrow = editor.ImageCanvas.Annotations[1];
            FontsBox(editor).SelectedValue = "Times New Roman";
            var text = Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.Annotations[0]);
            Assert.Equal("Times New Roman", Family(text));
            Assert.Equal("Sample", text.Text);
            Assert.Equal(30, text.FontSize);
            Assert.Equal(new Point(20, 20), text.Position);
            Assert.Equal(Colors.Red, text.Color);
            Assert.Equal(arrow, editor.ImageCanvas.Annotations[1]);
            editor.ImageCanvas.SelectedIndex = 1;
            Assert.False(FontsBox(editor).IsEnabled);
            editor.ImageCanvas.SelectedIndex = 0;
            Assert.Equal("Times New Roman", FontsBox(editor).SelectedValue);
        }));
    }

    [Fact]
    public void SelectedFontChangesBothTextMeasurementAndExportedGlyphs()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.ImageCanvas.AddText("iiiiWW Serif", new Point(20, 20), Colors.Red, 32);
            FontsBox(editor).SelectedValue = "Arial";
            Rect sansBounds = ImageAnnotationRenderer.GetBounds(editor.ImageCanvas.Annotations[0]);
            byte[] sans = Pixels(ImageAnnotationRenderer.Render(Image(), editor.ImageCanvas.Annotations));
            FontsBox(editor).SelectedValue = "Times New Roman";
            Rect serifBounds = ImageAnnotationRenderer.GetBounds(editor.ImageCanvas.Annotations[0]);
            byte[] serif = Pixels(ImageAnnotationRenderer.Render(Image(), editor.ImageCanvas.Annotations));
            Assert.NotEqual(sansBounds.Size, serifBounds.Size);
            Assert.False(sans.SequenceEqual(serif), "The selected font must affect exported glyphs.");
        }));
    }

    [Fact]
    public void EmptyTextHoverShowsAnInsertionIndicatorWithoutCreatingAnAnnotation()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            Pointer(editor, new Point(40, 50));
            var preview = Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.PreviewAnnotation);
            Assert.Equal(new Point(40, 50), preview.Position);
            Assert.True(string.IsNullOrWhiteSpace(preview.Text));
            Assert.Empty(editor.ImageCanvas.Annotations);
            Assert.False(editor.SaveButton.IsEnabled);
            editor.ImageCanvas.Measure(new Size(300, 200));
            editor.ImageCanvas.Arrange(new Rect(0, 0, 300, 200));
            var display = new RenderTargetBitmap(300, 200, 96, 96, PixelFormats.Pbgra32);
            display.Render(editor.ImageCanvas);
            Assert.Contains(Pixels(display), b => b != 0);
            Assert.All(Pixels(ImageAnnotationRenderer.Render(Image(), editor.ImageCanvas.Annotations)), b => Assert.Equal(0, b));
        }));
    }

    [Fact]
    public void InlinePlacementUsesChosenFontSizeColorAndClampsTextAtTheImageEdge()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            Pointer(editor, new Point(298, 198));
            FontsBox(editor).SelectedValue = "Times New Roman";
            editor.FontSizeBox.Text = "30";
            editor.ColorBox.Text = "#2468AC";
            var preview = Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.PreviewAnnotation);
            Assert.True(string.IsNullOrWhiteSpace(preview.Text));
            Assert.Equal("Times New Roman", preview.FontFamily);
            Assert.Empty(editor.ImageCanvas.Annotations);
            Place(editor, new Point(298, 198));
            editor.AnnotationTextBox.Text = "WW Test";
            var text = Assert.IsType<TextImageAnnotation>(Assert.Single(editor.ImageCanvas.Annotations));
            Assert.Equal("Times New Roman", text.FontFamily);
            Assert.Equal(30, text.FontSize);
            Assert.Equal(Color.FromRgb(36, 104, 172), text.Color);
            Assert.True(new Rect(0, 0, 300, 200).Contains(ImageAnnotationRenderer.GetBounds(text)));
            Assert.Null(editor.ImageCanvas.PreviewAnnotation);
            Assert.True(editor.SaveButton.IsEnabled);
            Assert.Equal(Visibility.Visible, editor.TextEditFrame.Visibility);
        }));
    }

    [Fact]
    public void PositionFirstPlacementStaysAnchoredWhenPointerLeavesThenAcceptsLiveText()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            Place(editor, new Point(60, 70));
            editor.ImageCanvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Pointer(editor, null);
            Assert.Null(editor.ImageCanvas.PreviewAnnotation);
            Assert.Equal(Visibility.Visible, editor.TextEditFrame.Visibility);
            Assert.Empty(editor.ImageCanvas.Annotations);
            Assert.False(editor.SaveButton.IsEnabled);
            FontsBox(editor).SelectedValue = "SimSun";
            editor.AnnotationTextBox.Text = "文字";
            editor.AnnotationTextBox.Text = "文字预览";
            var text = Assert.IsType<TextImageAnnotation>(Assert.Single(editor.ImageCanvas.Annotations));
            Assert.Equal("文字预览", text.Text);
            Assert.Equal(new Point(60, 70), text.Position);
            Assert.Equal("SimSun", Family(text));
            Assert.Equal(0, editor.ImageCanvas.SelectedIndex);
            Assert.True(editor.SaveButton.IsEnabled);
        }));
    }

    [Fact]
    public void HoverClearsOnMouseLeaveAndToolSwitchCancelsAnEmptyAnchor()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            Pointer(editor, new Point(30, 40));
            Assert.NotNull(editor.ImageCanvas.PreviewAnnotation);
            editor.ImageCanvas.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            Assert.Null(editor.ImageCanvas.PreviewAnnotation);
            Place(editor, new Point(30, 40));
            editor.ArrowToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(editor.ImageCanvas.PreviewAnnotation);
            editor.AnnotationTextBox.Text = "不能生成残留文字";
            Assert.Empty(editor.ImageCanvas.Annotations);
        }));
    }

    [Fact]
    public void EscapeCancelsPendingTextPositionWithoutClosingEditor()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            bool closed = false;
            editor.Closed += (_, _) => closed = true;
            Place(editor, new Point(20, 30));
            var source = new HwndSource(new HwndSourceParameters("Text placement key test") { Width = 1, Height = 1 });
            try
            {
                editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.False(closed);
                Assert.Null(editor.ImageCanvas.PreviewAnnotation);
                editor.AnnotationTextBox.Text = "取消后输入";
                Assert.Empty(editor.ImageCanvas.Annotations);
            }
            finally { source.Dispose(); }
        }));
    }

    [Fact]
    public void SavingWithATextPreviewExportsOnlyCommittedAnnotations()
    {
        wpf.Run(() =>
        {
            BitmapSource? saved = null;
            var editor = new ImageEditorWindow(Image(), image => { saved = image; return Task.CompletedTask; });
            try
            {
                editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(10, 160), new Point(200, 160), Colors.Red));
                Pointer(editor, new Point(30, 30));
                Assert.NotNull(editor.ImageCanvas.PreviewAnnotation);
                editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotNull(saved);
                byte[] pixels = Pixels(saved);
                Assert.All(pixels.Take(120 * 300 * 4), b => Assert.Equal(0, b));
                Assert.Contains(pixels.Skip(150 * 300 * 4), b => b != 0);
            }
            finally { editor.Close(); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Light, 944, 660)]
    [InlineData(AppTheme.Dark, 944, 660)]
    [InlineData(AppTheme.Light, 604, 400)]
    [InlineData(AppTheme.Dark, 604, 400)]
    public void FontSelectorAndTextPreviewRemainUsableInBothThemesAndNarrowWindows(AppTheme theme, int width, int height)
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(theme);
            var editor = new ImageEditorWindow(Image(1280, 900), _ => Task.CompletedTask);
            try
            {
                FontsBox(editor).SelectedValue = "Times New Roman";
                editor.FontSizeBox.Text = "52";
                Place(editor, new Point(140, 160));
                editor.AnnotationTextBox.Text = "Text preview 文字预览";
                var content = (FrameworkElement)editor.Content;
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                foreach (FrameworkElement control in new FrameworkElement[] { FontsBox(editor), editor.AnnotationTextBox, editor.SaveButton })
                {
                    Rect visibleBounds = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
                    Assert.True(visibleBounds.Left >= 0 && visibleBounds.Top >= 0 && visibleBounds.Right <= width + 1 && visibleBounds.Bottom <= height + 1,
                        $"Control {control.Name} is clipped in {width} x {height}.");
                }
                Assert.True(editor.AnnotationTextBox.ActualWidth * editor.ImageCanvas.DisplayScale > 32);
                Assert.True(editor.Viewport.ActualHeight > 50);
                Assert.True(editor.ImageCanvas.DisplayScale > 0);
                Assert.Equal(Visibility.Visible, editor.TextEditFrame.Visibility);
                string? output = Environment.GetEnvironmentVariable("CLIPBOARD_ANNOTATION_QA_DIR");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rendered));
                    using var stream = File.Create(Path.Combine(output, $"text-placement-{theme}-{width}.png"));
                    encoder.Save(stream);
                    editor.AnnotationTextBox.Clear();
                    editor.FontSizeBox.Text = "6";
                    Place(editor, new Point(1100, 780));
                    editor.UpdateTextPointer(null);
                    content.UpdateLayout();
                    SaveVisual(content, Path.Combine(output, $"text-insertion-{theme}-{width}.png"));
                }
            }
            finally { editor.Close(); }
        });
    }

    private static void SaveVisual(FrameworkElement element, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rendered = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rendered.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in VisualChildren(child)) yield return descendant;
        }
    }

    private static ComboBox FontsBox(ImageEditorWindow editor) => Assert.IsType<ComboBox>(editor.FindName("FontFamilyBox"));
    private static string Family(TextImageAnnotation text) => text.FontFamily;
    private static void Pointer(ImageEditorWindow editor, Point? point) => editor.UpdateTextPointer(point);
    private static void Place(ImageEditorWindow editor, Point point) => editor.PlaceText(point);
    private static void WithEditor(Action<ImageEditorWindow> action)
    {
        var editor = new ImageEditorWindow(Image(), _ => Task.CompletedTask);
        try { action(editor); }
        finally { editor.Close(); }
    }
    private static BitmapSource Image(int width = 300, int height = 200)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        image.Freeze();
        return image;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }
}
