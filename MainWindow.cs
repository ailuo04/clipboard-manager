using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using ClipboardManager.ViewModels;

namespace ClipboardManager;

public sealed class MainWindow : Window
{
    private const double CapsuleWidth = 240;
    private const double CapsuleHeight = 40;
    private const double PanelWidth = 300;
    private const double PanelHeight = 400;
    private const double EdgeMargin = 24;
    private const double DragThreshold = 3;
    private const int WmMouseActivate = 0x0021;
    private const int WmActivateApp = 0x001C;

    private readonly MainViewModel _viewModel;
    private readonly ListBox _historyList;
    private readonly TextBox _searchBox;
    private readonly GlobalHotKey _hotKey;
    private readonly Views.WindowFrame _windowFrame;
    private readonly Views.MainView _view;
    private readonly WindowOverlayHost _overlayHost;
    private readonly SystemTrayIcon _trayIcon;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _closeToTray;
    private bool _hideAfterCopy = true;
    private bool _restoreHistoryPopupAfterOverlay;
    private bool _deactivationDeferredForRecordDrag;
    private bool _applicationDeactivated;
    private IntPtr _foregroundBeforeShow;
    private bool _backdropAttempted;
    // Where the capsule sits. The expanded panel opens from here, shifted only as far as the work area requires.
    private Point _anchor;

