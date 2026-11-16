using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClipboardManager.Services;

namespace ClipboardManager.Utilities;

/// <summary>Compositor-friendly entrance transitions shared by cards and popup surfaces.</summary>
public static class TransitionAnimations
{
    public static readonly DependencyProperty EnterOnOpenProperty = DependencyProperty.RegisterAttached(
        "EnterOnOpen", typeof(bool), typeof(TransitionAnimations), new PropertyMetadata(false, OnEnterOnOpenChanged));
    private static readonly DependencyProperty ActiveTransitionProperty = DependencyProperty.RegisterAttached(
        "ActiveTransition", typeof(Transition), typeof(TransitionAnimations));

    public static bool GetEnterOnOpen(DependencyObject target) => (bool)target.GetValue(EnterOnOpenProperty);
    public static void SetEnterOnOpen(DependencyObject target, bool value) => target.SetValue(EnterOnOpenProperty, value);

    private static void OnEnterOnOpenChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        bool enabled = (bool)args.NewValue;
        switch (target)
        {
            case Popup popup:
                if (enabled) { popup.Opened += OnPopupOpened; popup.Closed += OnPopupClosed; }
                else { popup.Opened -= OnPopupOpened; popup.Closed -= OnPopupClosed; Stop(popup.Child as FrameworkElement); }
                break;
            case ContextMenu menu:
                if (enabled) { menu.Opened += OnSurfaceOpened; menu.Closed += OnSurfaceClosed; }
                else { menu.Opened -= OnSurfaceOpened; menu.Closed -= OnSurfaceClosed; Stop(menu); }
                break;
            case ToolTip tip:
                if (enabled) { tip.Opened += OnSurfaceOpened; tip.Closed += OnSurfaceClosed; }
                else { tip.Opened -= OnSurfaceOpened; tip.Closed -= OnSurfaceClosed; Stop(tip); }
                break;
        }
    }

    private static void OnPopupOpened(object? sender, EventArgs args)
    {
        if (sender is Popup { Child: FrameworkElement surface }) PlayEnter(surface);
    }

    private static void OnPopupClosed(object? sender, EventArgs args)
    {
        if (sender is Popup popup) Stop(popup.Child as FrameworkElement);
    }

    private static void OnSurfaceOpened(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(sender, args.OriginalSource) && sender is FrameworkElement surface) PlayEnter(surface);
    }

    private static void OnSurfaceClosed(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(sender, args.OriginalSource)) Stop(sender as FrameworkElement);
    }

    internal static void PlayEnter(FrameworkElement target, double verticalOffset = 0, int milliseconds = 150)
    {
        Stop(target);
        // Follow Windows' reduced-motion setting without leaving a hidden or transformed surface.
        if (!SystemParameters.ClientAreaAnimation) return;
        var transition = new Transition(target, verticalOffset, milliseconds);
        target.SetValue(ActiveTransitionProperty, transition);
        transition.Start();
    }

    internal static void Stop(FrameworkElement? target)
    {
        if (target?.GetValue(ActiveTransitionProperty) is not Transition transition) return;
        target.ClearValue(ActiveTransitionProperty);
        transition.Stop();
    }

    private static int? GetRefreshRate(FrameworkElement target)
    {
        IntPtr hwnd = (PresentationSource.FromVisual(target) as HwndSource)?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero && Window.GetWindow(target) is Window owner)
            hwnd = new WindowInteropHelper(owner).Handle;
        if (hwnd == IntPtr.Zero) return null;
        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfoEx(monitor, ref info)) return null;
        var mode = new NativeMethods.DisplayMode { Size = (ushort)Marshal.SizeOf<NativeMethods.DisplayMode>() };
        if (!NativeMethods.EnumDisplaySettings(info.DeviceName, -1, ref mode)) return null;
        // An unavailable rate (0/1) falls back to WPF's native display-paced scheduling.
        return mode.DisplayFrequency is >= 24 and <= 1000 ? (int)mode.DisplayFrequency : null;
    }

    private sealed class Transition
    {
        private readonly FrameworkElement _target;
        private readonly CacheMode? _originalCache;
        private readonly Transform _originalTransform;
        private readonly BitmapCache? _temporaryCache;
        private readonly TransformGroup? _temporaryTransform;
        private readonly TranslateTransform? _translation;
        private readonly DoubleAnimation _fade;
        private readonly DoubleAnimation? _rise;

        internal Transition(FrameworkElement target, double offset, int milliseconds)
        {
            _target = target;
            _originalCache = target.CacheMode;
            _originalTransform = target.RenderTransform;
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            _fade = new DoubleAnimation(0, (double)target.GetAnimationBaseValue(UIElement.OpacityProperty), duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            int? refreshRate = GetRefreshRate(target);
            Timeline.SetDesiredFrameRate(_fade, refreshRate);
            _fade.Completed += OnCompleted;
            if (_originalCache is null && RenderCapability.Tier >> 16 > 0)
                _temporaryCache = new BitmapCache { SnapsToDevicePixels = offset == 0 };
            if (offset != 0)
            {
                _translation = new TranslateTransform();
                _temporaryTransform = new TransformGroup();
                _temporaryTransform.Children.Add(_originalTransform);
                _temporaryTransform.Children.Add(_translation);
                _rise = new DoubleAnimation(offset, 0, duration)
                {
                    EasingFunction = _fade.EasingFunction,
                    FillBehavior = FillBehavior.Stop
                };
                Timeline.SetDesiredFrameRate(_rise, refreshRate);
            }
        }

        internal void Start()
        {
            _target.Unloaded += OnUnloaded;
            _target.IsVisibleChanged += OnVisibilityChanged;
            if (_temporaryCache is not null) _target.SetCurrentValue(UIElement.CacheModeProperty, _temporaryCache);
            if (_temporaryTransform is not null) _target.SetCurrentValue(UIElement.RenderTransformProperty, _temporaryTransform);
            _target.BeginAnimation(UIElement.OpacityProperty, _fade, HandoffBehavior.SnapshotAndReplace);
            if (_rise is not null) _translation!.BeginAnimation(TranslateTransform.YProperty, _rise, HandoffBehavior.SnapshotAndReplace);
        }

        internal void Stop()
        {
            _target.Unloaded -= OnUnloaded;
            _target.IsVisibleChanged -= OnVisibilityChanged;
            _fade.Completed -= OnCompleted;
            _target.BeginAnimation(UIElement.OpacityProperty, null);
            _translation?.BeginAnimation(TranslateTransform.YProperty, null);
            if (_temporaryTransform is not null && ReferenceEquals(_target.RenderTransform, _temporaryTransform))
                _target.SetCurrentValue(UIElement.RenderTransformProperty, _originalTransform);
            if (_temporaryCache is not null && ReferenceEquals(_target.CacheMode, _temporaryCache))
                _target.SetCurrentValue(UIElement.CacheModeProperty, _originalCache);
        }

        private void OnCompleted(object? sender, EventArgs args)
        {
            if (ReferenceEquals(_target.GetValue(ActiveTransitionProperty), this)) TransitionAnimations.Stop(_target);
        }
        private void OnUnloaded(object sender, RoutedEventArgs args) => TransitionAnimations.Stop(_target);
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue) TransitionAnimations.Stop(_target);
        }
    }
}
