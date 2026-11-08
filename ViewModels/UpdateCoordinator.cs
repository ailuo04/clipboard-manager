using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager.ViewModels;

/// <summary>Owns the update pipeline: the auto-check timer, the manual update overlay flow, and workspace cleanup.</summary>
internal sealed class UpdateCoordinator
{
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _timer;
    private readonly Window _owner;
    private readonly HistoryService _history;
    private readonly WindowOverlayHost? _overlays;
    private readonly CancellationTokenSource _lifetimeSource;
    private readonly Func<bool> _isClosing;
    private readonly Func<string> _getNotice;
    private readonly Action<string> _setNotice;
    private readonly Action<string, Exception> _report;
    private Task _checkTask = Task.CompletedTask;
    private Task _overlayTask = Task.CompletedTask;
    private Task _cleanupTask = Task.CompletedTask;

    public UpdateCoordinator(Window owner, HistoryService history, WindowOverlayHost? overlays,
        CancellationTokenSource lifetimeSource, Func<bool> isClosing,
        Func<string> getNotice, Action<string> setNotice, Action<string, Exception> report)
    {
        _owner = owner;
        _history = history;
        _overlays = overlays;
        _lifetimeSource = lifetimeSource;
        _isClosing = isClosing;
        _getNotice = getNotice;
        _setNotice = setNotice;
        _report = report;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += OnUpdateTimer;
    }

    public UpdateService Service => _updates;

    /// <summary>Marks startup recovery done, starts workspace cleanup and the auto-check timer.</summary>
    public void StartAfterLoad()
    {
        try { _updates.MarkStartupReady(); }
        catch (Exception exception) { AppLogger.LogException("标记更新启动状态失败", exception); }
        _cleanupTask = CleanUpdateFilesAsync();
        _timer.Start();
    }

    public void ResetAutoCheckDelay() => _timer.Interval = TimeSpan.FromSeconds(5);

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        _timer.Tick -= OnUpdateTimer;
        await _overlayTask;
        await _checkTask;
        await _cleanupTask;
        _updates.Dispose();
    }

    public Task ShowUpdatesAsync()
    {
        if (_overlays is null || _isClosing()) return Task.CompletedTask;
        Task? operation = null;
        operation = ShowUpdatesCoreAsync(() =>
        {
            if (ReferenceEquals(_overlayTask, operation)) _overlayTask = Task.CompletedTask;
        });
        _overlayTask = operation;
        return operation;
    }

    private async Task ShowUpdatesCoreAsync(Action clearTracking)
    {
        bool handedOff = false;
        try
        {
            if (_overlays is null || _isClosing()) return;
            var overlay = new UpdateOverlay(_updates);
            bool? result = await _overlays.ShowAsync(overlay, OverlaySize.Update);
            await overlay.WhenIdle;
            if (result != true || _isClosing()) return;
            clearTracking();
            handedOff = true;
            _overlays.CloseAll(null);
            if (_owner is MainWindow mainWindow) mainWindow.ExitApplication();
            else _owner.Close();
        }
        catch (Exception exception)
        {
            try { _updates.CancelPrepared(); }
            catch (Exception cleanup) { AppLogger.LogException("清理未启动的更新失败", cleanup); }
            _report("启动更新失败", exception);
        }
        finally
        {
            if (!handedOff) clearTracking();
        }
    }

    private void OnUpdateTimer(object? sender, EventArgs args)
    {
        _timer.Interval = TimeSpan.FromHours(6);
        if (_isClosing() || !_history.Settings.AutoCheckUpdates || !_checkTask.IsCompleted) return;
        _checkTask = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updates.CheckAsync(_lifetimeSource.Token);
            if (update is null || _isClosing()) return;
            string message = $"发现新版本 {update.Manifest.Version} · 下载 {Formatters.FormatSize(update.DownloadSize)}";
            string notice = _getNotice();
            _setNotice(string.IsNullOrEmpty(notice) ? message : notice.Contains(message, StringComparison.Ordinal) ? notice : notice + "\n" + message);
        }
        catch (OperationCanceledException) when (_isClosing()) { }
        catch (Exception exception) { AppLogger.LogException("自动检查更新失败", exception); }
    }

    private async Task CleanUpdateFilesAsync()
    {
        try { await _updates.CleanFinishedAsync(_lifetimeSource.Token); }
        catch (OperationCanceledException) when (_isClosing()) { }
        catch (Exception exception) { AppLogger.LogException("清理更新临时文件失败", exception); }
    }
}
