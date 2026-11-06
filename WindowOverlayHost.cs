using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager;

internal enum OverlaySize
{
    Settings,
    Update,
    Updates = Update,
    Details
}

internal static class OverlaySizePolicy
{
    internal static Size GetDesiredSize(OverlaySize size) => size switch
    {
        OverlaySize.Settings => new Size(510, 660),
        OverlaySize.Update => new Size(510, 510),
        OverlaySize.Details => new Size(800, 760),
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "未知的覆盖层尺寸。")
    };
}

internal sealed class WindowOverlayHost : IDisposable
{
    private const double WorkAreaMargin = 18;
    private const double MainSurfaceBlurRadius = 4;
    private readonly Window _owner;
    private readonly Grid _root;
    private readonly Grid _mainSurface;
    private readonly Grid _overlayLayer;
    private readonly Border _overlayPanel;
    private readonly ContentControl _overlayContent;
    private readonly OverlayPageStack<OverlayFrame> _parents = new();
    private bool _mainSurfaceWasEnabled;
    private bool _mainSurfaceWasHitTestVisible;
    private OverlayFrame? _current;
    private long _activationGeneration;
    private WindowSnapshot? _snapshot;
    private bool _sessionActive;
    private bool _closingAll;
    private bool _disposed;

    internal WindowOverlayHost(Window owner, Grid root)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _mainSurface = FindNamed<Grid>(root, "MainSurface");
        _overlayLayer = FindNamed<Grid>(root, "OverlayLayer");
        _overlayPanel = FindNamed<Border>(root, "OverlayPanel");
        _overlayContent = FindNamed<ContentControl>(root, "OverlayContent");
        _overlayLayer.PreviewKeyDown += OnOverlayPreviewKeyDown;
        _overlayLayer.Visibility = Visibility.Collapsed;
        _overlayContent.Content = null;
    }

    internal bool IsOpen => _sessionActive;
    internal int Count => (_current is null ? 0 : 1) + _parents.Count;

    internal Task<bool?> ShowAsync(OverlayView page, OverlaySize size)
    {
        ArgumentNullException.ThrowIfNull(page);
        ThrowIfDisposed();
        VerifyAccess();
        if (_closingAll) throw new InvalidOperationException("覆盖层正在关闭。");

        bool hasParent = _sessionActive;
        if (!hasParent) BeginSession();
        else
        {
            if (_current is null) throw new InvalidOperationException("覆盖层状态无效。");
            _current.Page.CloseRequest = null;
            _current.PreviousFocus = Keyboard.FocusedElement as IInputElement;
            _current.WindowState = WindowPresentationState.Capture(_owner);
            _parents.Push(_current);
        }

        var frame = new OverlayFrame(page, size);
        _current = frame;
        try
        {
            ShowFrame(frame, restoreWindowState: false);
            return frame.Completion.Task;
        }
        catch
        {
            frame.Page.CloseRequest = null;
            try { frame.Page.OnDismissed(null); }
            catch (Exception exception) { AppLogger.LogException("清理未能显示的覆盖层页面失败", exception); }
            frame.Completion.TrySetResult(null);
            if (hasParent && _parents.Pop() is OverlayFrame parent)
            {
                _current = parent;
                try { ShowFrame(parent, restoreWindowState: true); }
                catch (Exception exception)
                {
                    AppLogger.LogException("恢复父覆盖层页面失败", exception);
                    CloseAll(null);
                }
            }
            else
            {
                _current = null;
                EndSession();
            }
            throw;
        }
    }

    internal void CompleteCurrent(bool? result)
    {
        ThrowIfDisposed();
        VerifyAccess();
        if (_current is null) return;

        OverlayFrame frame = _current;
        _current = null;
        frame.Page.CloseRequest = null;
        try { frame.Page.OnDismissed(result); }
        catch (Exception exception) { AppLogger.LogException("关闭覆盖层页面失败", exception); }
        finally { frame.Completion.TrySetResult(result); }

        if (!_sessionActive) return;
        if (_parents.Pop() is OverlayFrame parent)
        {
            _current = parent;
            try { ShowFrame(parent, restoreWindowState: true); }
            catch (Exception exception)
            {
                AppLogger.LogException("恢复父覆盖层页面失败", exception);
                CloseAll(null);
            }
        }
        else EndSession();
    }

    internal void CloseAll(bool? result)
    {
        if (_disposed) return;
        VerifyAccess();
        if (!_sessionActive && _current is null && _parents.Count == 0) return;

        _closingAll = true;
        var frames = new List<OverlayFrame>();
        if (_current is not null) frames.Add(_current);
        while (_parents.Pop() is OverlayFrame parent) frames.Add(parent);
        _current = null;
        _sessionActive = false;

        foreach (OverlayFrame frame in frames) frame.Page.CloseRequest = null;
        try
        {
            foreach (OverlayFrame frame in frames)
            {
                try { frame.Page.OnDismissed(result); }
                catch (Exception exception) { AppLogger.LogException("关闭覆盖层页面失败", exception); }
                finally { frame.Completion.TrySetResult(result); }
            }
        }
        finally
        {
            _overlayContent.Content = null;
            _overlayLayer.Visibility = Visibility.Collapsed;
            try { RestoreMainWindow(); }
            finally { _closingAll = false; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { CloseAll(null); }
        finally
        {
            _overlayLayer.PreviewKeyDown -= OnOverlayPreviewKeyDown;
            _disposed = true;
        }
    }

    private void BeginSession()
    {
        _snapshot = WindowSnapshot.Capture(_owner, _mainSurface.Effect);
        _mainSurfaceWasEnabled = _mainSurface.IsEnabled;
        _mainSurfaceWasHitTestVisible = _mainSurface.IsHitTestVisible;
        _sessionActive = true;
        try
        {
            _mainSurface.IsEnabled = false;
            _mainSurface.IsHitTestVisible = false;
            _mainSurface.Effect = new BlurEffect
            {
                Radius = MainSurfaceBlurRadius,
                RenderingBias = RenderingBias.Performance
            };
            _overlayLayer.Visibility = Visibility.Visible;
        }
        catch
        {
            _sessionActive = false;
            _overlayLayer.Visibility = Visibility.Collapsed;
            RestoreMainWindow();
            throw;
        }
    }

    private void ShowFrame(OverlayFrame frame, bool restoreWindowState)
    {
        if (restoreWindowState && frame.WindowState is not null) RestoreWindowState(frame.WindowState);
        _overlayContent.Content = frame.Page;
        ApplyPageSize(frame.Size);
        frame.Page.Focusable = true;
        frame.Page.IsTabStop = true;
        long activationGeneration = ++_activationGeneration;
        frame.Page.CloseRequest = result =>
        {
            if (activationGeneration == _activationGeneration && ReferenceEquals(_current, frame))
                CompleteCurrent(result);
        };
        FocusPage(frame);
    }

    private void ApplyPageSize(OverlaySize size)
    {
        Size desired = OverlaySizePolicy.GetDesiredSize(size);
        Rect workArea = GetOwnerWorkArea();
        Rect fitted = OverlayWindowGeometry.Fit(workArea, desired, WorkAreaMargin);
        double outerWidth = Math.Min(workArea.Width, fitted.Width + WorkAreaMargin * 2);
        double outerHeight = Math.Min(workArea.Height, fitted.Height + WorkAreaMargin * 2);
        double ownerWidth = Math.Min(workArea.Width, Math.Max(_owner.Width, outerWidth));
        double ownerHeight = Math.Min(workArea.Height, Math.Max(_owner.Height, outerHeight));
        if (_owner.WindowState != WindowState.Maximized
            && (_owner.Width < outerWidth || _owner.Height < outerHeight
                || _owner.Left < workArea.Left || _owner.Top < workArea.Top
                || _owner.Left + _owner.Width > workArea.Right || _owner.Top + _owner.Height > workArea.Bottom))
        {
            _owner.WindowState = WindowState.Normal;
            _owner.Left = workArea.Left + (workArea.Width - ownerWidth) / 2;
            _owner.Top = workArea.Top + (workArea.Height - ownerHeight) / 2;
            _owner.Width = ownerWidth;
            _owner.Height = ownerHeight;
        }

        _overlayPanel.Width = Math.Min(fitted.Width, Math.Max(0, ownerWidth - WorkAreaMargin * 2));
        _overlayPanel.Height = Math.Min(fitted.Height, Math.Max(0, ownerHeight - WorkAreaMargin * 2));
    }

    private void FocusPage(OverlayFrame frame)
    {
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_sessionActive || !ReferenceEquals(_current, frame)) return;
            if (frame.PreviousFocus is not null && Keyboard.Focus(frame.PreviousFocus) is not null) return;
            if (frame.Page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) return;
            Keyboard.Focus(frame.Page);
        }));
    }

    private void EndSession()
    {
        _sessionActive = false;
        _overlayContent.Content = null;
        _overlayLayer.Visibility = Visibility.Collapsed;
        RestoreMainWindow();
    }

    private void RestoreMainWindow()
    {
        _mainSurface.IsEnabled = _mainSurfaceWasEnabled;
        _mainSurface.IsHitTestVisible = _mainSurfaceWasHitTestVisible;
        if (_snapshot is not WindowSnapshot snapshot) return;
        _snapshot = null;

        try { RestoreWindowState(new WindowPresentationState(snapshot.WindowState, snapshot.Left, snapshot.Top, snapshot.Width, snapshot.Height, snapshot.Topmost)); }
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

    private void RestoreWindowState(WindowPresentationState state)
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

    private Rect GetOwnerWorkArea()
    {
        IntPtr handle = new WindowInteropHelper(_owner).Handle;
        if (handle != IntPtr.Zero)
        {
            IntPtr monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var transform = PresentationSource.FromVisual(_owner)?.CompositionTarget?.TransformFromDevice;
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

    private void OnOverlayPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || _current is null) return;
        args.Handled = true;
        CompleteCurrent(null);
    }

    private static T FindNamed<T>(FrameworkElement root, string name) where T : FrameworkElement
    {
        return root.FindName(name) as T
            ?? throw new InvalidOperationException($"WindowFrame.xaml 缺少命名元素：{name}");
    }

    private void VerifyAccess()
    {
        if (!_owner.Dispatcher.CheckAccess()) throw new InvalidOperationException("覆盖层宿主必须在窗口 UI 线程调用。");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowOverlayHost));
    }

    private sealed class OverlayFrame
    {
        internal OverlayFrame(OverlayView page, OverlaySize size)
        {
            Page = page;
            Size = size;
            Completion = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal OverlayView Page { get; }
        internal OverlaySize Size { get; }
        internal TaskCompletionSource<bool?> Completion { get; }
        internal WindowPresentationState? WindowState { get; set; }
        internal IInputElement? PreviousFocus { get; set; }
    }

    private sealed record WindowSnapshot(WindowState WindowState, double Left, double Top, double Width, double Height,
        bool Topmost, IInputElement? FocusedElement, Effect? MainSurfaceEffect)
    {
        internal static WindowSnapshot Capture(Window owner, Effect? mainSurfaceEffect)
        {
            Rect restore = GetNormalBounds(owner);
            return new WindowSnapshot(owner.WindowState, restore.Left, restore.Top, restore.Width, restore.Height,
                owner.Topmost, Keyboard.FocusedElement as IInputElement, mainSurfaceEffect);
        }
    }

    private sealed record WindowPresentationState(WindowState WindowState, double Left, double Top, double Width, double Height,
        bool Topmost)
    {
        internal static WindowPresentationState Capture(Window owner)
        {
            Rect restore = GetNormalBounds(owner);
            return new WindowPresentationState(owner.WindowState, restore.Left, restore.Top, restore.Width, restore.Height,
                owner.Topmost);
        }
    }

    private static Rect GetNormalBounds(Window owner)
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
