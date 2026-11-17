using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.ViewModels;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class SettingsAndMarqueeTests(SettingsOverlayWpfFixture wpf)
{
    [Theory]
    [InlineData(AppTheme.Light, 1)]
    [InlineData(AppTheme.Dark, 1)]
    [InlineData(AppTheme.Light, 1.5)]
    [InlineData(AppTheme.Dark, 1.5)]
    public void SettingsChoicesAndInputsHaveDistinctSurfacesInBothThemes(AppTheme theme, double scale) => wpf.Run(() =>
    {
        ThemeManager.Apply(theme);
        var view = new SettingsView { LayoutTransform = new ScaleTransform(scale, scale) };
        var window = Host(view, 300 * scale, 420 * scale);
        try
        {
            view.ThemeLightRadio.IsChecked = true;
            window.UpdateLayout();
            var active = Assert.IsType<Border>(view.ThemeLightRadio.Template.FindName("Chrome", view.ThemeLightRadio));
            var idle = Assert.IsType<Border>(view.ThemeDarkRadio.Template.FindName("Chrome", view.ThemeDarkRadio));
            Color selected = Assert.IsType<SolidColorBrush>(active.Background).Color;
            Color normal = Assert.IsType<SolidColorBrush>(idle.Background).Color;
            Assert.True(Difference(selected, normal) >= 20, "The selected theme blends into the unselected options.");
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(active.BorderBrush).Color.A);
            Assert.Contains(Descendants<TextBlock>(view.ThemeDarkRadio), text => text.Text == "深色");
            Assert.Contains(Descendants<TextBlock>(view.ThemeLightRadio), text => text.Text == "浅色");
            Assert.Contains(Descendants<TextBlock>(view.ThemeSystemRadio), text => text.Text == "跟随系统");
            var card = Descendants<Border>(view).First(border => border.Style == view.Resources["SettingsCard"]);
            Color fill = Assert.IsType<SolidColorBrush>(card.Background).Color;
            Color input = Assert.IsType<SolidColorBrush>(view.MaxRecordsBox.Background).Color;
            Assert.Equal(255, fill.A);
            Assert.True(Difference(fill, input) >= 12, "Input fields blend into the settings cards.");
            Assert.True(Difference(fill, Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(view.FindName("SettingsBackground")).Background).Color) >= 10);
            var bounds = view.ThemeSystemRadio.TransformToAncestor(card).TransformBounds(new Rect(view.ThemeSystemRadio.RenderSize));
            Assert.True(bounds.Right <= card.ActualWidth, "Theme labels exceed the settings card.");
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(AppTheme.Light, 1)]
    [InlineData(AppTheme.Dark, 1)]
    [InlineData(AppTheme.Light, 1.2)]
    [InlineData(AppTheme.Dark, 1.2)]
    public void ThemeOptionPaintsItsCompleteBottomBorder(AppTheme theme, double scale) => wpf.Run(() =>
    {
        ThemeManager.Apply(theme);
        var view = new SettingsView { LayoutTransform = new ScaleTransform(scale, scale) };
        view.ThemeLightRadio.IsChecked = true;
        var root = new Grid();
        root.Children.Add(view);
        var window = Host(root, 300 * scale, 420 * scale);
        try
        {
            var chrome = Assert.IsType<Border>(view.ThemeLightRadio.Template.FindName("Chrome", view.ThemeLightRadio));
            Point bottom = chrome.TranslatePoint(new Point(chrome.ActualWidth / 2, chrome.ActualHeight - 0.5), root);
            Color expected = Assert.IsType<SolidColorBrush>(chrome.BorderBrush).Color;
            Color actual = Sample(root, bottom);
            Assert.True(Difference(expected, actual) < 45, $"The bottom border was clipped: expected {expected}, got {actual}, bounds {chrome.RenderSize}, point {bottom}.");
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void TransparentLogActionDoesNotPaintAPartialShadow(AppTheme theme) => wpf.Run(() =>
    {
        ThemeManager.Apply(theme);
        var view = new SettingsView();
        var window = Host(view, 300, 420);
        try
        {
            var scroll = Descendants<ScrollViewer>(view).First();
            scroll.ScrollToBottom();
            Pump(30);
            var button = view.OpenLogButton;
            Point bottom = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight - 1), view);
            Color expected = Assert.IsType<SolidColorBrush>(view.FindResource("SettingsCardBrush")).Color;
            Color actual = Sample(view, bottom);
            Assert.True(Difference(expected, actual) < 4, $"A shadow is visible on the transparent log action: expected {expected}, got {actual}.");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SettingsScrollbarIsNarrowAndStillScrollsThePage() => wpf.Run(() =>
    {
        ThemeManager.Apply(AppTheme.Light);
        var view = new SettingsView();
        var window = Host(view, 300, 420);
        try
        {
            var scroll = Descendants<ScrollViewer>(view).First();
            Assert.True(scroll.ScrollableHeight > 100);
            var bar = Assert.IsType<ScrollBar>(scroll.Template.FindName("PART_VerticalScrollBar", scroll));
            Assert.InRange(bar.ActualWidth, 4, 8);
            Assert.Equal(Visibility.Visible, bar.Visibility);
            scroll.ScrollToVerticalOffset(150);
            Pump(30);
            Assert.InRange(scroll.VerticalOffset, 149, 151);
            Assert.InRange(bar.Value, 149, 151);
            var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
            track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 15));
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 15, false));
            Pump(30);
            Assert.True(scroll.VerticalOffset > 150, "Dragging the narrow thumb must still scroll the page.");
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task CapsuleKeepsTextBeyondTheListPreviewLimitAndNormalizesWhitespace()
    {
        MainViewModel? model = null;
        wpf.Run(() => model = new MainViewModel(new Window(), new HistoryService(new HistoryStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))))));
        try
        {
            wpf.Run(() =>
            {
                string prefix = new('长', 220);
                var record = new RecordViewModel(new ClipboardRecord { Text = prefix + "\r\n\t末尾  内容", Kind = ClipboardRecordKind.Text }, model!);
                typeof(MainViewModel).GetField("_allRecords", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, new List<RecordViewModel> { record });
                typeof(MainViewModel).GetField("_isReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, true);
                Assert.Equal(prefix + " 末尾 内容", model!.CapsuleText);
                Assert.Equal(200, record.SingleLine.Length);
            });
        }
        finally
        {
            Task shutdown = Task.CompletedTask;
            wpf.Run(() => shutdown = model!.ShutdownAsync());
            await shutdown;
        }
    }

    [Fact]
    public void CapsuleScrollsOverflowAndResetsForShortTextVisibilityAndUnload() => wpf.Run(() =>
    {
        ThemeManager.Apply(AppTheme.Light);
        var view = new MainView();
        view.DataContext = CapsuleData(new string('长', 40));
        var window = Host(view, 240, 40);
        try
        {
            var marquee = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("CapsuleMarquee"));
            var text = Assert.Single(Descendants<TextBlock>(marquee));
            var offset = Assert.IsType<TranslateTransform>(text.RenderTransform);
            Assert.True(text.ActualWidth > marquee.ActualWidth);
            Assert.True(marquee.ClipToBounds);
            Assert.Equal(0, offset.X);
            Pump(1600);
            if (SystemParameters.ClientAreaAnimation) Assert.True(offset.X < -1, "Overflow text did not start scrolling after its opening pause.");
            else Assert.False(offset.HasAnimatedProperties);
            view.DataContext = CapsuleData("短文本");
            Pump(30);
            Assert.Equal("短文本", text.Text);
            Assert.False(offset.HasAnimatedProperties);
            Assert.Equal(0, offset.X);
            view.DataContext = CapsuleData(new string('长', 40));
            Pump(30);
            Assert.Equal(SystemParameters.ClientAreaAnimation, offset.HasAnimatedProperties);
            marquee.Visibility = Visibility.Collapsed;
            Assert.False(offset.HasAnimatedProperties);
            marquee.Visibility = Visibility.Visible;
            Pump(30);
            Assert.Equal(0, offset.X);
            window.Content = null;
            Pump(30);
            Assert.False(offset.HasAnimatedProperties);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MarqueePausesAtTheEndLoopsAndStopsWhenTheTextFits() => wpf.Run(() =>
    {
        var marquee = new ClipboardManager.Controls.MarqueeText { Text = "滚动到末尾再从头显示", FontSize = 12.5 };
        var window = Host(marquee, 90, 32);
        try
        {
            var text = Assert.Single(Descendants<TextBlock>(marquee));
            var offset = Assert.IsType<TranslateTransform>(text.RenderTransform);
            double overflow = text.ActualWidth - marquee.ActualWidth;
            Assert.True(overflow > 0);
            if (SystemParameters.ClientAreaAnimation)
            {
                Pump((int)(1200 + overflow / 28 * 1000 + 200));
                Assert.InRange(offset.X, -overflow - 0.5, -overflow + 0.5);
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                do { Pump(20); }
                while (offset.X < -1 && timeout.ElapsedMilliseconds < 3000);
                Assert.InRange(offset.X, -1, 0);
            }
            window.Width = 400;
            window.UpdateLayout();
            Pump(30);
            Assert.False(offset.HasAnimatedProperties);
            Assert.Equal(0, offset.X);
            Assert.Equal("滚动到末尾再从头显示", text.Text);
        }
        finally { window.Close(); }
    });

    private static object CapsuleData(string text) => new { CapsuleText = text, CapsuleImagePath = (string?)null, RecordCount = 2, HasRecords = true };

    private static Window Host(FrameworkElement content, double width, double height)
    {
        var window = new Window { Content = content, Width = width, Height = height, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Opacity = 0, Left = -10000, Top = -10000 };
        window.Show();
        window.UpdateLayout();
        Pump(30);
        return window;
    }

    private static Color Sample(FrameworkElement element, Point point)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(element);
        byte[] pixel = new byte[4];
        image.CopyPixels(new Int32Rect((int)Math.Floor(point.X * dpi.DpiScaleX), (int)Math.Floor(point.Y * dpi.DpiScaleY), 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static int Difference(Color a, Color b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
