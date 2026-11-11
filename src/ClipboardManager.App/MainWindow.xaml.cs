using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ClipboardManager.App.Views;
using ClipboardManager.App.Services;
using ClipboardManager.App.ViewModels;
using ClipboardManager.Core.Updates;
using ClipboardManager.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Windows.Graphics;

namespace ClipboardManager.App;

public sealed partial class MainWindow : Window
{
    private const double CompactNavigationWidth = 900;
    private const double HideDetailsWidth = 760;
    private readonly AppWindow _appWindow;
    private readonly OverlappedPresenter? _presenter;
    private readonly Func<HistoryViewModel> _createHistoryViewModel;
    private readonly Func<SettingsViewModel> _createSettingsViewModel;
    private readonly UpdateCoordinator _updateCoordinator;
    private readonly Func<UpdatesViewModel> _createUpdatesViewModel;
    private readonly Action _hideAfterCopy;
    private readonly Func<bool> _isTrayEnabled;
    private HistoryViewModel? _activeHistoryViewModel;
    private AppSettings _settings = new(500, 512, false, true, false, true, "System");
    private bool _exitRequested;
    private readonly TaskCompletionSource _loadedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private XamlRoot? _observedXamlRoot;
    private double _windowWidth = 1000;
    private double _lastRasterizationScale = 1;

