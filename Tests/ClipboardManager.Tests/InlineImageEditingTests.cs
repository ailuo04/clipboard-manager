using System.IO;
using System.Windows;
using System.Windows.Controls;
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
public sealed class InlineImageEditingTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public void ClickingTheImageStartsAnEmptyNativeTextEditorOnTheCanvas()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            Assert.Equal(Visibility.Collapsed, Frame(editor).Visibility);
            editor.PlaceText(new Point(60, 70));
            Assert.Equal(Visibility.Visible, Frame(editor).Visibility);
            Assert.True(editor.AnnotationTextBox.AcceptsReturn);
            Assert.True(InputMethod.GetIsInputMethodEnabled(editor.AnnotationTextBox));
            Assert.Empty(editor.AnnotationTextBox.Text);
            Assert.Empty(editor.ImageCanvas.Annotations);
            Assert.False(editor.SaveButton.IsEnabled);
            editor.UpdateTextPointer(null);
            Assert.Equal(Visibility.Visible, Frame(editor).Visibility);
            Assert.NotNull(editor.AnnotationTextBox.Parent);
        }));
    }

    [Fact]
    public void ChineseMultilineTypingKeepsTheEditorOpenAndUpdatesOneAnnotation()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.PlaceText(new Point(60, 70));
            editor.AnnotationTextBox.Text = "你";
            editor.AnnotationTextBox.Text = "你好\r\n第二行";
            var text = Assert.IsType<TextImageAnnotation>(Assert.Single(editor.ImageCanvas.Annotations));
            Assert.Equal("你好\r\n第二行", text.Text);
            Assert.Equal(new Point(60, 70), text.Position);
            Assert.True(ImageAnnotationRenderer.GetBounds(text).Height > text.FontSize * 1.7);
            Assert.Equal(Visibility.Visible, Frame(editor).Visibility);
            Assert.True(editor.SaveButton.IsEnabled);
            Assert.Equal(0, editor.ImageCanvas.SelectedIndex);
        }));
    }

    [Fact]
    public void EditingExistingTextIsLocalAndDoesNotChangeItsPositionOrOtherShapes()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.ImageCanvas.AddText("原文字", new Point(40, 50), Colors.Red, 24, "SimSun");
            var arrow = new ArrowImageAnnotation(new Point(20, 180), new Point(220, 180), Colors.Blue);
            editor.ImageCanvas.Annotations.Add(arrow);
            Assert.True(EditAt(editor, new Point(45, 55)));
            Assert.Equal("原文字", editor.AnnotationTextBox.Text);
            Assert.Equal("SimSun", editor.FontFamilyBox.SelectedValue);
            editor.AnnotationTextBox.Text = "修改后的文字";
            Finish(editor);
            var text = Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.Annotations[0]);
            Assert.Equal("修改后的文字", text.Text);
            Assert.Equal(new Point(40, 50), text.Position);
            Assert.Equal(24, text.FontSize);
            Assert.Equal("SimSun", text.FontFamily);
            Assert.Equal(arrow, editor.ImageCanvas.Annotations[1]);
            Assert.Equal(Visibility.Collapsed, Frame(editor).Visibility);
        }));
    }

    [Fact]
    public void InlineFontSizeAndColorFollowTheFontToolbar()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.PlaceText(new Point(40, 50));
            editor.AnnotationTextBox.Text = "Sample 文字";
            editor.FontFamilyBox.SelectedValue = "Times New Roman";
            editor.FontSizeBox.Text = "32";
            editor.ColorBox.Text = "#2468AC";
            var text = Assert.IsType<TextImageAnnotation>(Assert.Single(editor.ImageCanvas.Annotations));
            Assert.Equal("Times New Roman", text.FontFamily);
            Assert.Equal(32, text.FontSize);
            Assert.Equal(Color.FromRgb(36, 104, 172), text.Color);
            Assert.Equal("Times New Roman", editor.AnnotationTextBox.FontFamily.Source);
            Assert.Equal(32, editor.AnnotationTextBox.FontSize);
            Assert.Equal(text.Color, Assert.IsType<SolidColorBrush>(editor.AnnotationTextBox.Foreground).Color);
            Assert.Equal(Visibility.Visible, Frame(editor).Visibility);
        }));
    }

    [Fact]
    public void ContinuingTypingAfterResizingKeepsTheClampedTextPosition()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.PlaceText(new Point(450, 290));
            editor.AnnotationTextBox.Text = "WW";
            editor.FontSizeBox.Text = "100";
            Point resized = Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.SelectedAnnotation).Position;
            editor.FontSizeBox.Text = "20";
            editor.AnnotationTextBox.Text = "WWW";
            Assert.Equal(resized, Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.SelectedAnnotation).Position);
        }));
    }

    [Fact]
    public void EscapeFinishesTypingWithoutClosingTheWindow()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            bool closed = false;
            editor.Closed += (_, _) => closed = true;
            editor.PlaceText(new Point(40, 50));
            editor.AnnotationTextBox.Text = "保留文字";
            PressKey(editor, Key.Escape);
            Assert.False(closed);
            Assert.Equal(Visibility.Collapsed, Frame(editor).Visibility);
            Assert.Equal("保留文字", Assert.IsType<TextImageAnnotation>(Assert.Single(editor.ImageCanvas.Annotations)).Text);
        }));
    }

    [Fact]
    public void EnterAndImeEscapeRemainAvailableToTheNativeTextInput()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.PlaceText(new Point(40, 50));
            Assert.False(PressKey(editor, Key.Enter));
            var composition = new TextComposition(InputManager.Current, editor.AnnotationTextBox, "ni");
            editor.AnnotationTextBox.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            Assert.False(PressKey(editor, Key.Escape));
            Assert.Equal(Visibility.Visible, Frame(editor).Visibility);
            editor.AnnotationTextBox.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
        }));
    }

    [Fact]
    public void EmptyDraftsAndClearedExistingTextAreRemovedOnFinish()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.PlaceText(new Point(40, 50));
            Finish(editor);
            Assert.Empty(editor.ImageCanvas.Annotations);
            editor.TextToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.PlaceText(new Point(40, 50));
            editor.AnnotationTextBox.Text = "删除这段";
            editor.AnnotationTextBox.Clear();
            Finish(editor);
            Assert.Empty(editor.ImageCanvas.Annotations);
            Assert.False(editor.SaveButton.IsEnabled);
        }));
    }

    [Fact]
    public void NewTextStartsEmptyAndToolSwitchCommitsExistingText()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.PlaceText(new Point(20, 20));
            editor.AnnotationTextBox.Text = "第一段";
            editor.ArrowToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, Frame(editor).Visibility);
            Assert.Equal("第一段", Assert.IsType<TextImageAnnotation>(Assert.Single(editor.ImageCanvas.Annotations)).Text);
            Assert.Equal(Visibility.Visible, Element(editor, "ShapePropertiesGroup").Visibility);
            Assert.Equal(Visibility.Collapsed, Element(editor, "FontPropertiesGroup").Visibility);
            editor.TextToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.PlaceText(new Point(200, 100));
            Assert.Empty(editor.AnnotationTextBox.Text);
            editor.AnnotationTextBox.Text = "第二段";
            Finish(editor);
            Assert.Equal(2, editor.ImageCanvas.Annotations.Count);
            Assert.Equal("第一段", Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.Annotations[0]).Text);
            Assert.Equal("第二段", Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.Annotations[1]).Text);
        }));
    }

    [Fact]
    public void SelectingTextOrShapesShowsOnlyTheRelevantProperties()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.ImageCanvas.AddText("文字", new Point(30, 40), Colors.Red, 22);
            editor.ImageCanvas.Annotations.Add(new RectangleImageAnnotation(new Rect(20, 130, 160, 70), Colors.Blue));
            Assert.Equal(Visibility.Visible, Element(editor, "FontPropertiesGroup").Visibility);
            Assert.Equal(Visibility.Collapsed, Element(editor, "ShapePropertiesGroup").Visibility);
            editor.ImageCanvas.SelectedIndex = 1;
            Assert.Equal(Visibility.Collapsed, Element(editor, "FontPropertiesGroup").Visibility);
            Assert.Equal(Visibility.Visible, Element(editor, "ShapePropertiesGroup").Visibility);
            Assert.Equal("线条颜色", Assert.IsType<TextBlock>(editor.FindName("ColorLabel")).Text);
            editor.ImageCanvas.SelectedIndex = 0;
            Assert.Equal(Visibility.Visible, Element(editor, "FontPropertiesGroup").Visibility);
            Assert.Equal("文字颜色", Assert.IsType<TextBlock>(editor.FindName("ColorLabel")).Text);
        }));
    }

    [Fact]
    public void HiddenShapePropertiesDoNotBlockSavingAfterTheShapeIsDeleted()
    {
        wpf.Run(() => WithEditor(editor =>
        {
            editor.ImageCanvas.AddText("保留文字", new Point(30, 40), Colors.Red, 22);
            editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(20, 180), new Point(240, 180), Colors.Blue));
            editor.ImageCanvas.SelectedIndex = 1;
            editor.SelectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.ArrowThicknessBox.Text = "-1";
            Assert.False(editor.SaveButton.IsEnabled);
            editor.DeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, editor.ShapePropertiesGroup.Visibility);
            Assert.False(editor.ArrowThicknessPanel.IsEnabled);
            Assert.True(editor.SaveButton.IsEnabled);
            Assert.Single(editor.ImageCanvas.Annotations);
        }));
    }

    [Fact]
    public void SavingWhileTypingExportsTextWithoutTheEditingFrameOrPlaceholder()
    {
        wpf.Run(() =>
        {
            BitmapSource? saved = null;
            var editor = new ImageEditorWindow(Image(), image => { saved = image; return Task.CompletedTask; });
            try
            {
                editor.PlaceText(new Point(40, 50));
                editor.AnnotationTextBox.Text = "保存文字\r\n第二行";
                editor.ColorBox.Text = "#FF0000";
                Assert.True(editor.SaveButton.IsEnabled);
                editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotNull(saved);
                Assert.Equal(480, saved.PixelWidth);
                Assert.Equal(320, saved.PixelHeight);
                byte[] pixels = new byte[480 * 320 * 4];
                saved.CopyPixels(pixels, 480 * 4, 0);
                Assert.Contains(pixels, value => value != 0);
                for (int index = 0; index < pixels.Length; index += 4)
                {
                    Assert.Equal(0, pixels[index]);
                    Assert.Equal(0, pixels[index + 1]);
                }
            }
            finally { editor.Close(); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Light, 944, 660)]
    [InlineData(AppTheme.Dark, 944, 660)]
    [InlineData(AppTheme.Light, 604, 400)]
    [InlineData(AppTheme.Dark, 604, 400)]
    public void GroupedToolbarLeavesRoomForTheCanvasAndInlineEditor(AppTheme theme, int width, int height)
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(theme);
            var editor = new ImageEditorWindow(Image(1280, 900), _ => Task.CompletedTask);
            try
            {
                editor.FontSizeBox.Text = "48";
                editor.PlaceText(new Point(180, 160));
                editor.AnnotationTextBox.Text = "图片上的文字\r\n直接输入和修改";
                Layout(editor, width, height);
                var content = (FrameworkElement)editor.Content;
                Assert.True(editor.Toolbar.ActualHeight <= 122, $"Toolbar uses {editor.Toolbar.ActualHeight}px.");
                Assert.True(editor.Viewport.ActualHeight >= height * 0.40);
                foreach (FrameworkElement control in new FrameworkElement[] { editor.TextToolButton, editor.FontFamilyBox, editor.FontSizeBox, editor.ColorBox, editor.SaveButton })
                {
                    Point point = control.TranslatePoint(new Point(), content);
                    Assert.True(point.X >= 0 && point.Y >= 0 && point.X + control.ActualWidth <= width + 1 && point.Y + control.ActualHeight <= height + 1,
                        $"Control {control.Name} is clipped at {width} x {height}.");
                }
                var text = Assert.IsType<TextImageAnnotation>(Assert.Single(editor.ImageCanvas.Annotations));
                Point origin = editor.AnnotationTextBox.TranslatePoint(new Point(), editor.ImageCanvas);
                Assert.InRange(Math.Abs(origin.X - text.Position.X), 0, 1 + 1e-9);
                Assert.InRange(Math.Abs(origin.Y - text.Position.Y), 0, 1 + 1e-9);
                Assert.Equal(Visibility.Visible, Frame(editor).Visibility);
                string? output = Environment.GetEnvironmentVariable("CLIPBOARD_ANNOTATION_QA_DIR");
                if (!string.IsNullOrEmpty(output)) SaveVisual(content, Path.Combine(output, $"ribbon-inline-{theme}-{width}.png"));
                editor.ArrowToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(editor, width, height);
                if (!string.IsNullOrEmpty(output)) SaveVisual(content, Path.Combine(output, $"ribbon-shape-{theme}-{width}.png"));
            }
            finally { editor.Close(); }
        });
    }

    private static FrameworkElement Frame(ImageEditorWindow editor) => Element(editor, "TextEditFrame");
    private static FrameworkElement Element(ImageEditorWindow editor, string name) => Assert.IsAssignableFrom<FrameworkElement>(editor.FindName(name));
    private static bool EditAt(ImageEditorWindow editor, Point point) => editor.TryEditTextAt(point);
    private static void Finish(ImageEditorWindow editor) => editor.FinishTextEditing();
    private static bool PressKey(ImageEditorWindow editor, Key key)
    {
        using var source = new HwndSource(new HwndSourceParameters("Inline editor key test") { Width = 1, Height = 1 });
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        editor.RaiseEvent(args);
        return args.Handled;
    }
    private static void Layout(ImageEditorWindow editor, int width, int height)
    {
        var content = (FrameworkElement)editor.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
    }
    private static void SaveVisual(FrameworkElement visual, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rendered = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static void WithEditor(Action<ImageEditorWindow> action)
    {
        var editor = new ImageEditorWindow(Image(), _ => Task.CompletedTask);
        try { action(editor); }
        finally { editor.Close(); }
    }
    private static BitmapSource Image(int width = 480, int height = 320)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        image.Freeze();
        return image;
    }
}
