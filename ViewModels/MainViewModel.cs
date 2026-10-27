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
    private readonly ClipboardReader _clipboard = new();
    private readonly ClipboardMonitor _monitor;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly DispatcherTimer _captureTimer;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _updateTimer;
    private readonly UpdateService _updates = new();
    private Task _updateTask = Task.CompletedTask;
    private Task _updateCleanupTask = Task.CompletedTask;
    private List<RecordViewModel> _allRecords = [];
    private Task _captureTask = Task.CompletedTask;
    private Task _initializationTask = Task.CompletedTask;
    private bool _captureRunning;
    private bool _captureRequested;
    private uint? _lastSequence;
    private bool _isLoading = true;
    private bool _isReady;
    private bool _isBusy;
    private bool _isPaused;
    private bool _isClosing;
    private bool _isCompact = true;
    private RecordViewModel? _selectedRecord;
    private int _filterIndex;
    private string _searchTerm = string.Empty;
    private string _notice = string.Empty;
    private string _status = "正在加载历史记录…";

    public MainViewModel(Window owner, HistoryService history, Action<bool>? trayModeChanged = null)
    {
        _owner = owner;
        _history = history;
        _trayModeChanged = trayModeChanged;
        _monitor = new ClipboardMonitor(owner);
        _monitor.ClipboardChanged += OnClipboardChanged;
        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _captureTimer.Tick += OnCaptureTimer;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _searchTimer.Tick += OnSearchTimer;
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _updateTimer.Tick += OnUpdateTimer;
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => CanAct && _allRecords.Count > 0);
        SettingsCommand = new AsyncRelayCommand(ShowSettingsAsync, () => CanAct);
        UpdateCommand = new RelayCommand(() => ShowUpdates(_owner), () => CanAct);
        ToggleAlwaysOnTopCommand = new AsyncRelayCommand(ToggleAlwaysOnTopAsync, () => CanAct);
        RetryCommand = new AsyncRelayCommand(InitializeAsync, () => !IsLoading && !IsClosing);
        PauseCommand = new RelayCommand(() =>
        {
            IsPaused = !IsPaused;
            _captureTimer.Stop();
            _captureRequested = false;
        }, () => IsReady && !IsClosing);
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
    public bool IsAlwaysOnTop => _owner.Topmost;
    public string AlwaysOnTopToolTip => IsAlwaysOnTop ? "取消窗口置顶" : "窗口置顶";
    public string AlwaysOnTopLabel => IsAlwaysOnTop ? "已置顶" : "置顶";
    public double ThumbnailHeight => IsCompact ? 104 : 196;
    public RecordViewModel? SelectedRecord { get => _selectedRecord; set => Set(ref _selectedRecord, value); }

    public void UpdateWindowSize()
    {
        bool compact = _owner.ActualWidth < 600 || _owner.ActualHeight < 620;
        if (Set(ref _isCompact, compact, nameof(IsCompact))) Raise(nameof(ThumbnailHeight));
    }
    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) RaiseState(); } }
    public bool IsReady { get => _isReady; private set { if (Set(ref _isReady, value)) RaiseState(); } }
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
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string SearchTerm
    {
        get => _searchTerm;
        set { if (Set(ref _searchTerm, value)) { _searchTimer.Stop(); _searchTimer.Start(); Raise(nameof(HasSearch)); } }
    }
    public bool HasSearch => !string.IsNullOrEmpty(SearchTerm);
    public int FilterIndex
    {
        get => _filterIndex;
        set { if (Set(ref _filterIndex, Math.Clamp(value, 0, 2))) ApplyFilter(); }
    }
    public string AllLabel => $"全部  {_allRecords.Count}";
    public string TextLabel => $"文本  {_allRecords.Count(item => !item.IsImage)}";
    public string ImageLabel => $"图片  {_allRecords.Count(item => item.IsImage)}";
    public string CountLabel => $"{VisibleRecords.Count} / {_allRecords.Count} 条 · {RecordViewModel.FormatSize(_allRecords.Sum(item => item.Record.ByteLength))}";
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
            try { _updates.MarkStartupReady(); }
            catch (Exception exception) { AppLogger.LogException("标记更新启动状态失败", exception); }
            _updateCleanupTask = CleanUpdateFilesAsync();
            _updateTimer.Start();
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
    }, "复制记录失败");

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

    public void ShowDetails(RecordViewModel item)
    {
        try { new DetailsWindow(_owner, item, _clipboard, _monitor.Handle).ShowDialog(); }
        catch (Exception exception) { Report("打开详情失败", exception); }
    }

    public async Task ShutdownAsync()
    {
        if (IsClosing) return;
        IsClosing = true;
        _captureTimer.Stop();
        _searchTimer.Stop();
        _updateTimer.Stop();
        _updateTimer.Tick -= OnUpdateTimer;
        _captureTimer.Tick -= OnCaptureTimer;
        _searchTimer.Tick -= OnSearchTimer;
        _monitor.ClipboardChanged -= OnClipboardChanged;
        _monitor.Dispose();
        _lifetime.Cancel();
        await _initializationTask;
        await _captureTask;
        await _updateTask;
        await _updateCleanupTask;
        await _actionGate.WaitAsync();
        _actionGate.Release();
        await _history.FlushAsync();
        await _clipboard.DisposeAsync();
        _updates.Dispose();
        _lifetime.Dispose();
    }

    private async Task ClearAsync()
    {
        int pinned = _allRecords.Count(item => item.IsPinned);
        if (MessageBox.Show(_owner, $"确定清空全部 {_allRecords.Count} 条记录吗？\n包括 {pinned} 条固定记录，保存的图片也会删除。此操作无法撤销。",
            "清空历史记录", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunOperationAsync(async () =>
        {
            var warning = await _history.ClearAsync(_lifetime.Token);
            RefreshRecords();
            Status = "历史记录已清空";
            if (warning is not null) Notice = warning;
        }, "清空历史记录失败");
    }

    private async Task ShowSettingsAsync()
    {
        try
        {
            SettingsWindow? dialog = null;
            dialog = new SettingsWindow(_owner, _history.Settings, _history.RootDirectory, () => ShowUpdates(dialog!));
            if (dialog.ShowDialog() != true) return;
            await RunOperationAsync(async () =>
            {
                string? warning = await _history.UpdateSettingsAsync(dialog.Settings, _lifetime.Token);
                ThemeManager.SetPreference(dialog.SelectedTheme);
                ApplyWindowSettings();
                ApplyTrayMode(_history.Settings.MinimizeToTray);
                _updateTimer.Interval = TimeSpan.FromSeconds(5);
                RefreshRecords();
                Status = "设置已保存";
                if (warning is not null) Notice = warning;
            }, "保存设置失败");
        }
        catch (Exception exception) { Report("打开设置失败", exception); }
    }

    private void ShowUpdates(Window owner)
    {
        try
        {
            if (new UpdateWindow(owner, _updates).ShowDialog() != true || IsClosing) return;
            _updates.LaunchPrepared();
            foreach (Window dialog in _owner.OwnedWindows.Cast<Window>().ToArray()) dialog.Close();
            if (_owner is MainWindow mainWindow) mainWindow.ExitApplication();
            else _owner.Close();
        }
        catch (Exception exception)
        {
            try { _updates.CancelPrepared(); }
            catch (Exception cleanup) { AppLogger.LogException("清理未启动的更新失败", cleanup); }
            Report("启动更新失败", exception);
        }
    }

    private void OnUpdateTimer(object? sender, EventArgs args)
    {
        _updateTimer.Interval = TimeSpan.FromHours(6);
        if (IsClosing || !_history.Settings.AutoCheckUpdates || !_updateTask.IsCompleted) return;
        _updateTask = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updates.CheckAsync(_lifetime.Token);
            if (update is null || IsClosing) return;
            string message = $"发现新版本 {update.Manifest.Version} · 下载 {UpdateService.FormatSize(update.DownloadSize)}";
            Notice = string.IsNullOrEmpty(Notice) ? message : Notice.Contains(message, StringComparison.Ordinal) ? Notice : Notice + "\n" + message;
        }
        catch (OperationCanceledException) when (IsClosing) { }
        catch (Exception exception) { AppLogger.LogException("自动检查更新失败", exception); }
    }

    private async Task CleanUpdateFilesAsync()
    {
        try { await _updates.CleanFinishedAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (IsClosing) { }
        catch (Exception exception) { AppLogger.LogException("清理更新临时文件失败", exception); }
    }

    private async Task ToggleAlwaysOnTopAsync()
    {
        await RunOperationAsync(async () =>
        {
            var settings = _history.Settings;
            settings.AlwaysOnTop = !settings.AlwaysOnTop;
            string? warning = await _history.UpdateSettingsAsync(settings, _lifetime.Token);
            ApplyWindowSettings();
            Status = IsAlwaysOnTop ? "窗口已置顶" : "已取消窗口置顶";
            if (warning is not null) Notice = warning;
        }, "切换窗口置顶失败");
        // Reset the toggle to the committed value even when persistence fails.
        Raise(nameof(IsAlwaysOnTop));
    }

    private void ApplyWindowSettings()
    {
        _owner.Topmost = _history.Settings.AlwaysOnTop;
        Raise(nameof(IsAlwaysOnTop));
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

    private void OnClipboardChanged(object? sender, EventArgs args) => ScheduleCapture();
    private void ScheduleCapture()
    {
        if (!IsReady || IsPaused || IsClosing) return;
        _captureTimer.Stop();
        _captureTimer.Start();
    }
    private void OnSearchTimer(object? sender, EventArgs args) { _searchTimer.Stop(); ApplyFilter(); }
    private void OnCaptureTimer(object? sender, EventArgs args)
    {
        _captureTimer.Stop();
        if (IsPaused || IsClosing) return;
        _captureRequested = true;
        if (!_captureRunning) _captureTask = CaptureLoopAsync();
    }

    private async Task CaptureLoopAsync()
    {
        _captureRunning = true;
        try
        {
            while (_captureRequested && !IsClosing && !IsPaused)
            {
                _captureRequested = false;
                await RunOperationAsync(async () =>
                {
                    var result = await _clipboard.ReadAsync(_lifetime.Token);
                    if (IsPaused || IsClosing) return;
                    if (result.Warning is not null) Notice = result.Warning;
                    if (_lastSequence == result.Sequence) return;
                    _lastSequence = result.Sequence;
                    if (result.Snapshot is null) return;
                    var saved = await _history.AddAsync(result.Snapshot, _lifetime.Token);
                    RefreshRecords();
                    Status = result.Snapshot.Kind == ClipboardRecordKind.Image ? "已保存图片" : "已保存文本";
                    if (saved.Warning is not null) Notice = saved.Warning;
                }, "读取或保存剪贴板失败");
            }
        }
        finally { _captureRunning = false; }
    }

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
        foreach (var item in filtered) VisibleRecords.Add(item);
        SelectedRecord = filtered.FirstOrDefault(item => item.Record.Id == selectedId)
            ?? (selectedIndex < 0 || filtered.Count == 0 ? null : filtered[Math.Min(selectedIndex, filtered.Count - 1)]);
        Raise(nameof(CountLabel)); Raise(nameof(IsEmpty)); Raise(nameof(EmptyTitle)); Raise(nameof(EmptySubtitle));
    }

    private void RaiseState()
    {
        Raise(nameof(CanAct)); Raise(nameof(IsEmpty)); Raise(nameof(CanRetry)); Raise(nameof(MonitorStatus));
        Raise(nameof(EmptyTitle)); Raise(nameof(EmptySubtitle));
        (ClearCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleAlwaysOnTopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        foreach (var item in _allRecords) item.RefreshCommands();
    }

    public void ReportHotKeyFailure(Exception exception)
    {
        string previousNotice = Notice;
        Report("全局快捷键 Ctrl+Shift+Space 注册失败，可能已被其他程序占用；仍可从任务栏打开窗口", exception);
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
