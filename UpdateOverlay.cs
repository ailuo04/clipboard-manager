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
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _status;
    private readonly TextBlock _details;
    private readonly TextBlock _credentialStatus;
    private readonly ProgressBar _progress;
    private readonly Button _check;
    private readonly Button _install;
    private readonly Button _cancelDownload;
    private readonly Button _saveToken;
    private readonly Button _deleteToken;
    private readonly PasswordBox _token;
    private AvailableUpdate? _available;
    private CancellationTokenSource? _download;
    private Task _operation = Task.CompletedTask;
    private bool _busy;
    private bool _closed;
    private bool _autoCheckStarted;

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

        var view = ViewLoader.Load<Grid>("Views/UpdateView.xaml");
        Content = view;
        _status = (TextBlock)view.FindName("UpdateStatusText");
        _details = (TextBlock)view.FindName("UpdateDetailsText");
        _credentialStatus = (TextBlock)view.FindName("UpdateCredentialText");
        _progress = (ProgressBar)view.FindName("UpdateProgressBar");
        _check = (Button)view.FindName("CheckUpdateButton");
        _install = (Button)view.FindName("InstallUpdateButton");
        _cancelDownload = (Button)view.FindName("CancelDownloadButton");
        _saveToken = (Button)view.FindName("SaveUpdateTokenButton");
        _deleteToken = (Button)view.FindName("DeleteUpdateTokenButton");
        _token = (PasswordBox)view.FindName("UpdateTokenBox");
        ((TextBlock)view.FindName("CurrentVersionText")).Text = "当前版本 " + UpdateService.CurrentVersion;
        _check.Click += (_, _) => StartOperation(CheckAsync);
        _install.Click += (_, _) => StartOperation(InstallAsync);
        _cancelDownload.Click += (_, _) => { if (!_closed) _download?.Cancel(); };
        ((Button)view.FindName("CloseUpdateButton")).Click += (_, _) => Complete(null);
        _saveToken.Click += (_, _) => ChangeCredential(
            () => { UpdateCredentials.Save(_token.Password.Trim()); _token.Clear(); }, "访问令牌已安全保存。");
        _deleteToken.Click += (_, _) => ChangeCredential(
            () => { UpdateCredentials.Delete(); _token.Clear(); }, "已删除保存的访问令牌。");
        Loaded += OnLoaded;
        try { _credentialStatus.Text = UpdateCredentials.Read() is null ? "未保存访问令牌" : "已保存访问令牌"; }
        catch (Exception exception) { _credentialStatus.Text = exception.Message; }
    }

    internal Task WhenIdle => _operation;

    internal override void OnDismissed(bool? result)
    {
        if (_closed) return;
        _closed = true;
        Loaded -= OnLoaded;
        try { _lifetime.Cancel(); }
        finally { _token.Clear(); }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_closed || _autoCheckStarted) return;
        _autoCheckStarted = true;
        StartOperation(CheckAsync);
    }

    private void StartOperation(Func<Task> operation)
    {
        if (_closed) return;
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

    private void ChangeCredential(Action action, string success)
    {
        if (_closed) return;
        try { action(); _credentialStatus.Text = success; }
        catch (Exception exception) { if (!_closed) _credentialStatus.Text = exception.Message; }
    }

    private async Task CheckAsync()
    {
        if (_busy || _closed) return;
        SetBusy(true);
        _available = null;
        _status.Text = "正在检查更新";
        _details.Text = "";
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        try
        {
            _available = await _checkAsync(_lifetime.Token);
            if (_closed) return;
            if (_available is null) _status.Text = "已是最新版本";
            else
            {
                _status.Text = "发现新版本 " + _available.Manifest.Version;
                _details.Text = $"下载 {UpdateService.FormatSize(_available.DownloadSize)} · 更新 {_available.Plan.Changes.Count} 个文件 · 删除 {_available.Plan.Deletes.Count} 个旧文件";
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception) { if (!_closed) { _status.Text = "检查更新失败"; _details.Text = exception.Message; } }
        finally { if (!_closed) { _progress.Visibility = Visibility.Collapsed; SetBusy(false); } }
    }

    private async Task InstallAsync()
    {
        if (_busy || _available is null || _closed) return;
        const string title = "增量更新";
        string message = $"下载 {UpdateService.FormatSize(_available.DownloadSize)} 并更新到 {_available.Manifest.Version}？\n程序将保存当前操作后重启，剪贴板历史和设置保持不变。";
        Window? owner = Window.GetWindow(this);
        MessageBoxResult confirmation = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        SetBusy(true);
        using var download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _download = download;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        _cancelDownload.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<UpdateProgress>(value =>
            {
                if (_closed) return;
                _status.Text = value.Status;
                _details.Text = $"{UpdateService.FormatSize(value.Downloaded)} / {UpdateService.FormatSize(value.Total)}";
                _progress.Value = value.Total == 0 ? 100 : Math.Clamp(100d * value.Downloaded / value.Total, 0, 100);
            });
            await _prepareAsync(_available, progress, download.Token);
            if (_closed) { _cancelPrepared(); return; }
            CommitPreparedUpdate();
        }
        catch (OperationCanceledException)
        {
            if (!_closed) { _status.Text = "更新已取消"; _details.Text = ""; }
        }
        catch (Exception exception)
        {
            if (!_closed) { _status.Text = "更新未完成"; _details.Text = exception.Message; }
        }
        finally
        {
            _download = null;
            if (!_closed) { _progress.Visibility = Visibility.Collapsed; _cancelDownload.Visibility = Visibility.Collapsed; SetBusy(false); }
        }
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
        _check.IsEnabled = !value;
        _install.IsEnabled = !value && _available is not null;
        _saveToken.IsEnabled = !value;
        _deleteToken.IsEnabled = !value;
        _token.IsEnabled = !value;
    }
}
