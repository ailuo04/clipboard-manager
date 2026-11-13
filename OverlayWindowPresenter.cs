using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager;

internal sealed record WindowPresentationState(WindowState WindowState, double Left, double Top, double Width, double Height,
    bool Topmost)
{
    internal static WindowPresentationState Capture(Window owner)
    {
        Rect restore = GetNormalBounds(owner);
        return new WindowPresentationState(owner.WindowState, restore.Left, restore.Top, restore.Width, restore.Height,
            owner.Topmost);
    }

    internal static Rect GetNormalBounds(Window owner)
    {
        Rect restore = owner.RestoreBounds;
        if (IsUsable(restore)) return restore;
        double width = IsFinite(owner.Width) && owner.Width > 0 ? owner.Width : Math.Max(1, owner.ActualWidth);
        double height = IsFinite(owner.Height) && owner.Height > 0 ? owner.Height : Math.Max(1, owner.ActualHeight);
        Rect workArea = SystemParameters.WorkArea;
        double left = IsFinite(owner.Left) ? owner.Left : workArea.Left + (workArea.Width - width) / 2;
        double top = IsFinite(owner.Top) ? owner.Top : workArea.Top + (workArea.Height - height) / 2;
        return new Rect(left, top, width, height);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsUsable(Rect value) =>
        !double.IsNaN(value.Left) && !double.IsNaN(value.Top) && !double.IsNaN(value.Width) && !double.IsNaN(value.Height)
        && !double.IsInfinity(value.Left) && !double.IsInfinity(value.Top)
        && value.Width > 0 && value.Height > 0;
}

/// <summary>Owns the owner-window presentation for overlay sessions: page sizing, snapshots, and restoration.</summary>
internal sealed class OverlayWindowPresenter
{
    private const double WidgetMargin = 8;

    private readonly Window _owner;
    private readonly Grid _mainSurface;
    private readonly Border _overlayPanel;
    private WindowSnapshot? _snapshot;
    private bool _mainSurfaceWasEnabled;
    private bool _mainSurfaceWasHitTestVisible;

    internal OverlayWindowPresenter(Window owner, Grid mainSurface, Border overlayPanel)
    {
        _owner = owner;
        _mainSurface = mainSurface;
        _overlayPanel = overlayPanel;
    }

    internal WindowPresentationState CapturePresentationState() => WindowPresentationState.Capture(_owner);

    internal void BeginSession(double blurRadius)
    {
        CaptureSnapshot(_mainSurface.Effect);
        _mainSurfaceWasEnabled = _mainSurface.IsEnabled;
        _mainSurfaceWasHitTestVisible = _mainSurface.IsHitTestVisible;
        DisableMainSurface(blurRadius);
    }

    internal void CaptureSnapshot(Effect? mainSurfaceEffect)
    {
        _snapshot = new WindowSnapshot(_owner.WindowState, WindowPresentationState.GetNormalBounds(_owner),
            _owner.Topmost, Keyboard.FocusedElement as IInputElement, mainSurfaceEffect);
    }

    private void DisableMainSurface(double blurRadius)
    {
        _mainSurface.IsEnabled = false;
        _mainSurface.IsHitTestVisible = false;
        _mainSurface.Effect = new BlurEffect
        {
            Radius = blurRadius,
            RenderingBias = RenderingBias.Performance
        };
    }

    internal void ApplyPageSize(OverlaySize size)
    {
        Size desired = OverlaySizePolicy.GetDesiredSize(size);
        // Every page is a card inside the temporary overlay window bounds.
        double roomWidth = FiniteOr(_owner.Width, _owner.ActualWidth) - WidgetMargin * 2;
        double roomHeight = FiniteOr(_owner.Height, _owner.ActualHeight) - WidgetMargin * 2;
        _overlayPanel.Width = roomWidth > 0 ? Math.Min(desired.Width, roomWidth) : desired.Width;
        _overlayPanel.Height = double.NaN;
        _overlayPanel.MaxHeight = roomHeight > 0 ? Math.Min(desired.Height, roomHeight) : desired.Height;
    }

    private static double FiniteOr(double value, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;

    internal void RestoreWindowState(WindowPresentationState state)
    {
        if (state.WindowState != WindowState.Maximized && state.WindowState != WindowState.Minimized)
            _owner.WindowState = WindowState.Normal;
        _owner.Left = state.Left;
        _owner.Top = state.Top;
        _owner.Width = state.Width;
        _owner.Height = state.Height;
        _owner.Topmost = state.Topmost;
        _owner.WindowState = state.WindowState;
    }

    internal void RestoreMainWindow()
    {
        _mainSurface.IsEnabled = _mainSurfaceWasEnabled;
        _mainSurface.IsHitTestVisible = _mainSurfaceWasHitTestVisible;
        if (_snapshot is not WindowSnapshot snapshot) return;
        _snapshot = null;

        try { RestoreWindowState(new WindowPresentationState(snapshot.WindowState, snapshot.RestoreBounds.Left, snapshot.RestoreBounds.Top,
            snapshot.RestoreBounds.Width, snapshot.RestoreBounds.Height, snapshot.Topmost)); }
        catch (Exception exception) { AppLogger.LogException("恢复覆盖层前的窗口位置失败", exception); }
        finally
        {
            try { _mainSurface.Effect = snapshot.MainSurfaceEffect; }
            catch (Exception exception) { AppLogger.LogException("恢复覆盖层前的主界面效果失败", exception); }
            try
            {
                _owner.Topmost = snapshot.Topmost;
                if (snapshot.FocusedElement is null || Keyboard.Focus(snapshot.FocusedElement) is null) _owner.Focus();
            }
            catch (Exception exception) { AppLogger.LogException("恢复覆盖层前的键盘焦点失败", exception); }
        }
    }

    /// <summary>Work area, in device-independent units, of the monitor the window is on.</summary>
    internal static Rect GetWorkArea(Window owner)
    {
        IntPtr handle = new WindowInteropHelper(owner).Handle;
        if (handle != IntPtr.Zero)
        {
            IntPtr monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var transform = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice;
                if (transform is not null)
                {
                    Point topLeft = transform.Value.Transform(new Point(info.Work.Left, info.Work.Top));
                    Point bottomRight = transform.Value.Transform(new Point(info.Work.Right, info.Work.Bottom));
                    return new Rect(topLeft, bottomRight);
                }
            }
        }
        return SystemParameters.WorkArea;
    }

    private sealed record WindowSnapshot(WindowState WindowState, Rect RestoreBounds, bool Topmost,
        IInputElement? FocusedElement, Effect? MainSurfaceEffect);
}
