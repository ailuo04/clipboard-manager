using System.Windows;
using System.Windows.Input;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly HistoryService _history;
    private readonly StartupService _startup;
    private readonly Window _owner;
    private Views.ImageEditorWindow? _imageEditor;
    private readonly WindowOverlayHost? _overlays;
    private readonly ClipboardReader _clipboard = new();
    private readonly ClipboardMonitor _monitor;
    private readonly ClipboardCaptureCoordinator _capture;
    private readonly UpdateCoordinator _updateCoordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private List<RecordViewModel> _allRecords = [];
    private List<RecordViewModel> _visibleRecords = [];
    private bool _isShowingFavorites;
    private Task _initializationTask = Task.CompletedTask;
    private bool _isReady;
    private bool _isBusy;
    private bool _isPaused;
    private bool _isClosing;
    private string _notice = string.Empty;
    private string _status = "正在加载历史记录…";

    public MainViewModel(Window owner, HistoryService history)
        : this(owner, history, null) { }

    internal MainViewModel(Window owner, HistoryService history, WindowOverlayHost? overlays,
        StartupService? startup = null)
    {
        _history = history;
        _startup = startup ?? new StartupService();
        _owner = owner;
        _overlays = overlays;
        _monitor = new ClipboardMonitor(owner);
        _capture = new ClipboardCaptureCoordinator(_clipboard, _history,
            () => IsReady && !IsPaused && !IsClosing, () => IsPaused, () => IsClosing,
            _lifetime, RunOperationAsync, RefreshRecords, value => Notice = value ?? string.Empty,
            value => Status = value);
        _capture.Attach(_monitor);
        _updateCoordinator = new UpdateCoordinator(owner, history, overlays, _lifetime,
            () => IsClosing, () => Notice, value => Notice = value, Report);
        ShowHistoryCommand = new RelayCommand(() => IsShowingFavorites = false);
        ShowFavoritesCommand = new RelayCommand(() => IsShowingFavorites = true);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => CanAct && _allRecords.Count > 0);
        EditLatestImageCommand = new AsyncRelayCommand(
            () => LatestImageRecord is { } image ? EditImageAsync(image) : Task.CompletedTask,
            () => CanAct && LatestImageRecord is not null);
        SettingsCommand = new AsyncRelayCommand(ShowSettingsAsync, () => CanAct);
        UpdateCommand = new AsyncRelayCommand(ShowUpdatesAsync, () => CanAct);
        PauseCommand = new RelayCommand(() => { IsPaused = !IsPaused; _capture.Pause(); }, () => IsReady && !IsClosing);
    }

    public ICommand ShowHistoryCommand { get; }
    public ICommand ShowFavoritesCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand EditLatestImageCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand PauseCommand { get; }
    public bool CanAct => IsReady && !IsBusy && !IsClosing;
    public int RecordCount => _allRecords.Count;
    public IReadOnlyList<RecordViewModel> HistoryRecords => _visibleRecords;
    public bool IsShowingFavorites
    {
        get => _isShowingFavorites;
        private set
        {
            if (!Set(ref _isShowingFavorites, value)) return;
            Raise(nameof(IsShowingHistory));
            RefreshVisibleRecords();
        }
    }
    public bool IsShowingHistory => !IsShowingFavorites;
    public string EmptyMessage => IsShowingFavorites ? "暂无收藏，点击记录旁的星标收藏" : "暂无剪贴板记录";
    public RecordViewModel? LatestRecord => _allRecords.FirstOrDefault();
    public RecordViewModel? LatestImageRecord => LatestRecord is { IsImage: true } image ? image : null;
    public bool HasRecords => _allRecords.Count > 0;
    public string CapsuleText => !IsReady ? Status
        : _history.Settings.HideCapsulePreview || _allRecords.Count == 0 ? "剪贴板"
        : _allRecords[0].CapsulePreview;
    public string? CapsuleImagePath => IsReady && !_history.Settings.HideCapsulePreview ? LatestRecord?.ImagePath : null;
    public Point? WidgetPosition
    {
        get
        {
            var settings = _history.Settings;
            return settings.WidgetLeft is double left && settings.WidgetTop is double top ? new Point(left, top) : null;
        }
    }
    public bool IsReady { get => _isReady; private set { if (Set(ref _isReady, value)) { RaiseState(); Raise(nameof(CapsuleText)); Raise(nameof(CapsuleImagePath)); } } }
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

    public Task ToggleFavoriteAsync(RecordViewModel item) => RunOperationAsync(async () =>
    {
        var current = _history.Records.FirstOrDefault(record => record.Id == item.Record.Id);
        if (current is null) return;
        string? warning = await _history.TogglePinAsync(current.Id, _lifetime.Token);
        RefreshRecords();
        Status = current.IsPinned ? "已取消收藏" : "已收藏";
        if (warning is not null) Notice = warning;
    }, "更新收藏失败");

    public Task EditImageAsync(RecordViewModel item)
    {
        if (!item.IsImage || string.IsNullOrWhiteSpace(item.ImagePath)) return Task.CompletedTask;
        return RunOperationAsync(async () =>
        {
            var image = await ImageLoader.LoadAsync(item.ImagePath, cancellationToken: _lifetime.Token);
            if (IsClosing) return;
            var editor = new Views.ImageEditorWindow(image, async edited =>
            {
                var saved = await _history.AddAsync(ClipboardSnapshot.FromImage(edited, "ImageAnnotation"),
                    _lifetime.Token, protectedRecordId: item.Record.Id);
                RefreshRecords();
                if (saved.Warning is not null) Notice = saved.Warning;
                if (saved.Record is null)
                    throw new InvalidOperationException(saved.Warning ?? "容量不足，标注图片未能保存。");
                await _clipboard.WriteImageAsync(_history.GetImagePath(saved.Record), _monitor.Handle, _lifetime.Token);
                Status = "标注图片已保存并复制到剪贴板";
            }) { Owner = _owner };
            _imageEditor = editor;
            try { editor.ShowDialog(); }
            finally { _imageEditor = null; }
        }, "打开图片编辑器失败");
    }

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
        _imageEditor?.CloseForShutdown();
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
            bool? startupEnabled = null;
            try { startupEnabled = _startup.IsEnabled; }
            catch (Exception exception) { AppLogger.LogException("读取开机自启动设置失败", exception); }
            var dialog = new SettingsOverlay(_history.Settings, _history.RootDirectory, ShowUpdatesAsync, startupEnabled);
            if (await _overlays.ShowAsync(dialog, OverlaySize.Settings) != true) return;
            await RunOperationAsync(async () =>
            {
                string? warning = await _history.UpdateSettingsAsync(dialog.Settings, _lifetime.Token);
                Notice = warning ?? string.Empty;
                ThemeManager.SetPreference(dialog.SelectedTheme);
                _updateCoordinator.ResetAutoCheckDelay();
                RefreshRecords();
                Status = "设置已保存";
                if (dialog.StartWithWindows is bool startWithWindows)
                {
                    try { _startup.SetEnabled(startWithWindows); }
                    catch (Exception exception)
                    {
                        AppLogger.LogException("更新开机自启动设置失败", exception);
                        Status = "部分设置已保存";
                        string message = "其他设置已保存，但开机自启动设置失败：" + exception.Message;
                        Notice = string.IsNullOrEmpty(Notice) ? message : Notice + " " + message;
                    }
                }
            }, "保存设置失败");
        }
        catch (Exception exception) { Report("打开设置失败", exception); }
    }

    private Task ShowUpdatesAsync() => _updateCoordinator.ShowUpdatesAsync();

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
            .Select(record => new RecordViewModel(record, this)).ToList();
        RefreshVisibleRecords();
        Raise(nameof(RecordCount)); Raise(nameof(LatestRecord));
        Raise(nameof(HasRecords)); Raise(nameof(CapsuleText)); Raise(nameof(CapsuleImagePath)); Raise(nameof(LatestImageRecord));
        RaiseState();
    }

    private void RefreshVisibleRecords()
    {
        _visibleRecords = _allRecords.Where(item => !IsShowingFavorites || item.IsPinned).ToList();
        for (int index = 0; index < _visibleRecords.Count; index++) _visibleRecords[index].Position = index + 1;
        Raise(nameof(HistoryRecords));
        Raise(nameof(EmptyMessage));
    }

    private void RaiseState()
    {
        Raise(nameof(CanAct)); Raise(nameof(MonitorStatus));
        (ClearCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EditLatestImageCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        foreach (var item in _allRecords) item.RefreshCommands();
    }

    public void ReportHotKeyFailure(Exception exception)
    {
        string previousNotice = Notice;
        Report("全局快捷键 Ctrl+Shift+Space 注册失败，可能已被其他程序占用；可从系统托盘或胶囊打开历史记录", exception);
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
