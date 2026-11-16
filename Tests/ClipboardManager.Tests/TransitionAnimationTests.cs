using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class TransitionAnimationTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public void CompletedOverlayEnterReleasesAnimationClocks() => wpf.Run(() => WithFrame(frame =>
    {
        frame.PlayOverlayEnter();
        Assert.Equal(SystemParameters.ClientAreaAnimation, frame.OverlayPanel.HasAnimatedProperties);
        Pump(280);
        Assert.False(frame.OverlayPanel.HasAnimatedProperties);
        Assert.Equal(1, frame.OverlayPanel.Opacity);
        Assert.False(frame.OverlayPanel.RenderTransform.HasAnimatedProperties);
        Assert.Null(frame.OverlayPanel.CacheMode);
    }));

    [Fact]
    public void HiddenOverlayStopsItsTransitionImmediately() => wpf.Run(() => WithFrame(frame =>
    {
        frame.PlayOverlayEnter();
        frame.OverlayLayer.Visibility = Visibility.Collapsed;
        Assert.False(frame.OverlayPanel.HasAnimatedProperties);
        Assert.Equal(1, frame.OverlayPanel.Opacity);
        Assert.Null(frame.OverlayPanel.CacheMode);
    }));

    [Fact]
    public void ReplayingOverlayEnterPreservesItsExistingTransformAndCache() => wpf.Run(() => WithFrame(frame =>
    {
        var transform = new ScaleTransform(0.95, 0.95);
        var cache = new BitmapCache(1.5);
        frame.OverlayPanel.RenderTransform = transform;
        frame.OverlayPanel.CacheMode = cache;
        frame.PlayOverlayEnter();
        frame.PlayOverlayEnter();
        Pump(280);
        Assert.Same(transform, frame.OverlayPanel.RenderTransform);
        Assert.Same(cache, frame.OverlayPanel.CacheMode);
        Assert.False(frame.OverlayPanel.HasAnimatedProperties);
    }));

    [Fact]
    public void ClosingPreviewUnloadsItsSurfaceWithoutWaitingForAFadeOut() => wpf.Run(() =>
    {
        var view = new MainView();
        var owner = new Window { Content = view, Width = 240, Height = 40, ShowInTaskbar = false };
        try
        {
            owner.Show();
            var popup = view.HoverPreviewPopup;
            view.HoverPreviewSurface.Content = new { HoverText = "动画结束与关闭测试", ContentInfo = "测试" };
            popup.PlacementTarget = view;
            popup.IsOpen = true;
            Pump(220);
            Assert.NotNull(PresentationSource.FromVisual(view.HoverPreviewSurface));
            view.CloseHoverPreview();
            Pump(25); // Popup HWND disposal is queued at Input priority, without the old 150 ms fade.
            Assert.Null(PresentationSource.FromVisual(view.HoverPreviewSurface));
            Assert.False(view.HoverPreviewSurface.HasAnimatedProperties);
            Assert.Null(view.HoverPreviewSurface.CacheMode);
        }
        finally { view.CloseHoverPreview(); owner.Close(); }
    });

    [Fact]
    public void MeasuringCappedHistoryDoesNotScanOffscreenRecords() => wpf.Run(() =>
    {
        var records = new CountingRecords(500);
        var view = new MainView { DataContext = new { HistoryRecords = records } };
        var owner = new Window { Content = view, Width = 240, Height = 40, ShowInTaskbar = false };
        try
        {
            owner.Show();
            _ = view.MeasureHistoryPopup(300);
            records.Reads = 0;
            Size size = view.MeasureHistoryPopup(300);
            Assert.InRange(size.Height, 300, 360);
            Assert.InRange(records.Reads, 0, 12);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void PreviewTextDoesNotPassThroughTheShadowShader() => wpf.Run(() =>
    {
        var view = new MainView();
        var owner = new Window { Content = view, Width = 240, Height = 40, ShowInTaskbar = false };
        try
        {
            owner.Show();
            view.HoverPreviewSurface.Content = new { HoverText = "可选择、可滚动的预览文字", ContentInfo = "测试" };
            view.HoverPreviewPopup.PlacementTarget = view;
            view.HoverPreviewPopup.IsOpen = true;
            Pump(30);
            TextBox text = Descendants<TextBox>(view.HoverPreviewSurface).Single();
            text.Select(0, 3);
            Assert.Equal("可选择", text.SelectedText);
            for (DependencyObject? node = text; node is UIElement element; node = VisualTreeHelper.GetParent(node))
                Assert.Null(element.Effect);
            Assert.Contains(Descendants<Border>(view.HoverPreviewSurface), border => border.Effect is not null);
        }
        finally { view.CloseHoverPreview(); owner.Close(); }
    });

    [Fact]
    public void RapidPopupReopenReleasesThePreviousTransitionAndRestoresOpacity() => wpf.Run(() =>
    {
        var surface = new Border { Width = 120, Height = 50, Background = Brushes.White, Opacity = 0.8 };
        var popup = new Popup { Child = surface, AllowsTransparency = true, PopupAnimation = PopupAnimation.None };
        ClipboardManager.Utilities.TransitionAnimations.SetEnterOnOpen(popup, true);
        try
        {
            popup.IsOpen = true;
            Pump(30);
            Assert.Equal(SystemParameters.ClientAreaAnimation, surface.HasAnimatedProperties);
            popup.IsOpen = false;
            popup.IsOpen = true;
            Pump(230);
            Assert.False(surface.HasAnimatedProperties);
            Assert.Equal(0.8, surface.Opacity);
            Assert.Null(surface.CacheMode);
        }
        finally { popup.IsOpen = false; Pump(25); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedPopupStylesReleaseTheirEntranceClocks(bool isToolTip) => wpf.Run(() =>
    {
        var owner = new Window { Content = new Border(), Width = 240, Height = 80, ShowInTaskbar = false };
        FrameworkElement surface;
        Action<bool> setOpen;
        if (isToolTip)
        {
            var tip = new ToolTip { Content = "提示动画", PlacementTarget = owner };
            surface = tip;
            setOpen = value => tip.IsOpen = value;
        }
        else
        {
            var menu = new ContextMenu { PlacementTarget = owner };
            menu.Items.Add(new MenuItem { Header = "菜单动画" });
            surface = menu;
            setOpen = value => menu.IsOpen = value;
        }
        try
        {
            owner.Show();
            setOpen(true);
            Pump(25);
            Assert.Equal(SystemParameters.ClientAreaAnimation, surface.HasAnimatedProperties);
            Pump(230);
            Assert.False(surface.HasAnimatedProperties);
            Assert.Null(surface.CacheMode);
            setOpen(false);
            Pump(25);
            Assert.Null(PresentationSource.FromVisual(surface));
        }
        finally { setOpen(false); owner.Close(); }
    });

    [Fact]
    public void ImagePreviewReservesItsFinalSizeBeforeTheBitmapLoads() => wpf.Run(() =>
    {
        var view = new MainView();
        var template = (DataTemplate)view.FindResource("HoverPreviewImage");
        var content = (FrameworkElement)template.LoadContent();
        content.DataContext = new { PreviewImageWidth = 400d, PreviewImageHeight = 250d, ImagePath = (string?)null, ContentInfo = "1600 × 1000" };
        content.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var thumbnail = Descendants<ClipboardManager.Controls.AsyncThumbnail>(content).Single();
        Assert.Null(thumbnail.Source);
        Assert.True(thumbnail.DesiredSize == new Size(400, 250), $"Expected reserved 400 × 250, got {thumbnail.DesiredSize}; width={thumbnail.Width}, height={thumbnail.Height}, context={thumbnail.DataContext is not null}.");
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void WithFrame(Action<WindowFrame> action)
    {
        var frame = new WindowFrame();
        frame.OverlayContent.Content = new TextBlock { Text = "动画测试" };
        frame.OverlayLayer.Visibility = Visibility.Visible;
        var owner = new Window { Content = frame, Width = 300, Height = 400, ShowInTaskbar = false };
        try { owner.Show(); owner.UpdateLayout(); action(frame); }
        finally { owner.Close(); }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class CountingRecords(int count) : IList
    {
        private readonly object _record = new { SingleLine = "测试记录", PositionLabel = "1", ShortTimeLabel = "12:00" };
        public int Reads { get; set; }
        public int Count => count;
        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public object? this[int index] { get { Reads++; return _record; } set => throw new NotSupportedException(); }
        public IEnumerator GetEnumerator() { for (int i = 0; i < count; i++) yield return this[i]; }
        public int IndexOf(object? value) => ReferenceEquals(value, _record) ? 0 : -1;
        public bool Contains(object? value) => ReferenceEquals(value, _record);
        public void CopyTo(Array array, int index) { for (int i = 0; i < count; i++) array.SetValue(this[i], index + i); }
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
