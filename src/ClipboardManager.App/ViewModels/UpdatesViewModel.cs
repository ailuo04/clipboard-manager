using System.ComponentModel;
using System.Globalization;
using ClipboardManager.App.Services;
using ClipboardManager.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClipboardManager.App.ViewModels;

public sealed record UpdateCheckPreferenceResult(bool Enabled, bool Saved, string? Message);

/// <summary>Projects update pipeline state into a single-window, user-action-driven update center.</summary>
public sealed class UpdatesViewModel : ObservableObject, IDisposable
{
    private const int MaximumReleaseNotesLength = 5000;
    private readonly UpdateCoordinator _updates;
    private readonly Func<bool, CancellationToken, Task<UpdateCheckPreferenceResult>> _saveAutoCheckPreference;
    private readonly string? _logFilePath;
    private int _actionInProgress;
    private bool _autoCheckUpdates;
    private bool _isSavingAutoCheck;
    private string? _autoCheckMessage;
    private bool _disposed;

    public UpdatesViewModel(UpdateCoordinator updates, bool autoCheckUpdates,
        Func<bool, CancellationToken, Task<UpdateCheckPreferenceResult>> saveAutoCheckPreference, string? logFilePath = null)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _saveAutoCheckPreference = saveAutoCheckPreference ?? throw new ArgumentNullException(nameof(saveAutoCheckPreference));
        _autoCheckUpdates = autoCheckUpdates;
        _logFilePath = logFilePath;
        _updates.PropertyChanged += OnUpdateCoordinatorPropertyChanged;
    }

    public UpdateState State => _updates.State;
    public UpdateState? ErrorStage => _updates.ErrorStage;
    public string CurrentVersion => _updates.CurrentVersion;
    public string? LogFilePath => _logFilePath;
    public string AvailableVersion => _updates.CurrentUpdate?.AvailableVersion ?? "—";
    public string ReleaseNotes => LimitReleaseNotes(_updates.CurrentUpdate?.ReleaseNotes);
    public string DownloadSizeText => FormatBytes(_updates.CurrentUpdate?.DownloadSize ?? 0);
    public string LastCheckedText => _updates.LastCheckedAtUtc is DateTimeOffset checkedAt
        ? checkedAt.ToLocalTime().ToString("yyyy年M月d日 HH:mm", CultureInfo.InvariantCulture)
        : "尚未检查";
    public string StatusText => State switch
    {
        UpdateState.Checking => "正在检查更新…",
        UpdateState.UpToDate => "已是最新版本。",
        UpdateState.Available => "发现新版本，可查看说明并手动更新。",
        UpdateState.Downloading => "正在下载更新文件…",
        UpdateState.Verifying => "正在校验更新文件…",
        UpdateState.ReadyToApply => "文件已通过校验，正在准备交接…",
        UpdateState.Applying => "正在准备重启并安装更新…",
        UpdateState.StartupHealthCheck => "正在确认新版本正常启动…",
        UpdateState.Complete => "更新已完成。",
        UpdateState.Cancelled => "下载已取消，程序文件未更改。",
        UpdateState.Error => "更新未能完成。",
        _ => "尚未检查更新。"
    };
    public string StageText => _updates.ErrorStage switch
    {
        UpdateState.Checking => "检查更新",
        UpdateState.Downloading => "下载更新文件",
        UpdateState.Verifying => "校验更新文件",
        UpdateState.Applying => "应用更新",
        UpdateState.StartupHealthCheck => "启动健康确认",
        _ => State switch
        {
            UpdateState.Checking => "检查更新",
            UpdateState.Downloading => "下载更新文件",
            UpdateState.Verifying => "校验更新文件",
            UpdateState.Applying or UpdateState.ReadyToApply => "准备重启",
            UpdateState.StartupHealthCheck => "启动健康确认",
            _ => "更新状态"
        }
    };
    public string? RecoveryMessage => _updates.RecoveryMessage;
    public string DownloadButtonText => State == UpdateState.Error && CanRetry
        ? "重试下载并更新"
        : State == UpdateState.Cancelled ? "重新下载并更新" : "下载并更新";
    public string ProgressText => State switch
    {
        UpdateState.Downloading => $"已下载 {FormatBytes(_updates.Progress.BytesTransferred)} / {FormatBytes(_updates.Progress.TotalBytes)}",
        UpdateState.Verifying => "正在核对下载文件的完整性…",
        _ => string.Empty
    };
    public double ProgressPercent => _updates.Progress.TotalBytes <= 0
        ? 0
        : Math.Clamp(100d * _updates.Progress.BytesTransferred / _updates.Progress.TotalBytes, 0, 100);
    public bool IsProgressVisible => State is UpdateState.Downloading or UpdateState.Verifying;
    public bool IsVerifying => State == UpdateState.Verifying;
    public bool CanRetry => _updates.CanRetry;
    public bool CanCancel => _updates.CanCancel;
    public bool CanCheck => State is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Verifying or
        UpdateState.ReadyToApply or UpdateState.Applying or UpdateState.StartupHealthCheck);
    public bool CanDownload => _updates.CurrentUpdate?.IsUpdateAvailable == true &&
        (State is UpdateState.Available or UpdateState.Cancelled || State == UpdateState.Error && CanRetry);

    public bool AutoCheckUpdates
    {
        get => _autoCheckUpdates;
        private set => SetProperty(ref _autoCheckUpdates, value);
    }
    public bool IsSavingAutoCheck { get => _isSavingAutoCheck; private set { if (SetProperty(ref _isSavingAutoCheck, value)) OnPropertyChanged(nameof(CanToggleAutoCheck)); } }
    public bool CanToggleAutoCheck => !IsSavingAutoCheck;
    public string? AutoCheckMessage { get => _autoCheckMessage; private set => SetProperty(ref _autoCheckMessage, value); }

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!CanCheck || Interlocked.CompareExchange(ref _actionInProgress, 1, 0) != 0) return;
        try { await _updates.CheckForUpdatesAsync(cancellationToken); }
        finally { Interlocked.Exchange(ref _actionInProgress, 0); RefreshStateBindings(); }
    }

    public async Task DownloadAndUpdateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!CanDownload || Interlocked.CompareExchange(ref _actionInProgress, 1, 0) != 0) return;
        try { await _updates.DownloadAndVerifyAsync(cancellationToken); }
        finally { Interlocked.Exchange(ref _actionInProgress, 0); RefreshStateBindings(); }
    }

    public bool CancelDownload() => _updates.CancelDownload();

    public async Task SetAutoCheckUpdatesAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsSavingAutoCheck || enabled == AutoCheckUpdates) return;
        bool previous = AutoCheckUpdates;
        IsSavingAutoCheck = true;
        AutoCheckMessage = null;
        try
        {
            UpdateCheckPreferenceResult result = await _saveAutoCheckPreference(enabled, cancellationToken);
            AutoCheckUpdates = result.Enabled;
            AutoCheckMessage = result.Message ?? (result.Saved
                ? enabled ? "自动检查已开启。" : "自动检查已关闭。"
                : "自动检查设置保存失败，请重试。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AutoCheckUpdates = previous;
            throw;
        }
        catch
        {
            AutoCheckUpdates = previous;
            AutoCheckMessage = "自动检查设置保存失败，请重试。";
        }
        finally { IsSavingAutoCheck = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _updates.PropertyChanged -= OnUpdateCoordinatorPropertyChanged;
    }

    private void OnUpdateCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs args)
        => RefreshStateBindings();

    private void RefreshStateBindings()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(ErrorStage));
        OnPropertyChanged(nameof(CurrentVersion));
        OnPropertyChanged(nameof(AvailableVersion));
        OnPropertyChanged(nameof(ReleaseNotes));
        OnPropertyChanged(nameof(DownloadSizeText));
        OnPropertyChanged(nameof(LastCheckedText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StageText));
        OnPropertyChanged(nameof(RecoveryMessage));
        OnPropertyChanged(nameof(DownloadButtonText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsProgressVisible));
        OnPropertyChanged(nameof(IsVerifying));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanDownload));
    }

    private static string LimitReleaseNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "此版本没有附带更新说明。";
        string normalized = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return normalized.Length <= MaximumReleaseNotesLength
            ? normalized
            : normalized[..MaximumReleaseNotesLength] + "\n…";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "未知";
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value.ToString("0", CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
