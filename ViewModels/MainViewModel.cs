using System.Windows;
using System.Windows.Input;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly HistoryService _history;
    private readonly Action<bool>? _trayModeChanged;
    private readonly WindowOverlayHost? _overlays;
    private readonly ClipboardReader _clipboard = new();
    private readonly ClipboardMonitor _monitor;
    private readonly ClipboardCaptureCoordinator _capture;
    private readonly UpdateCoordinator _updateCoordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private List<RecordViewModel> _allRecords = [];
    private Task _initializationTask = Task.CompletedTask;
    private bool _isReady;
    private bool _isBusy;
    private bool _isPaused;
    private bool _isClosing;
    private string _notice = string.Empty;
    private string _status = "正在加载历史记录…";

    public MainViewModel(Window owner, HistoryService history, Action<bool>? trayModeChanged = null)
        : this(owner, history, trayModeChanged, null) { }

    internal MainViewModel(Window owner, HistoryService history, Action<bool>? trayModeChanged, WindowOverlayHost? overlays)
    {
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
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => CanAct && _allRecords.Count > 0);
        SettingsCommand = new AsyncRelayCommand(ShowSettingsAsync, () => CanAct);
        UpdateCommand = new AsyncRelayCommand(ShowUpdatesAsync, () => CanAct);
        PauseCommand = new RelayCommand(() => { IsPaused = !IsPaused; _capture.Pause(); }, () => IsReady && !IsClosing);
    }

    public ICommand ClearCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand PauseCommand { get; }
    public bool CanAct => IsReady && !IsBusy && !IsClosing;
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
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(CapsuleText)); } }
    public Task InitializeAsync() => _initializationTask = InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (IsClosing) return;
        Notice = string.Empty;
        try
        {
            var warning = await Task.Run(() => _history.LoadAsync(_lifetime.Token));
            if (IsClosing) return;
            RefreshRecords();
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

    public async Task ShutdownAsync()
    {
        if (IsClosing) return;
        IsClosing = true;
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
                ApplyTrayMode(_history.Settings.MinimizeToTray);
                _updateCoordinator.ResetAutoCheckDelay();
                RefreshRecords();
                Status = "设置已保存";
                if (warning is not null) Notice = warning;
            }, "保存设置失败");
        }
        catch (Exception exception) { Report("打开设置失败", exception); }
    }

    private Task ShowUpdatesAsync() => _updateCoordinator.ShowUpdatesAsync();

    private void ApplyTrayMode(bool enabled)
    {
        try { _trayModeChanged?.Invoke(enabled); }
        catch (Exception exception)
        {
            AppLogger.LogException("更新系统托盘状态失败", exception);
            Notice = "系统托盘不可用；关闭窗口时程序将退出。";
        }
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
        _allRecords = _history.Records.OrderByDescending(record => record.CopiedAtUtc)
            .Select((record, index) => new RecordViewModel(record, this) { Position = index + 1 }).ToList();
        Raise(nameof(RecordCount)); Raise(nameof(HistoryRecords)); Raise(nameof(LatestRecord));
        Raise(nameof(HasRecords)); Raise(nameof(CapsuleText));
        RaiseState();
    }

    private void RaiseState()
    {
        Raise(nameof(CanAct)); Raise(nameof(MonitorStatus));
        (ClearCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        foreach (var item in _allRecords) item.RefreshCommands();
    }

    public void ReportHotKeyFailure(Exception exception)
    {
        string previousNotice = Notice;
        Report("全局快捷键 Ctrl+Shift+Space 注册失败，可能已被其他程序占用；仍可点击胶囊箭头查看历史记录", exception);
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
