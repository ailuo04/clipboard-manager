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
    private readonly ColumnDefinition _captionButtonsSpace;
    private bool _allowClose;

    public MainWindow(HistoryService? history = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "剪贴板";
        Width = 360;
        Height = 520;
        MinWidth = 320;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        WindowChrome.SetWindowChrome(this, new WindowChrome { UseAeroCaptionButtons = true });
        _windowFrame = ViewLoader.Load<Grid>("Views/WindowFrame.xaml");
        _titleBar = (Grid)_windowFrame.FindName("TitleBar");
        _captionButtonsSpace = (ColumnDefinition)_windowFrame.FindName("CaptionButtonsSpace");
        var view = ViewLoader.Load<Grid>("Views/MainView.xaml");
        ((Grid)_windowFrame.FindName("WindowContent")).Children.Add(view);
        Content = _windowFrame;
        _historyList = (ListBox)view.FindName("HistoryList");
        _searchBox = (TextBox)view.FindName("SearchBox");
        _viewModel = new MainViewModel(this, history ?? new HistoryService(new HistoryStore()));
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
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => _hotKey.Dispose();
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
        double captionHeight = Math.Max(32, SystemParameters.CaptionHeight + resizeBorder.Top);
        _titleBar.Height = captionHeight;
        var chrome = WindowChrome.GetWindowChrome(this);
        chrome.CaptionHeight = captionHeight + _windowFrame.Margin.Top - resizeBorder.Top;
        chrome.GlassFrameThickness = new Thickness(0, captionHeight + _windowFrame.Margin.Top, 0, 0);

        // DWM reports physical pixels; reserve its actual button bounds at this window's DPI.
        double reservedWidth = 146;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DwmwaCaptionButtonBounds, out var bounds,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NativeRect>()) == 0 && bounds.Right > bounds.Left)
        {
            reservedWidth = Math.Max(0, ActualWidth - _windowFrame.Margin.Right - bounds.Left / VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
        _captionButtonsSpace.Width = new GridLength(reservedWidth);
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

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
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