    public MainWindow(HistoryService? history = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "剪贴板";
        Width = CapsuleWidth;
        Height = CapsuleHeight;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        // The capsule appears at startup without taking the focus from whatever the user is doing.
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Rect workArea = SystemParameters.WorkArea;
        _anchor = new Point(workArea.Right - CapsuleWidth - EdgeMargin, workArea.Bottom - CapsuleHeight - EdgeMargin);
        Left = _anchor.X;
        Top = _anchor.Y;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            UseAeroCaptionButtons = false,
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(16)
        });
        _windowFrame = new Views.WindowFrame();
        _view = new Views.MainView();
        _view.CapsuleDragHandle.MouseLeftButtonDown += OnCapsuleDragHandleMouseDown;
        _view.HistoryToggleRequested += OnHistoryToggleRequested;
        _view.RecordDragCompleted += OnRecordDragCompleted;
        _view.DragHandle.MouseLeftButtonDown += OnDragHandleMouseDown;
        _view.ToggleRequested += (_, _) =>
        {
            _view.CloseHistoryPopup();
            if (_viewModel!.IsWidgetCollapsed) ExpandAndFocus();
            else SetCollapsed(true);
        };
        _view.ExitRequested += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ExitApplication));
        _windowFrame.WindowContent.Children.Add(_view);
        Content = _windowFrame;
        _overlayHost = new WindowOverlayHost(this, _windowFrame);
        _overlayHost.SessionStarting += OnOverlaySessionStarting;
        _overlayHost.SessionEnded += OnOverlaySessionEnded;
        _historyList = _view.HistoryList;
        _searchBox = _view.SearchBox;
        _viewModel = new MainViewModel(this, history ?? new HistoryService(new HistoryStore()),
            UpdateTrayMode, _overlayHost);
        _trayIcon = new SystemTrayIcon(this);
        _trayIcon.OpenRequested += (_, _) => ExpandAndFocus();
        _trayIcon.PauseRequested += (_, _) => _viewModel.PauseCommand.Execute(null);
        _trayIcon.ExitRequested += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ExitApplication));
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.CopyCompleted += OnCopyCompleted;
        _viewModel.SettingsApplied += OnSettingsApplied;
        DataContext = _viewModel;
        _hotKey = new GlobalHotKey(this);
        _hotKey.Pressed += OnHotKeyPressed;
        _historyList.PreviewKeyDown += OnHistoryKeyDown;
        _historyList.PreviewMouseDown += OnHistoryMouseDown;
        _historyList.SelectionChanged += (_, _) =>
        {
            if (_historyList.SelectedItem is not null) _historyList.ScrollIntoView(_historyList.SelectedItem);
        };
        _searchBox.PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.None && (args.Key is Key.Down or Key.Up) && _historyList.Items.Count > 0)
            {
                args.Handled = true;
                if (_historyList.SelectedIndex < 0) _historyList.SelectedIndex = args.Key == Key.Up ? _historyList.Items.Count - 1 : 0;
                FocusSelectedRecord();
            }
        };
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewMouseDown += OnWindowPreviewMouseDown;
        Deactivated += OnDeactivated;
        SizeChanged += (_, _) => { _viewModel.UpdateWindowSize(); UpdateWindowFrame(); };
        StateChanged += (_, _) => UpdateWindowFrame();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateWindowFrame));
        ContentRendered += (_, _) => UpdateWindowFrame();
        SourceInitialized += (_, _) =>
        {
            ApplyWindowBackdrop();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        ThemeManager.RegisterWindow(this);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Application.Current.SessionEnding += OnSessionEnding;
        Closed += (_, _) =>
        {
            _overlayHost.Dispose();
            _hotKey.Dispose();
            Application.Current.SessionEnding -= OnSessionEnding;
            _trayIcon.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        UpdateWindowFrame();
        await _viewModel.InitializeAsync();
        if (_viewModel.IsClosing) return;
        ApplyWindowSettings();
        RestoreWidgetPosition();
        try { _hotKey.Start(); }
        catch (Exception exception) { _viewModel.ReportHotKeyFailure(exception); }
    }

    private void UpdateWindowFrame()
    {
        if (WindowState == WindowState.Minimized) return;
        Thickness resizeBorder = SystemParameters.WindowResizeBorderThickness;
        _windowFrame.Margin = WindowState == WindowState.Maximized ? resizeBorder : new Thickness();
        var chrome = WindowChrome.GetWindowChrome(this);
        // There is no title bar: the widget is moved by its own capsule and drag handle.
        chrome.CaptionHeight = 0;
        chrome.GlassFrameThickness = new Thickness(-1);
    }

    private void ApplyWindowBackdrop()
    {
        if (_backdropAttempted) return;
        _backdropAttempted = true;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        int backdropType = NativeMethods.DwmsbtTransientWindow;
        _ = NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaSystemBackdropType, ref backdropType,
            System.Runtime.InteropServices.Marshal.SizeOf<int>());
        int cornerPreference = 2;
        _ = NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaWindowCornerPreference, ref cornerPreference,
            System.Runtime.InteropServices.Marshal.SizeOf<int>());
        int darkMode = ThemeManager.IsDark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref darkMode,
            System.Runtime.InteropServices.Marshal.SizeOf<int>());
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // A click is about to activate the widget: remember who had the focus so a copy can hand it back.
        if (message == WmMouseActivate) RememberForeground();
        if (message == WmActivateApp)
        {
            // A popup/menu can already have deactivated the owner. Leaving the app
            // must still dismiss history even if no second Window.Deactivated fires.
            _applicationDeactivated = wParam == IntPtr.Zero;
            if (_applicationDeactivated && _viewModel.IsWidgetCollapsed)
                OnDeactivated(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    private void RememberForeground()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != new WindowInteropHelper(this).Handle) _foregroundBeforeShow = foreground;
    }

    private void RestoreWidgetPosition()
    {
        if (_viewModel.WidgetPosition is not Point saved || _overlayHost.IsOpen) return;
        // Move first so the work area is that of the monitor the widget was left on.
        Left = saved.X;
        Top = saved.Y;
        _anchor = ClampToWorkArea(saved, CapsuleWidth, CapsuleHeight);
        PlaceWidget();
    }

    private Point ClampToWorkArea(Point position, double width, double height)
    {
        Rect area = OverlayWindowPresenter.GetWorkArea(this);
        return new Point(Math.Clamp(position.X, area.Left, Math.Max(area.Left, area.Right - width)),
            Math.Clamp(position.Y, area.Top, Math.Max(area.Top, area.Bottom - height)));
    }

    private void PlaceWidget()
    {
        bool collapsed = _viewModel.IsWidgetCollapsed;
        double width = collapsed ? CapsuleWidth : PanelWidth;
        double height = collapsed ? CapsuleHeight : PanelHeight;
        Point position = ClampToWorkArea(_anchor, width, height);
        Left = position.X;
        Top = position.Y;
        Width = width;
        Height = height;
    }

    private void SetCollapsed(bool collapsed)
    {
        // An open overlay page owns the window bounds until it closes.
        if (_overlayHost.IsOpen || WindowState != WindowState.Normal || _viewModel.IsWidgetCollapsed == collapsed) return;
        _viewModel.IsWidgetCollapsed = collapsed;
        PlaceWidget();
    }

    private void OnHistoryToggleRequested(object? sender, EventArgs args)
    {
        if (_view.IsHistoryPopupOpen)
        {
            _view.CloseHistoryPopup();
            return;
        }

        OpenHistoryPopup();
    }

    private void OpenHistoryPopup()
    {
        Rect workArea = OverlayWindowPresenter.GetWorkArea(this);
        Point capsuleOrigin = _view.Capsule.TranslatePoint(new Point(), this);
        var target = new Rect(Left + capsuleOrigin.X, Top + capsuleOrigin.Y,
            _view.Capsule.ActualWidth, _view.Capsule.ActualHeight);
        Size desired = _view.MeasureHistoryPopup(Math.Min(PanelWidth, workArea.Width));
        CapsulePopupLayout layout = CapsulePopupPositioning.Calculate(workArea, target, desired);
        _view.ShowHistoryPopup(layout);
    }

    private void OnOverlaySessionStarting(object? sender, EventArgs args)
    {
        _restoreHistoryPopupAfterOverlay = _view.IsHistoryPopupOpen;
        _view.CloseHistoryPopup();
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (_viewModel.IsWidgetCollapsed) PlaceOverlayBounds();
        Activate();
    }

    private void OnOverlaySessionEnded(object? sender, EventArgs args)
    {
        bool restoreHistoryPopup = _restoreHistoryPopupAfterOverlay;
        _restoreHistoryPopupAfterOverlay = false;
        if (!restoreHistoryPopup || _viewModel.IsClosing || _exitRequested) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_overlayHost.IsOpen && !_viewModel.IsClosing && !_exitRequested && _viewModel.IsWidgetCollapsed)
                OpenHistoryPopup();
        }));
    }

    private void PlaceOverlayBounds()
    {
        Point position = ClampToWorkArea(_anchor, PanelWidth, PanelHeight);
        Left = position.X;
        Top = position.Y;
        Width = PanelWidth;
        Height = PanelHeight;
    }
    private void CollapseAndReturnFocus()
    {
        SetCollapsed(true);
        if (_viewModel.IsWidgetCollapsed) RestoreForeground(_foregroundBeforeShow);
    }

    private void OnCapsuleDragHandleMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (_overlayHost.IsOpen) return;
        _view.CloseHistoryPopup();
        args.Handled = true;
        var start = new Point(Left, Top);
        if (!TryDragMove()) return;
        if (Math.Abs(Left - start.X) >= DragThreshold || Math.Abs(Top - start.Y) >= DragThreshold)
            CommitWidgetPosition();
    }

    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (_overlayHost.IsOpen) return;
        args.Handled = true;
        if (TryDragMove()) CommitWidgetPosition();
    }

    private bool TryDragMove()
    {
        try { DragMove(); return true; }
        // The button was already released by the time the handler ran.
        catch (InvalidOperationException) { return false; }
    }

    private void CommitWidgetPosition()
    {
        _anchor = ClampToWorkArea(new Point(Left, Top), CapsuleWidth, CapsuleHeight);
        PlaceWidget();
        _ = _viewModel.SaveWidgetPositionAsync(_anchor.X, _anchor.Y);
    }

    private static bool ShouldCloseHistoryPopupOnDeactivation(bool isPopupOpen, bool isContextMenuOpen,
        bool isPointerOverPopup) =>
        isPopupOpen && !isContextMenuOpen && !isPointerOverPopup;

    private static bool ShouldDeferDeactivationForRecordDrag(bool isRecordDragInProgress) => isRecordDragInProgress;

    private static bool ShouldReplayDeactivationAfterRecordDrag(bool wasDeferred, bool isActive) => wasDeferred && !isActive;

    private void OnRecordDragCompleted(object? sender, EventArgs args)
    {
        if (!_deactivationDeferredForRecordDrag) return;
        _deactivationDeferredForRecordDrag = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!ShouldReplayDeactivationAfterRecordDrag(true, IsActive)) return;
            OnDeactivated(this, EventArgs.Empty);
        }));
    }

    private void OnDeactivated(object? sender, EventArgs args)
    {
        if (ShouldDeferDeactivationForRecordDrag(_view.IsRecordDragInProgress))
        {
            _deactivationDeferredForRecordDrag = true;
            return;
        }
        if (_viewModel.IsWidgetCollapsed)
        {
            bool applicationDeactivated = _applicationDeactivated;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (IsActive || _viewModel.IsClosing || (applicationDeactivated && !_applicationDeactivated)) return;
                if (ShouldDeferDeactivationForRecordDrag(_view.IsRecordDragInProgress))
                {
                    _deactivationDeferredForRecordDrag = true;
                    return;
                }
                if (_applicationDeactivated || ShouldCloseHistoryPopupOnDeactivation(_view.IsHistoryPopupOpen, _view.IsContextMenuOpen,
                        _view.IsHistoryPopupPointerOver))
                    _view.CloseHistoryPopup();
            }));
            return;
        }
        if (_viewModel.IsAlwaysOnTop || _overlayHost.IsOpen || _view.IsMenuOpen || _viewModel.IsClosing) return;
        // A confirmation dialog owned by the widget disables it and takes the focus; that is not the user leaving.
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && !NativeMethods.IsWindowEnabled(handle)) return;
        SetCollapsed(true);
    }
    private void OnHistoryMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Left && args.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(_historyList, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OnHistoryKeyDown(object sender, KeyEventArgs args)
    {
        if (!_historyList.IsKeyboardFocusWithin || _viewModel.IsClosing) return;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && (args.Key is Key.Up or Key.Down))
        {
            args.Handled = true;
            int count = _historyList.Items.Count;
            if (count == 0) return;
            int index = _historyList.SelectedIndex;
            _historyList.SelectedIndex = index < 0 ? (args.Key == Key.Up ? count - 1 : 0)
                : Math.Clamp(index + (args.Key == Key.Up ? -1 : 1), 0, count - 1);
            FocusSelectedRecord();
        }
        else if ((args.Key == Key.Enter && modifiers == ModifierKeys.None) || (args.Key == Key.C && modifiers == ModifierKeys.Control))
        {
            args.Handled = true;
            if (_historyList.SelectedItem is RecordViewModel item && item.CopyCommand.CanExecute(null)) item.CopyCommand.Execute(null);
        }
        else if (args.Key == Key.Delete && modifiers == ModifierKeys.None)
        {
            args.Handled = true;
            if (_historyList.SelectedItem is RecordViewModel item && item.DeleteCommand.CanExecute(null)) item.DeleteCommand.Execute(null);
        }
        else if (args.Key == Key.P && modifiers == ModifierKeys.Control)
        {
            args.Handled = true;
            if (_historyList.SelectedItem is RecordViewModel item && item.PinCommand.CanExecute(null)) item.PinCommand.Execute(null);
        }
        else if (modifiers == ModifierKeys.None && TryGetDigit(args.Key, out int digit) && digit >= 1 && digit <= 9)
        {
            args.Handled = true;
            int index = digit - 1;
            if (index >= _historyList.Items.Count) return;
            if (_historyList.Items[index] is RecordViewModel item && item.CopyCommand.CanExecute(null))
            {
                _historyList.SelectedIndex = index;
                item.CopyCommand.Execute(null);
            }
        }
    }

    private static bool TryGetDigit(Key key, out int digit)
    {
        digit = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
            _ => 0
        };
        return digit > 0;
    }

    private void FocusSelectedRecord()
    {
        if (_historyList.SelectedItem is null) { _historyList.Focus(); return; }
        _historyList.ScrollIntoView(_historyList.SelectedItem);
        _historyList.UpdateLayout();
        if (_historyList.ItemContainerGenerator.ContainerFromItem(_historyList.SelectedItem) is ListBoxItem container) container.Focus();
        else _historyList.Focus();
    }

    private void ApplyWindowSettings()
    {
        _hideAfterCopy = _viewModel.HideAfterCopy;
    }

    private void OnSettingsApplied(object? sender, EventArgs args)
    {
        _hideAfterCopy = _viewModel.HideAfterCopy;
    }

    private void OnCopyCompleted(object? sender, EventArgs args)
    {
        _view.CloseHistoryPopup();
        if (_viewModel.IsWidgetCollapsed)
        {
            IntPtr previous = _foregroundBeforeShow;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RestoreForeground(previous)));
            return;
        }
        if (!_hideAfterCopy || _overlayHost.IsOpen) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CollapseAndReturnFocus));
    }

    private void RestoreForeground(IntPtr window)
    {
        if (window == IntPtr.Zero || window == new WindowInteropHelper(this).Handle) return;
        try { NativeMethods.SetForegroundWindow(window); }
        catch (Exception exception) { AppLogger.LogException("恢复复制前的前台窗口失败", exception); }
    }

    private static bool ShouldCloseHistoryPopupOnMouseDown(bool isPopupOpen, bool isPointerOverPopup, bool isPointerOverToggle) =>
        isPopupOpen && !isPointerOverPopup && !isPointerOverToggle;

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (ShouldCloseHistoryPopupOnMouseDown(_view.IsHistoryPopupOpen,
                _view.IsHistoryPopupPointerOver, _view.IsHistoryTogglePointerOver))
            _view.CloseHistoryPopup();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (_overlayHost.IsOpen || _viewModel.IsClosing) return;
        if (_viewModel.IsWidgetCollapsed && _view.IsHistoryPopupOpen && args.Key == Key.Escape)
        {
            args.Handled = true;
            _view.CloseHistoryPopup();
            return;
        }
        if (_viewModel.IsWidgetCollapsed)
        {
            if (Keyboard.FocusedElement is Button || Keyboard.Modifiers != ModifierKeys.None || args.Key is not (Key.Enter or Key.Space or Key.Down)) return;
            args.Handled = true;
            ExpandAndFocus();
            return;
        }
        if (args.Key != Key.Escape) return;
        args.Handled = true;
        if (Keyboard.FocusedElement is TextBox { Text.Length: > 0 } search && ReferenceEquals(search, _searchBox))
        {
            _viewModel.ClearSearchCommand.Execute(null);
            return;
        }
        CollapseAndReturnFocus();
    }

    /// <summary>Expands the widget when a second launch finds this instance already running.</summary>
    public void ShowFromExternalRequest() => ExpandAndFocus();

    private void OnHotKeyPressed(object? sender, EventArgs args)
    {
        bool expandedInFront = !_viewModel.IsWidgetCollapsed && IsVisible && IsActive && WindowState == WindowState.Normal;
        if (expandedInFront && !_overlayHost.IsOpen && !_viewModel.IsClosing) CollapseAndReturnFocus();
        else ExpandAndFocus();
    }

    private void ExpandAndFocus()
    {
        if (_viewModel.IsClosing) return;
        _view.CloseHistoryPopup();
        RememberForeground();
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        SetCollapsed(false);
        Activate();
        if (_overlayHost.IsOpen) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_viewModel.IsClosing || !IsEnabled || _viewModel.IsWidgetCollapsed || _overlayHost.IsOpen) return;
            if (_historyList.Items.Count == 0) _searchBox.Focus();
            else
            {
                if (_historyList.SelectedIndex < 0) _historyList.SelectedIndex = 0;
                FocusSelectedRecord();
            }
        }));
    }

    public void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private void UpdateTrayMode(bool enabled)
    {
        try
        {
            _trayIcon.SetEnabled(enabled, _viewModel.IsPaused);
            _closeToTray = enabled && _trayIcon.IsEnabled;
        }
        catch (Exception exception)
        {
            _closeToTray = false;
            _viewModel.ReportTrayFailure(exception);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.IsPaused)) _trayIcon.UpdatePausedState(_viewModel.IsPaused);
        if (args.PropertyName == nameof(MainViewModel.HistoryRecords) && _view.IsHistoryPopupOpen)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_view.IsHistoryPopupOpen) OpenHistoryPopup();
            }));
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs args) => _exitRequested = true;

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        _overlayHost.CloseAll(null);
        if (!_exitRequested && _closeToTray && _viewModel.IsReady)
        {
            args.Cancel = true;
            Hide();
            return;
        }
        if (_allowClose) return;
        args.Cancel = true;
        if (_viewModel.IsClosing) return;
        IsEnabled = false;
        _hotKey.Pressed -= OnHotKeyPressed;
        _hotKey.Dispose();
        try { await _viewModel.ShutdownAsync(); }
        catch (Exception exception) { AppLogger.LogException("关闭应用和释放监听资源失败", exception); }
        finally { _allowClose = true; Close(); }
    }
}