    public MainWindow(Func<HistoryViewModel> createHistoryViewModel, Func<SettingsViewModel> createSettingsViewModel, Action hideAfterCopy, Func<bool> isTrayEnabled, UpdateCoordinator updateCoordinator, Func<UpdatesViewModel> createUpdatesViewModel)
    {
        _createHistoryViewModel = createHistoryViewModel ?? throw new ArgumentNullException(nameof(createHistoryViewModel));
        _createSettingsViewModel = createSettingsViewModel ?? throw new ArgumentNullException(nameof(createSettingsViewModel));
        _hideAfterCopy = hideAfterCopy ?? throw new ArgumentNullException(nameof(hideAfterCopy));
        _isTrayEnabled = isTrayEnabled ?? throw new ArgumentNullException(nameof(isTrayEnabled));
        _updateCoordinator = updateCoordinator ?? throw new ArgumentNullException(nameof(updateCoordinator));
        _createUpdatesViewModel = createUpdatesViewModel ?? throw new ArgumentNullException(nameof(createUpdatesViewModel));
        StartupExceptionRecorder? startupExceptionRecorder =
            Environment.GetEnvironmentVariable("CLIPBOARDMANAGER_STARTUP_DIAGNOSTICS") == "1"
                ? new StartupExceptionRecorder()
                : null;
        try { InitializeComponent(); }
        catch
        {
            startupExceptionRecorder?.WriteToStartupLog();
            throw;
        }
        finally { startupExceptionRecorder?.Dispose(); }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        IntPtr handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(handle));
        _appWindow.Title = "剪贴板管理器";
        _presenter = _appWindow.Presenter as OverlappedPresenter;
        double initialScale = GetDpiForWindow(handle) / 96d;
        _appWindow.ResizeClient(new SizeInt32(
            checked((int)Math.Round(1000 * initialScale)), checked((int)Math.Round(720 * initialScale))));
        UpdateDpiSensitiveLayout(initialScale);
        RootGrid.Loaded += OnRootLoaded;
        _appWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
        SizeChanged += OnWindowSizeChanged;
        _updateCoordinator.PropertyChanged += OnUpdateCoordinatorPropertyChanged;
        UpdateUpdateNotice();
        RootNavigation.SelectedItem = HistoryNavigationItem;
        NavigateTo("history");
        UpdateResponsiveLayout(_windowWidth);
    }

    public async Task<bool> ShowStartupFailureDialogAsync(string logPath)
    {
        await WaitForRootAsync();
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "数据初始化或安全检查未完成，因此剪贴板采集尚未启动。修复问题后可以重试。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = $"诊断日志：{logPath}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "应用没有完成启动",
            Content = content,
            PrimaryButtonText = "重试",
            CloseButtonText = "退出",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task ShowUnhandledErrorDialogAsync(string? logPath)
    {
        await WaitForRootAsync();
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "应用遇到问题",
            Content = $"为保护数据，详细诊断只写入本机日志。\n日志：{logPath ?? "不可用"}",
            CloseButtonText = "关闭"
        };
        await dialog.ShowAsync();
    }

    public Task WaitUntilLoadedAsync() => _loadedCompletion.Task;

    private Task WaitForRootAsync()
    {
        if (RootGrid.XamlRoot is not null) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? loaded = null;
        loaded = (_, _) => { RootGrid.Loaded -= loaded; completion.TrySetResult(); };
        RootGrid.Loaded += loaded;
        return completion.Task;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        _loadedCompletion.TrySetResult();
        XamlRoot? current = RootGrid.XamlRoot;
        if (current is null || ReferenceEquals(current, _observedXamlRoot)) return;
        if (_observedXamlRoot is not null) _observedXamlRoot.Changed -= OnXamlRootChanged;
        _observedXamlRoot = current;
        _observedXamlRoot.Changed += OnXamlRootChanged;
        UpdateDpiSensitiveLayout(current.RasterizationScale);
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        double scale = sender.RasterizationScale;
        if (Math.Abs(scale - _lastRasterizationScale) >= 0.001) UpdateDpiSensitiveLayout(scale);
        else UpdateTitleBarInsets(scale);
    }

    private void UpdateDpiSensitiveLayout(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        _lastRasterizationScale = scale;
        if (_presenter is not null)
        {
            _presenter.PreferredMinimumWidth = checked((int)Math.Round(720 * scale));
            _presenter.PreferredMinimumHeight = checked((int)Math.Round(520 * scale));
        }
        UpdateTitleBarInsets(scale);
    }

    private void UpdateTitleBarInsets(double scale)
    {
        LeftTitleBarInset.Width = new GridLength(_appWindow.TitleBar.LeftInset / scale);
        RightTitleBarInset.Width = new GridLength(_appWindow.TitleBar.RightInset / scale);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _loadedCompletion.TrySetCanceled();
        _appWindow.Closing -= OnAppWindowClosing;
        if (_activeHistoryViewModel is not null) _activeHistoryViewModel.CopyCompleted -= OnHistoryCopyCompleted;
        _updateCoordinator.PropertyChanged -= OnUpdateCoordinatorPropertyChanged;
        RootGrid.Loaded -= OnRootLoaded;
        SizeChanged -= OnWindowSizeChanged;
        if (_observedXamlRoot is not null) _observedXamlRoot.Changed -= OnXamlRootChanged;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string route) NavigateTo(route);
    }

    private void NavigateTo(string route)
    {
        if (_activeHistoryViewModel is not null)
        {
            _activeHistoryViewModel.CopyCompleted -= OnHistoryCopyCompleted;
            _activeHistoryViewModel = null;
        }

        (string title, string description, string emptyTitle, string emptyDescription, string hint) = route switch
        {
            "favorites" => ("收藏", "把常用内容收好，需要时快速找回。", "收藏内容会出现在这里", "在历史记录中点选收藏，即可建立自己的常用内容集。", "你收藏的记录会一直保留，直到你主动取消收藏或删除。"),
            "updates" => ("更新中心", "查看版本、检查更新，并清楚了解下载与验证进度。", "更新状态一目了然", "可用版本、更新说明和安装进度都会集中显示在这里。", "自动检查只提醒有新版本，不会替你下载或安装。"),
            "settings" => ("设置", "按你的使用习惯调整外观、容量和复制行为。", "设置集中管理", "主题、历史容量、托盘和复制后的行为将在这里调整。", "设置保存在本机；历史记录和图片不会随更新被覆盖。"),
            _ => ("历史", "最近复制的文本和图片，清晰查看、快速搜索。", "复制内容后，历史会显示在这里", "复制文本或图片即可开始；你也可以将重要内容加入收藏。", "剪贴板记录仅保存在本机，应用不会把它们上传到更新服务。")
        };
        CurrentSectionTitle.Text = title;
        if (route is "history" or "favorites")
        {
            _activeHistoryViewModel = _createHistoryViewModel();
            _activeHistoryViewModel.CopyCompleted += OnHistoryCopyCompleted;
            ContentFrame.Content = new HistoryPage(_activeHistoryViewModel, route == "favorites");
        }
        else if (route == "settings")
        {
            ContentFrame.Content = new SettingsPage(_createSettingsViewModel());
        }
        else if (route == "updates")
        {
            ContentFrame.Content = new UpdatesPage(_createUpdatesViewModel());
        }
        else
        {
            ContentFrame.Navigate(typeof(ShellPage), new ShellPageState(title, description, emptyTitle, emptyDescription, hint));
        }
        UpdateResponsiveLayout(_windowWidth);
    }

    private void OnUpdateCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(UpdateCoordinator.State) or nameof(UpdateCoordinator.CurrentUpdate))
            UpdateUpdateNotice();
    }

    private void UpdateUpdateNotice()
    {
        bool available = _updateCoordinator.CurrentUpdate?.IsUpdateAvailable == true &&
            _updateCoordinator.State is (UpdateState.Available or UpdateState.Cancelled or UpdateState.Error);
        UpdateNoticeButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUpdateNoticeClicked(object sender, RoutedEventArgs args)
        => RootNavigation.SelectedItem = UpdatesNavigationItem;

    private void OnHistoryCopyCompleted(object? sender, EventArgs args) => _hideAfterCopy();

    public void RefreshCurrentHistory()
    {
        if (ContentFrame.Content is HistoryPage) _ = _activeHistoryViewModel?.RefreshAsync();
    }

    public void ApplyWindowSettings(AppSettings settings)
    {
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();
        if (_presenter is not null) _presenter.IsAlwaysOnTop = _settings.AlwaysOnTop;
    }

    public void HideWindow() => ShowWindow(WindowNative.GetWindowHandle(this), 0);
    public void ShowWindow() => ShowWindow(WindowNative.GetWindowHandle(this), 9);

    public void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_settings.MinimizeToTray && _isTrayEnabled() && !_exitRequested)
        {
            args.Cancel = true;
            HideWindow();
        }
    }

    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        UpdateResponsiveLayout(args.Size.Width);
        UpdateTitleBarInsets(_lastRasterizationScale);
    }

    private void UpdateResponsiveLayout(double width)
    {
        _windowWidth = width;
        RootNavigation.PaneDisplayMode = width < CompactNavigationWidth
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;
        if (ContentFrame.Content is ShellPage page) page.UpdateResponsiveLayout(width, HideDetailsWidth);
        if (ContentFrame.Content is SettingsPage settingsPage) settingsPage.UpdateResponsiveLayout(width);
        if (ContentFrame.Content is UpdatesPage updatesPage) updatesPage.UpdateResponsiveLayout(width, HideDetailsWidth);
        if (ContentFrame.Content is HistoryPage historyPage) historyPage.UpdateResponsiveLayout(width, HideDetailsWidth);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);
}

public sealed record ShellPageState(string Title, string Description, string EmptyTitle, string EmptyDescription, string Hint);

