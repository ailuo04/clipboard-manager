using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Window _owner;
    private readonly HistoryService _history;
    private readonly Action<bool>? _trayModeChanged;
    private readonly WindowOverlayHost? _overlays;
    private readonly ClipboardReader _clipboard = new();
    private readonly ClipboardMonitor _monitor;
    private readonly ClipboardCaptureCoordinator _capture;
    private readonly UpdateCoordinator _updateCoordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly DispatcherTimer _searchTimer;
    private List<RecordViewModel> _allRecords = [];
    private Task _initializationTask = Task.CompletedTask;
    private bool _isLoading = true;
    private bool _isReady;
    private bool _isBusy;
    private bool _isPaused;
    private bool _isClosing;
    private bool _isCompact = true;
    private bool _isWidgetCollapsed = true;
    private RecordViewModel? _selectedRecord;
    private int _filterIndex;
    private string _searchTerm = string.Empty;
    private string _notice = string.Empty;
    private string _status = "正在加载历史记录…";

    public MainViewModel(Window owner, HistoryService history, Action<bool>? trayModeChanged = null)
        : this(owner, history, trayModeChanged, null) { }

    internal MainViewModel(Window owner, HistoryService history, Action<bool>? trayModeChanged, WindowOverlayHost? overlays)
    {
        _owner = owner;
        _history = history;
        _trayModeChanged = trayModeChanged;
        _overlays = overlays;
        _monitor = new ClipboardMonitor(owner);
        _capture = new ClipboardCaptureCoordinator(_clipboard, _history,
            () => IsReady && !IsPaused && !IsClosing, () => IsPaused, () => IsClosing,
            _lifetime, RunOperationAsync, RefreshRecords, value => Notice = value ?? string.Empty,
            value => Status = value);
        _capture.Attach(_monitor);
        _updateCoordinator = new UpdateCoordinator(owner, history, overlays, _lifetime,
            () => IsClosing, () => Notice, value => Notice = value, Report);
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _searchTimer.Tick += OnSearchTimer;
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => CanAct && _allRecords.Count > 0);
        SettingsCommand = new AsyncRelayCommand(ShowSettingsAsync, () => CanAct);
        UpdateCommand = new AsyncRelayCommand(ShowUpdatesAsync, () => CanAct);
        ToggleAlwaysOnTopCommand = new AsyncRelayCommand(ToggleAlwaysOnTopAsync, () => CanAct);
        RetryCommand = new AsyncRelayCommand(InitializeAsync, () => !IsLoading && !IsClosing);
        PauseCommand = new RelayCommand(() => { IsPaused = !IsPaused; _capture.Pause(); }, () => IsReady && !IsClosing);
        DismissNoticeCommand = new RelayCommand(() => Notice = string.Empty);
        ClearSearchCommand = new RelayCommand(() => SearchTerm = string.Empty);
    }

    public ObservableCollection<RecordViewModel> VisibleRecords { get; } = [];
    public ICommand ClearCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand ToggleAlwaysOnTopCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand DismissNoticeCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public bool CanAct => IsReady && !IsBusy && !IsClosing;
    public bool IsCompact => _isCompact;
    /// <summary>The widget is always topmost; this setting keeps the expanded panel open when it loses focus.</summary>
    public bool IsAlwaysOnTop => _history.Settings.AlwaysOnTop;
    public bool HideAfterCopy => _history.Settings.HideAfterCopy;
    public string AlwaysOnTopToolTip => IsAlwaysOnTop ? "取消保持展开" : "保持展开（失去焦点时不收起）";
    public string AlwaysOnTopLabel => IsAlwaysOnTop ? "已保持展开" : "保持展开";
    public bool IsWidgetCollapsed
    {
        get => _isWidgetCollapsed;
        set { if (Set(ref _isWidgetCollapsed, value)) Raise(nameof(WidgetToggleLabel)); }
    }
    public string WidgetToggleLabel => IsWidgetCollapsed ? "展开" : "收起";
    public int RecordCount => _allRecords.Count;
    public IReadOnlyList<RecordViewModel> HistoryRecords => _allRecords;
    public RecordViewModel? LatestRecord => _allRecords.FirstOrDefault();
    public bool HasRecords => _allRecords.Count > 0;
    public string CapsuleText => !IsReady ? Status
        : _history.Settings.HideCapsulePreview || _allRecords.Count == 0 ? "剪贴板"
        : _allRecords[0].SingleLine;
    public Point? WidgetPosition
    {
        get
        {
            var settings = _history.Settings;
            return settings.WidgetLeft is double left && settings.WidgetTop is double top ? new Point(left, top) : null;
        }
    }
    public double ThumbnailHeight => IsCompact ? 104 : 196;
    public RecordViewModel? SelectedRecord { get => _selectedRecord; set => Set(ref _selectedRecord, value); }

    public void UpdateWindowSize()
    {
        bool compact = _owner.ActualWidth < 600 || _owner.ActualHeight < 620;
        if (Set(ref _isCompact, compact, nameof(IsCompact))) Raise(nameof(ThumbnailHeight));
    }
    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) RaiseState(); } }
    public bool IsReady { get => _isReady; private set { if (Set(ref _isReady, value)) { RaiseState(); Raise(nameof(CapsuleText)); } } }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RaiseState(); } }
    public bool IsClosing { get => _isClosing; private set { if (Set(ref _isClosing, value)) RaiseState(); } }
    public bool IsPaused
    {
        get => _isPaused;
        private set { if (Set(ref _isPaused, value)) { Raise(nameof(MonitorStatus)); Raise(nameof(PauseToolTip)); } }
    }
    public string PauseToolTip => IsPaused ? "恢复监听" : "暂停监听";
    public string MonitorStatus => IsClosing ? "正在关闭" : !IsReady ? "监听未启动" : IsPaused ? "监听已暂停" : "正在监听";
    public string Notice { get => _notice; private set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(CapsuleText)); } }
    public string SearchTerm
    {
        get => _searchTerm;
        set { if (Set(ref _searchTerm, value)) { _searchTimer.Stop(); _searchTimer.Start(); Raise(nameof(HasSearch)); Raise(nameof(SearchHidesImages)); } }
    }
    public bool HasSearch => !string.IsNullOrEmpty(SearchTerm);

    public bool SearchHidesImages => HasSearch && FilterIndex != 2 && _allRecords.Any(item => item.IsImage);
    public int FilterIndex
    {
        get => _filterIndex;
        set { if (Set(ref _filterIndex, Math.Clamp(value, 0, 2))) ApplyFilter(); }
    }
    public string AllLabel => $"全部  {_allRecords.Count}";
    public string TextLabel => $"文本  {_allRecords.Count(item => !item.IsImage)}";
    public string ImageLabel => $"图片  {_allRecords.Count(item => item.IsImage)}";
    public string CountLabel => $"{VisibleRecords.Count} / {_allRecords.Count} 条 · {Formatters.FormatSize(_allRecords.Sum(item => item.Record.ByteLength))}";
    public bool IsEmpty => !IsLoading && VisibleRecords.Count == 0;
    public bool CanRetry => !IsLoading && !IsReady;
    public string EmptyTitle => !IsReady ? "历史记录加载失败" : _allRecords.Count == 0 ? "暂无剪贴板记录" : "没有匹配的记录";
    public string EmptySubtitle => !IsReady ? "请检查数据目录权限或日志" : _allRecords.Count == 0 ? "文本与图片历史将在这里显示" : "当前搜索或筛选结果为空";

    public Task InitializeAsync() => _initializationTask = InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (IsClosing) return;
        IsLoading = true;
        Notice = string.Empty;
        try
        {
            var warning = await Task.Run(() => _history.LoadAsync(_lifetime.Token));
            if (IsClosing) return;
            RefreshRecords();
            ApplyWindowSettings();
            _monitor.Start();
            ApplyTrayMode(_history.Settings.MinimizeToTray);
            IsReady = true;
            _updateCoordinator.StartAfterLoad();
            Status = "历史记录已加载";
            if (warning is not null) Notice = warning;
        }
        catch (OperationCanceledException) when (IsClosing) { }
        catch (Exception exception)
        {
            IsReady = false;
            Report("加载历史记录失败", exception);
        }
        finally { IsLoading = false; }
    }

    public string GetImagePath(ClipboardRecord record) => _history.GetImagePath(record);

    public Task CopyAsync(RecordViewModel item) => RunOperationAsync(async () =>
    {
        if (item.IsImage) await _clipboard.WriteImageAsync(item.ImagePath!, _monitor.Handle, _lifetime.Token);
        else await _clipboard.WriteTextAsync(item.Record.Text!, _monitor.Handle, _lifetime.Token);
        Status = item.IsImage ? "图片已复制到剪贴板" : "完整文本已复制到剪贴板";
        CopyCompleted?.Invoke(this, EventArgs.Empty);
    }, "复制记录失败");

    public event EventHandler? CopyCompleted;

    public event EventHandler? SettingsApplied;

    public Task SaveWidgetPositionAsync(double left, double top)
    {
        if (!IsReady || IsClosing) return Task.CompletedTask;
        return RunOperationAsync(async () =>
        {
            var settings = _history.Settings;
            if (settings.WidgetLeft == left && settings.WidgetTop == top) return;
            settings.WidgetLeft = left;
            settings.WidgetTop = top;
            await _history.UpdateSettingsAsync(settings, _lifetime.Token);
        }, "保存小组件位置失败");
    }

    public Task DeleteAsync(RecordViewModel item) => RunOperationAsync(async () =>
    {
        var warning = await _history.DeleteAsync(item.Record.Id, _lifetime.Token);
        RefreshRecords();
        Status = "记录已删除";
        if (warning is not null) Notice = warning;
    }, "删除记录失败");

    public Task TogglePinAsync(RecordViewModel item) => RunOperationAsync(async () =>
    {
        var warning = await _history.TogglePinAsync(item.Record.Id, _lifetime.Token);
        RefreshRecords();
        Status = item.IsPinned ? "已取消固定" : "记录已固定";
        if (warning is not null) Notice = warning;
    }, "更新固定状态失败");

    public async Task ShowDetails(RecordViewModel item)
    {
        try
        {
            if (_overlays is null || IsClosing) return;
            var details = new DetailsOverlay(item, _clipboard, _monitor.Handle);
            await _overlays.ShowAsync(details, OverlaySize.Details);
        }
        catch (Exception exception) { Report("打开详情失败", exception); }
    }

    public async Task ShutdownAsync()
    {
        if (IsClosing) return;
        IsClosing = true;
        _searchTimer.Stop();
        _searchTimer.Tick -= OnSearchTimer;
        Task captureTask = _capture.ShutdownAsync();
        Task updateTask = _updateCoordinator.ShutdownAsync();
        _monitor.Dispose();
        _lifetime.Cancel();
        _overlays?.CloseAll(null);
        await _initializationTask;
        await captureTask;
        await updateTask;
        await _actionGate.WaitAsync();
        _actionGate.Release();
        await _history.FlushAsync();
        await _clipboard.DisposeAsync();
        _lifetime.Dispose();
    }

    private Task ClearAsync() => RunOperationAsync(async () =>
    {
        if (_overlays is null || IsClosing) return;
        int pinned = _allRecords.Count(item => item.IsPinned);
        var confirmation = new ClearConfirmationOverlay(_allRecords.Count, pinned);
        if (await _overlays.ShowAsync(confirmation, OverlaySize.ClearConfirmation) != true) return;

        var warning = await _history.ClearAsync(_lifetime.Token);
        RefreshRecords();
        Status = "历史记录已清空";
        if (warning is not null) Notice = warning;
    }, "清空历史记录失败");
    private async Task ShowSettingsAsync()
    {
        try
        {
            if (_overlays is null) return;
            var dialog = new SettingsOverlay(_history.Settings, _history.RootDirectory, ShowUpdatesAsync);
            if (await _overlays.ShowAsync(dialog, OverlaySize.Settings) != true) return;
            await RunOperationAsync(async () =>
            {
                string? warning = await _history.UpdateSettingsAsync(dialog.Settings, _lifetime.Token);
                ThemeManager.SetPreference(dialog.SelectedTheme);
                ApplyWindowSettings();
                ApplyTrayMode(_history.Settings.MinimizeToTray);
                _updateCoordinator.ResetAutoCheckDelay();
                RefreshRecords();
                Status = "设置已保存";
                if (warning is not null) Notice = warning;
                SettingsApplied?.Invoke(this, EventArgs.Empty);
            }, "保存设置失败");
        }
        catch (Exception exception) { Report("打开设置失败", exception); }
    }

    private Task ShowUpdatesAsync() => _updateCoordinator.ShowUpdatesAsync();

    private async Task ToggleAlwaysOnTopAsync()
    {
        await RunOperationAsync(async () =>
        {
            var settings = _history.Settings;
            settings.AlwaysOnTop = !settings.AlwaysOnTop;
            string? warning = await _history.UpdateSettingsAsync(settings, _lifetime.Token);
            ApplyWindowSettings();
            Status = IsAlwaysOnTop ? "已保持展开" : "失去焦点时自动收起";
            if (warning is not null) Notice = warning;
        }, "切换保持展开失败");
        // Reset the toggle to the committed value even when persistence fails.
        Raise(nameof(IsAlwaysOnTop));
    }

    private void ApplyWindowSettings()
    {
        Raise(nameof(IsAlwaysOnTop));
        Raise(nameof(CapsuleText));
        Raise(nameof(AlwaysOnTopToolTip));
        Raise(nameof(AlwaysOnTopLabel));
    }

    private void ApplyTrayMode(bool enabled)
    {
        try { _trayModeChanged?.Invoke(enabled); }
        catch (Exception exception)
        {
            AppLogger.LogException("更新系统托盘状态失败", exception);
            Notice = "系统托盘不可用；关闭窗口时程序将退出。";
        }
    }

    private void OnSearchTimer(object? sender, EventArgs args) { _searchTimer.Stop(); ApplyFilter(); }

    private async Task RunOperationAsync(Func<Task> action, string context)
    {
        bool entered = false;
        try
        {
            await _actionGate.WaitAsync(_lifetime.Token);
            entered = true;
            IsBusy = true;
            await action();
        }
        catch (OperationCanceledException) when (IsClosing) { }
        catch (Exception exception) { Report(context, exception); }
        finally
        {
            if (entered) { IsBusy = false; _actionGate.Release(); }
        }
    }

    private void RefreshRecords()
    {
        _allRecords = _history.Records.OrderByDescending(record => record.CopiedAtUtc).Select(record => new RecordViewModel(record, this)).ToList();
        Raise(nameof(AllLabel)); Raise(nameof(TextLabel)); Raise(nameof(ImageLabel));
        Raise(nameof(RecordCount)); Raise(nameof(HistoryRecords)); Raise(nameof(LatestRecord)); Raise(nameof(HasRecords)); Raise(nameof(CapsuleText));
        ApplyFilter();
        RaiseState();
    }

    private void ApplyFilter()
    {
        Guid? selectedId = SelectedRecord?.Record.Id;
        int selectedIndex = SelectedRecord is null ? -1 : VisibleRecords.IndexOf(SelectedRecord);
        string query = SearchTerm.Trim();
        var filtered = _allRecords.Where(item => (FilterIndex == 0 || FilterIndex == 1 && !item.IsImage || FilterIndex == 2 && item.IsImage)
            && (query.Length == 0 || !item.IsImage && item.Record.Text!.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
        VisibleRecords.Clear();
        for (int index = 0; index < filtered.Count; index++)
        {
            filtered[index].Position = index + 1;
            VisibleRecords.Add(filtered[index]);
        }
        SelectedRecord = filtered.FirstOrDefault(item => item.Record.Id == selectedId)
            ?? (selectedIndex < 0 || filtered.Count == 0 ? null : filtered[Math.Min(selectedIndex, filtered.Count - 1)]);
        Raise(nameof(CountLabel)); Raise(nameof(IsEmpty)); Raise(nameof(EmptyTitle)); Raise(nameof(EmptySubtitle));
        Raise(nameof(SearchHidesImages));
    }

    private void RaiseState()
    {
        Raise(nameof(CanAct)); Raise(nameof(IsEmpty)); Raise(nameof(CanRetry)); Raise(nameof(MonitorStatus));
        Raise(nameof(EmptyTitle)); Raise(nameof(EmptySubtitle));
        (ClearCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ToggleAlwaysOnTopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        foreach (var item in _allRecords) item.RefreshCommands();
    }

    public void ReportHotKeyFailure(Exception exception)
    {
        string previousNotice = Notice;
        Report("全局快捷键 Ctrl+Shift+Space 注册失败，可能已被其他程序占用；仍可点击胶囊展开", exception);
        if (!string.IsNullOrEmpty(previousNotice)) Notice = previousNotice + " " + Notice;
    }

    public void ReportTrayFailure(Exception exception) => Report("系统托盘不可用", exception);

    private void Report(string context, Exception exception)
    {
        AppLogger.LogException(context, exception);
        Notice = $"{context}：{exception.Message} {exception.InnerException?.Message}";
        Status = "操作未完成，详细信息已写入日志";
    }
}
