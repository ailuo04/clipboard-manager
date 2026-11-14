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
public sealed class ImageEditorWindowTests(SettingsOverlayWpfFixture wpf)
{
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void EditorKeepsLabelsReadableAndWorkspaceOpaqueInBothThemes(AppTheme theme)
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(theme);
            var editor = new ImageEditorWindow(Image(), _ => Task.CompletedTask);
            try
            {
                var label = editor.ColorLabel;
                var color = Assert.IsType<SolidColorBrush>(label.Foreground).Color;
                if (theme == AppTheme.Dark) Assert.True(color.R > 180 && color.G > 180 && color.B > 180, $"Dark label is unreadable: {color}");
                else Assert.True(color.R < 100 && color.G < 100 && color.B < 100);
                editor.ColorBox.Text = "#FF8800";
                var content = (FrameworkElement)editor.Content;
                content.Measure(new Size(960, 660));
                content.Arrange(new Rect(0, 0, 960, 660));
                content.UpdateLayout();
                Assert.True(editor.ColorBox.ExtentWidth <= editor.ColorBox.ViewportWidth + 1, "The custom color value is clipped.");
                var rendered = new RenderTargetBitmap(960, 660, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(content);
                var pixel = new byte[4];
                rendered.CopyPixels(new Int32Rect(4, 200, 1, 1), pixel, 4, 0);
                Assert.Equal(255, pixel[3]);
            }
            finally { editor.Close(); }
        });
    }

    [Fact]
    public void MovingTextAndArrowsKeepsTheirOwnColorsAndOriginalPixelCoordinates()
    {
        wpf.Run(() =>
        {
            var canvas = new ImageAnnotationCanvas();
            canvas.SetImage(Image());
            canvas.DisplayScale = 0.25;
            canvas.AddText("标注", new Point(40, 50), Colors.Red, 20);
            canvas.MoveSelection(new Vector(12, 18));
            var text = Assert.IsType<TextImageAnnotation>(canvas.SelectedAnnotation);
            Assert.Equal(new Point(52, 68), text.Position);
            Assert.Equal(Colors.Red, text.Color);
            canvas.Annotations.Add(new ArrowImageAnnotation(new Point(50, 130), new Point(200, 130), Colors.Blue));
            canvas.SelectedIndex = 1;
            canvas.MoveSelection(new Vector(-500, 0));
            var arrow = Assert.IsType<ArrowImageAnnotation>(canvas.SelectedAnnotation);
            Assert.Equal(new Point(0, 130), arrow.Start);
            Assert.Equal(new Point(150, 130), arrow.End);
            Assert.Equal(Colors.Blue, arrow.Color);
            Assert.Equal(text, canvas.Annotations[0]);
        });
    }

    [Fact]
    public void HitTestingUsesTheArrowLineRatherThanItsEntireBoundingBox()
    {
        wpf.Run(() =>
        {
            var canvas = new ImageAnnotationCanvas();
            canvas.SetImage(Image());
            canvas.Annotations.Add(new ArrowImageAnnotation(new Point(20, 20), new Point(180, 180), Colors.Blue));
            Assert.Equal(0, canvas.HitTestAnnotation(new Point(100, 100)));
            Assert.Equal(-1, canvas.HitTestAnnotation(new Point(170, 30)));
        });
    }

    [Fact]
    public void ChangingSelectedTextOrColorDoesNotChangeOtherAnnotations()
    {
        wpf.Run(() =>
        {
            var editor = new ImageEditorWindow(Image(), _ => Task.CompletedTask);
            try
            {
                editor.ImageCanvas.AddText("原文字", new Point(30, 40), Colors.Red, 22);
                editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(20, 140), new Point(200, 140), Colors.Blue));
                editor.AnnotationTextBox.Text = "自定义文字";
                editor.ColorBox.Text = "#00A050";
                var text = Assert.IsType<TextImageAnnotation>(editor.ImageCanvas.Annotations[0]);
                Assert.Equal("自定义文字", text.Text);
                Assert.Equal(Color.FromRgb(0, 160, 80), text.Color);
                Assert.Equal(Colors.Blue, editor.ImageCanvas.Annotations[1].Color);
                editor.ImageCanvas.SelectedIndex = 1;
                Assert.Equal("#0000FF", editor.ColorBox.Text);
                editor.ColorBox.Text = "#FF8800";
                Assert.Equal(Color.FromRgb(255, 136, 0), editor.ImageCanvas.Annotations[1].Color);
                Assert.Equal(text, editor.ImageCanvas.Annotations[0]);
            }
            finally { editor.Close(); }
        });
    }

    [Fact]
    public void InvalidCustomColorBlocksSavingAndCanBeCorrectedWithoutLosingWork()
    {
        wpf.Run(() =>
        {
            var editor = new ImageEditorWindow(Image(), _ => Task.CompletedTask);
            try
            {
                editor.ImageCanvas.AddText("测试", new Point(30, 40), Colors.Red, 22);
                editor.ColorBox.Text = "#GGGGGG";
                Assert.False(editor.SaveButton.IsEnabled);
                Assert.Equal(Colors.Red, Assert.Single(editor.ImageCanvas.Annotations).Color);
                editor.ColorBox.Text = "#123ABC";
                Assert.True(editor.SaveButton.IsEnabled);
                Assert.Equal(Color.FromRgb(18, 58, 188), Assert.Single(editor.ImageCanvas.Annotations).Color);
            }
            finally { editor.Close(); }
        });
    }

    [Fact]
    public void FailedSaveKeepsEditsAndAllowsRetryAndExportsWithoutSelectionBorder()
    {
        wpf.Run(() =>
        {
            int attempts = 0;
            BitmapSource? exported = null;
            var editor = new ImageEditorWindow(Image(), image =>
            {
                attempts++;
                if (attempts == 1) return Task.FromException(new IOException("test save unavailable"));
                exported = image;
                return Task.CompletedTask;
            });
            bool closed = false;
            editor.Closed += (_, _) => closed = true;
            editor.ImageCanvas.Annotations.Add(new ArrowImageAnnotation(new Point(20, 80), new Point(220, 80), Colors.Red));
            editor.ImageCanvas.SelectedIndex = 0;
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(closed);
            Assert.Single(editor.ImageCanvas.Annotations);
            Assert.True(editor.SaveButton.IsEnabled);
            Assert.Contains("test save unavailable", editor.StatusText.Text);
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(closed);
            Assert.Equal(2, attempts);
            Assert.NotNull(exported);
            Assert.Equal(300, exported.PixelWidth);
            var pixels = new byte[300 * 200 * 4];
            exported.CopyPixels(pixels, 300 * 4, 0);
            int shaft = (80 * 300 + 80) * 4;
            Assert.Equal(255, pixels[shaft + 2]);
            Assert.Equal(255, pixels[shaft + 3]);
            // The blue dashed selection outline is editor-only.
            for (int i = 0; i < pixels.Length; i += 4)
                Assert.Equal(0, pixels[i]);
        });
    }

    [Fact]
    public void CancelDoesNotSaveAnnotations()
    {
        wpf.Run(() =>
        {
            bool saved = false;
            var editor = new ImageEditorWindow(Image(), _ => { saved = true; return Task.CompletedTask; });
            editor.ImageCanvas.AddText("不保存", new Point(20, 20), Colors.Red, 20);
            editor.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(saved);
        });
    }

    [Fact]
    public void SavingAtCapacityProtectsTheEditedOriginalFromRetention()
    {
        var original = new ClipboardRecord { CopiedAtUtc = new DateTime(2026, 1, 1), ByteLength = 10 };
        var other = new ClipboardRecord { CopiedAtUtc = new DateTime(2026, 1, 2), ByteLength = 10 };
        var edited = new ClipboardRecord { CopiedAtUtc = new DateTime(2026, 1, 3), ByteLength = 10 };
        var records = new List<ClipboardRecord> { edited, other, original };
        var removed = HistoryService.ApplyRetention(records, new AppSettings { MaxRecords = 2 }, original.Id);
        Assert.Equal(other.Id, Assert.Single(removed).Id);
        Assert.Contains(original, records);
        Assert.Contains(edited, records);
    }

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(300, 200, 96, 96, PixelFormats.Bgra32, null, new byte[300 * 200 * 4], 300 * 4);
        image.Freeze();
        return image;
    }
}
