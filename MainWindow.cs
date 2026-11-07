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
    private readonly MainViewModel _viewModel;
    private readonly ListBox _historyList;
    private readonly TextBox _searchBox;
    private readonly GlobalHotKey _hotKey;
    private readonly Views.WindowFrame _windowFrame;
    private readonly WindowOverlayHost _overlayHost;
    private readonly Grid _titleBar;
    private readonly TextBlock _maximizeGlyph;
    private readonly SystemTrayIcon _trayIcon;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _closeToTray;
    private bool _hideAfterCopy = true;
    private bool _backdropAttempted;

    public MainWindow(HistoryService? history = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "剪贴板";
        Width = 420;
        Height = 620;
        MinWidth = 380;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            UseAeroCaptionButtons = false,
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(16)
        });
        _windowFrame = new Views.WindowFrame();
        _titleBar = _windowFrame.TitleBar;
        _maximizeGlyph = _windowFrame.MaximizeGlyph;
        _windowFrame.MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        _windowFrame.MaximizeButton.Click += (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        _windowFrame.CloseButton.Click += (_, _) => Close();
        var view = new Views.MainView();
        _windowFrame.WindowContent.Children.Add(view);
        Content = _windowFrame;
        _overlayHost = new WindowOverlayHost(this, _windowFrame);
        _historyList = view.HistoryList;
        _searchBox = view.SearchBox;
        _viewModel = new MainViewModel(this, history ?? new HistoryService(new HistoryStore()),
            UpdateTrayMode, _overlayHost);
        _trayIcon = new SystemTrayIcon(this);
        _trayIcon.OpenRequested += (_, _) => OnHotKeyPressed(this, EventArgs.Empty);
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
        SizeChanged += (_, _) => { _viewModel.UpdateWindowSize(); UpdateWindowFrame(); };
        StateChanged += (_, _) => UpdateWindowFrame();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateWindowFrame));
        ContentRendered += (_, _) => UpdateWindowFrame();
        SourceInitialized += (_, _) => ApplyWindowBackdrop();
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
        try { _hotKey.Start(); }
        catch (Exception exception) { _viewModel.ReportHotKeyFailure(exception); }
        _searchBox.Focus();
    }

    private void UpdateWindowFrame()
    {
        if (WindowState == WindowState.Minimized) return;
        Thickness resizeBorder = SystemParameters.WindowResizeBorderThickness;
        _windowFrame.Margin = WindowState == WindowState.Maximized ? resizeBorder : new Thickness();
        const double captionHeight = 40;
        _titleBar.Height = captionHeight;
        var chrome = WindowChrome.GetWindowChrome(this);
        chrome.CaptionHeight = captionHeight;
        chrome.GlassFrameThickness = new Thickness(-1);
        _maximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
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
        if (!_hideAfterCopy || !IsVisible || _overlayHost.IsOpen) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsVisible) Hide();
        }));
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || _overlayHost.IsOpen || _viewModel.IsClosing) return;
        args.Handled = true;
        if (Keyboard.FocusedElement is TextBox { Text.Length: > 0 } search && ReferenceEquals(search, _searchBox))
        {
            _viewModel.ClearSearchCommand.Execute(null);
            return;
        }
        if (_closeToTray) Hide();
        else Close();
    }

    private void OnHotKeyPressed(object? sender, EventArgs args)
    {
        if (_viewModel.IsClosing) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_overlayHost.IsOpen) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_viewModel.IsClosing || !IsEnabled) return;
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
