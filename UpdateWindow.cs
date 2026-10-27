using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager;

public sealed class UpdateWindow : Window
{
    private readonly UpdateService _updates;
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
    private bool _busy;
    private bool _closed;

    public UpdateWindow(Window owner, UpdateService updates)
    {
        _updates = updates;
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = "应用更新";
        Width = 510; Height = 510; MinWidth = 430; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
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
        _check.Click += async (_, _) => await CheckAsync();
        _install.Click += async (_, _) => await InstallAsync();
        _cancelDownload.Click += (_, _) => _download?.Cancel();
        ((Button)view.FindName("CloseUpdateButton")).Click += (_, _) => Close();
        _saveToken.Click += (_, _) => ChangeCredential(() => { UpdateCredentials.Save(_token.Password.Trim()); _token.Clear(); }, "访问令牌已安全保存。");
        _deleteToken.Click += (_, _) => ChangeCredential(() => { UpdateCredentials.Delete(); _token.Clear(); }, "已删除保存的访问令牌。");
        Closing += OnClosing;
        Loaded += async (_, _) => await CheckAsync();
        try { _credentialStatus.Text = UpdateCredentials.Read() is null ? "未保存访问令牌" : "已保存访问令牌"; }
        catch (Exception exception) { _credentialStatus.Text = exception.Message; }
    }

    private void ChangeCredential(Action action, string success)
    {
        try { action(); _credentialStatus.Text = success; }
        catch (Exception exception) { _credentialStatus.Text = exception.Message; }
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
            _available = await _updates.CheckAsync(_lifetime.Token);
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
        if (MessageBox.Show(this, $"下载 {UpdateService.FormatSize(_available.DownloadSize)} 并更新到 {_available.Manifest.Version}？\n程序将保存当前操作后重启，剪贴板历史和设置保持不变。", "增量更新",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
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
            await _updates.PrepareAsync(_available, progress, download.Token);
            if (_closed) { _updates.CancelPrepared(); return; }
            DialogResult = true;
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

    private void SetBusy(bool value)
    {
        _busy = value;
        _check.IsEnabled = !value;
        _install.IsEnabled = !value && _available is not null;
        _saveToken.IsEnabled = !value;
        _deleteToken.IsEnabled = !value;
        _token.IsEnabled = !value;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        _closed = true;
        _lifetime.Cancel();
        _token.Clear();
    }
}
