using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager;

internal enum OverlaySize
{
    Settings,
    Update,
    Details,
    ClearConfirmation
}
internal static class OverlaySizePolicy
{
    internal static Size GetDesiredSize(OverlaySize size) => size switch
    {
        OverlaySize.Settings => new Size(284, 384),
        OverlaySize.Update => new Size(264, 384),
        OverlaySize.Details => new Size(800, 760),
        OverlaySize.ClearConfirmation => new Size(284, 280),
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "未知的覆盖层尺寸。")
    };

    /// <summary>
    /// Settings and updates open as a card inside the widget, at most the desired size and no taller than their content.
    /// Details needs room for full text and images, so it enlarges the window instead.
    /// </summary>
    internal static bool StaysInWidget(OverlaySize size) => size is OverlaySize.Settings or OverlaySize.Update or OverlaySize.ClearConfirmation;
}

internal sealed class WindowOverlayHost : IDisposable
{
    private const double MainSurfaceBlurRadius = 4;
    private readonly Window _owner;
    private readonly Grid _root;
    private readonly Grid _overlayLayer;
    private readonly ContentControl _overlayContent;
    private readonly OverlayWindowPresenter _presenter;
    private readonly Stack<OverlayFrame> _parents = new();
    private OverlayFrame? _current;
    private long _activationGeneration;
    private bool _sessionActive;
    private bool _closingAll;
    private bool _disposed;

    internal WindowOverlayHost(Window owner, Grid root)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _overlayLayer = FindNamed<Grid>(root, "OverlayLayer");
        _overlayContent = FindNamed<ContentControl>(root, "OverlayContent");
        _presenter = new OverlayWindowPresenter(owner, FindNamed<Grid>(root, "MainSurface"), FindNamed<Border>(root, "OverlayPanel"));
        _overlayLayer.PreviewKeyDown += OnOverlayPreviewKeyDown;
        _overlayLayer.Visibility = Visibility.Collapsed;
        _overlayContent.Content = null;
        if (root is Views.WindowFrame frame) frame.BackRequested += OnFrameBackRequested;
    }

    /// <summary>Raised before the first page of a session is shown, while the owner window still has its own bounds.</summary>
    internal event EventHandler? SessionStarting;
    internal event EventHandler? SessionEnded;

    internal bool IsOpen => _sessionActive;
    internal int Count => (_current is null ? 0 : 1) + _parents.Count;

    internal Task<bool?> ShowAsync(OverlayView page, OverlaySize size)
    {
        ArgumentNullException.ThrowIfNull(page);
        ThrowIfDisposed();
        VerifyAccess();
        if (_closingAll) throw new InvalidOperationException("覆盖层正在关闭。");

        bool hasParent = _sessionActive;
        if (!hasParent)
        {
            BeginSession();
        }
        else
        {
            if (_current is null) throw new InvalidOperationException("覆盖层状态无效。");
            _current.Page.CloseRequest = null;
            _current.PreviousFocus = Keyboard.FocusedElement as IInputElement;
            _current.WindowState = _presenter.CapturePresentationState();
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
            if (hasParent && _parents.TryPop(out OverlayFrame? prior) && prior is not null)
            {
                _current = prior;
                try { ShowFrame(prior, restoreWindowState: true); }
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
        if (_parents.TryPop(out OverlayFrame? parent) && parent is not null)
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
        while (_parents.TryPop(out OverlayFrame? parent) && parent is not null) frames.Add(parent);
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
            SetPageHeader(null);
            try { _presenter.RestoreMainWindow(); }
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
        _sessionActive = true;
        try
        {
            _presenter.BeginSession(MainSurfaceBlurRadius);
            _overlayLayer.Visibility = Visibility.Visible;
            SessionStarting?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            _sessionActive = false;
            _overlayLayer.Visibility = Visibility.Collapsed;
            _presenter.RestoreMainWindow();
            throw;
        }
    }

    private void ShowFrame(OverlayFrame frame, bool restoreWindowState)
    {
        if (restoreWindowState && frame.WindowState is not null) _presenter.RestoreWindowState(frame.WindowState);
        _overlayContent.Content = frame.Page;
        _presenter.ApplyPageSize(frame.Size);
        frame.Page.Focusable = true;
        frame.Page.IsTabStop = true;
        long activationGeneration = ++_activationGeneration;
        frame.Page.CloseRequest = result =>
        {
            if (activationGeneration == _activationGeneration && ReferenceEquals(_current, frame))
                CompleteCurrent(result);
        };
        SetPageHeader(frame.Page.PageTitle);
        (_root as Views.WindowFrame)?.PlayOverlayEnter();
        FocusPage(frame);
    }

    private void SetPageHeader(string? title)
    {
        if (_root is not Views.WindowFrame frame) return;
        frame.SetPageHeader(title, canGoBack: _parents.Count > 0);
    }

    private void OnFrameBackRequested(object? sender, EventArgs e)
    {
        if (_current is null) return;
        CompleteCurrent(null);
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
        SetPageHeader(null);
        try { _presenter.RestoreMainWindow(); }
        finally { SessionEnded?.Invoke(this, EventArgs.Empty); }
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
}
