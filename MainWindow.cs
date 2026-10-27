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
    private readonly Grid _windowFrame;
    private readonly Grid _titleBar;
    private readonly TextBlock _maximizeGlyph;
    private readonly SystemTrayIcon _trayIcon;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _closeToTray;
    private bool _backdropAttempted;

    public MainWindow(HistoryService? history = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "剪贴板";
        Width = 340;
        Height = 500;
        MinWidth = 320;
        MinHeight = 400;
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
        _windowFrame = ViewLoader.Load<Grid>("Views/WindowFrame.xaml");
        _titleBar = (Grid)_windowFrame.FindName("TitleBar");
        _maximizeGlyph = (TextBlock)_windowFrame.FindName("MaximizeGlyph");
        ((Button)_windowFrame.FindName("MinimizeButton")).Click += (_, _) => WindowState = WindowState.Minimized;
        ((Button)_windowFrame.FindName("MaximizeButton")).Click += (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        ((Button)_windowFrame.FindName("CloseButton")).Click += (_, _) => Close();
        var view = ViewLoader.Load<Grid>("Views/MainView.xaml");
        ((Grid)_windowFrame.FindName("WindowContent")).Children.Add(view);
        Content = _windowFrame;
        _historyList = (ListBox)view.FindName("HistoryList");
        _searchBox = (TextBox)view.FindName("SearchBox");
        _viewModel = new MainViewModel(this, history ?? new HistoryService(new HistoryStore()), UpdateTrayMode);
        _trayIcon = new SystemTrayIcon(this);
        _trayIcon.OpenRequested += (_, _) => OnHotKeyPressed(this, EventArgs.Empty);
        _trayIcon.PauseRequested += (_, _) => _viewModel.PauseCommand.Execute(null);
        _trayIcon.ExitRequested += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ExitApplication));
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
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
    }

    private void FocusSelectedRecord()
    {
        if (_historyList.SelectedItem is null) { _historyList.Focus(); return; }
        _historyList.ScrollIntoView(_historyList.SelectedItem);
        _historyList.UpdateLayout();
        if (_historyList.ItemContainerGenerator.ContainerFromItem(_historyList.SelectedItem) is ListBoxItem container) container.Focus();
        else _historyList.Focus();
    }

    private void OnHotKeyPressed(object? sender, EventArgs args)
    {
        if (_viewModel.IsClosing) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        var dialog = OwnedWindows.Cast<Window>().LastOrDefault(window => window.IsVisible);
        if (dialog is not null) { dialog.Activate(); return; }
        Activate();
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
