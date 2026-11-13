using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager;

internal sealed class UpdateOverlay : OverlayView
{
    private readonly Func<CancellationToken, Task<AvailableUpdate?>> _checkAsync;
    private readonly Func<AvailableUpdate, IProgress<UpdateProgress>?, CancellationToken, Task> _prepareAsync;
    private readonly Action _commitPrepared;
    private readonly Action _cancelPrepared;
    private readonly Grid _confirmationActions;
    private readonly Button _cancelConfirmation;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _status;
    private readonly TextBlock _details;
    private readonly ProgressBar _progress;
    private readonly Button _check;
    private readonly Button _install;
    private readonly Button _cancelDownload;
    private AvailableUpdate? _available;
    private CancellationTokenSource? _download;
    private Task _operation = Task.CompletedTask;
    private bool _busy;
    private bool _closed;
    private bool _autoCheckStarted;
    private bool _confirmingInstall;

    internal UpdateOverlay(UpdateService updates)
        : this(updates.CheckAsync, updates.PrepareAsync, updates.LaunchPrepared, updates.CancelPrepared) { }

    internal UpdateOverlay(
        Func<CancellationToken, Task<AvailableUpdate?>> checkAsync,
        Func<AvailableUpdate, IProgress<UpdateProgress>?, CancellationToken, Task> prepareAsync,
        Action commitPrepared,
        Action cancelPrepared)
    {
        _checkAsync = checkAsync ?? throw new ArgumentNullException(nameof(checkAsync));
        _prepareAsync = prepareAsync ?? throw new ArgumentNullException(nameof(prepareAsync));
        _commitPrepared = commitPrepared ?? throw new ArgumentNullException(nameof(commitPrepared));
        _cancelPrepared = cancelPrepared ?? throw new ArgumentNullException(nameof(cancelPrepared));

        var view = new Views.UpdateView();
        Content = view;
        _status = view.UpdateStatusText;
        _details = view.UpdateDetailsText;
        _progress = view.UpdateProgressBar;
        _check = view.CheckUpdateButton;
        _install = view.InstallUpdateButton;
        _cancelDownload = view.CancelDownloadButton;
        _confirmationActions = view.ConfirmationActions;
        _cancelConfirmation = view.CancelConfirmationButton;
        view.CurrentVersionText.Text = "版本 " + UpdateService.CurrentVersion;
        _check.Click += (_, _) => StartOperation(CheckAsync);
        _install.Click += (_, _) => ShowInstallConfirmation();
        view.ConfirmUpdateButton.Click += (_, _) => StartOperation(InstallAsync);
        _cancelConfirmation.Click += (_, _) => CancelInstallConfirmation();
        _cancelDownload.Click += (_, _) => { if (!_closed) _download?.Cancel(); };
        Loaded += OnLoaded;
    }

    internal Task WhenIdle => _operation;

    internal override string? PageTitle => "应用更新";

    internal override void OnDismissed(bool? result)
    {
        if (_closed) return;
        _closed = true;
        _confirmingInstall = false;
        Loaded -= OnLoaded;
        _lifetime.Cancel();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_closed || _autoCheckStarted) return;
        _autoCheckStarted = true;
        StartOperation(CheckAsync);
    }

    private void StartOperation(Func<Task> operation)
    {
        if (_closed || _busy) return;
        Task task;
        try { task = operation(); }
        catch (Exception exception)
        {
            AppLogger.LogException("更新覆盖层操作启动失败", exception);
            return;
        }
        _operation = task;
        _ = ObserveOperationAsync(task);
    }

    private async Task ObserveOperationAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { AppLogger.LogException("更新覆盖层操作失败", exception); }
        finally
        {
            if (ReferenceEquals(_operation, task)) _operation = Task.CompletedTask;
        }
    }

    private async Task CheckAsync()
    {
        if (_busy || _closed) return;
        _available = null;
        SetBusy(true);
        _check.Content = "检查更新";
        ShowStatus("正在检查更新…");
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        try
        {
            _available = await _checkAsync(_lifetime.Token);
            if (_closed) return;
            if (_available is null) ShowStatus("已是最新版本");
            else ShowStatus("发现新版本 " + _available.Manifest.Version,
                $"下载 {Formatters.FormatSize(_available.DownloadSize)}，更新后自动重启");
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception)
        {
            if (!_closed)
            {
                ShowStatus("暂时无法检查更新", exception.Message);
                _check.Content = "重试";
            }
        }
        finally { if (!_closed) { _progress.Visibility = Visibility.Collapsed; SetBusy(false); } }
    }

    private void ShowStatus(string status, string details = "")
    {
        _status.Text = status;
        _details.Text = details;
        _details.Visibility = details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task InstallAsync()
    {
        if (_busy || _available is null || _closed || !_confirmingInstall) return;
        _confirmingInstall = false;

        using var download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _download = download;
        SetBusy(true);
        ShowStatus("正在准备更新…");
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        try
        {
            var progress = new Progress<UpdateProgress>(value =>
            {
                if (_closed) return;
                ShowStatus(value.Status, $"{Formatters.FormatSize(value.Downloaded)} / {Formatters.FormatSize(value.Total)}");
                _progress.Value = value.Total == 0 ? 100 : Math.Clamp(100d * value.Downloaded / value.Total, 0, 100);
            });
            await _prepareAsync(_available, progress, download.Token);
            download.Token.ThrowIfCancellationRequested();
            if (_closed) { _cancelPrepared(); return; }
            CommitPreparedUpdate();
        }
        catch (OperationCanceledException)
        {
            _cancelPrepared();
            if (!_closed) ShowStatus("更新已取消");
        }
        catch (Exception exception)
        {
            if (!_closed) ShowStatus("更新未完成", exception.Message);
        }
        finally
        {
            _download = null;
            if (!_closed) { _progress.Visibility = Visibility.Collapsed; SetBusy(false); }
        }
    }

    private void ShowInstallConfirmation()
    {
        if (_busy || _available is null || _closed || _confirmingInstall) return;
        _confirmingInstall = true;
        ShowStatus($"更新到 {_available.Manifest.Version}？",
            $"下载 {Formatters.FormatSize(_available.DownloadSize)}\n更新完成后自动重启，剪贴板历史、图片和设置将保留。");
        SetBusy(false);
        _cancelConfirmation.Focus();
    }

    private void CancelInstallConfirmation()
    {
        if (_busy || _closed || !_confirmingInstall || _available is null) return;
        _confirmingInstall = false;
        ShowStatus("发现新版本 " + _available.Manifest.Version,
            $"下载 {Formatters.FormatSize(_available.DownloadSize)}，更新后自动重启");
        SetBusy(false);
        _install.Focus();
    }

    internal void CommitPreparedUpdate()
    {
        try { _commitPrepared(); }
        catch
        {
            _cancelPrepared();
            throw;
        }
        Complete(true);
    }

    private void SetBusy(bool value)
    {
        if (_closed) return;
        _busy = value;
        // One action at a time: cancel while downloading, install when there is an update, otherwise check.
        bool downloading = _download is not null;
        bool canInstall = _available is not null;
        _cancelDownload.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        _install.Visibility = !downloading && canInstall && !_confirmingInstall ? Visibility.Visible : Visibility.Collapsed;
        _confirmationActions.Visibility = _confirmingInstall ? Visibility.Visible : Visibility.Collapsed;
        _confirmationActions.IsEnabled = !value;
        _check.Visibility = !downloading && !canInstall ? Visibility.Visible : Visibility.Collapsed;
        _check.IsEnabled = !value;
        _install.IsEnabled = !value && canInstall;
    }
}
