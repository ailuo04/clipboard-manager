namespace ClipboardManager.Core.Updates;

/// <summary>Enforces legal update transitions and makes failure/recovery decisions visible to the UI.</summary>
public sealed class UpdateStateMachine
{
    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateState? ErrorStage { get; private set; }
    public UpdateErrorKind? ErrorKind { get; private set; }
    public string? RecoveryMessage { get; private set; }
    public bool CanCancel => State is UpdateState.Downloading or UpdateState.Verifying;
    public bool CanRetry => State == UpdateState.Error && ErrorKind is not (UpdateErrorKind.InvalidManifest or UpdateErrorKind.InvalidRelease);

    public void BeginChecking() => Move(UpdateState.Checking, UpdateState.Idle, UpdateState.UpToDate, UpdateState.Available, UpdateState.Error, UpdateState.Cancelled, UpdateState.Complete);
    public void ReportUpToDate() => Move(UpdateState.UpToDate, UpdateState.Checking);
    public void ReportAvailable() => Move(UpdateState.Available, UpdateState.Checking);
    public void BeginDownloading() => Move(UpdateState.Downloading, UpdateState.Available, UpdateState.Cancelled, UpdateState.Error);
    public void BeginVerifying() => Move(UpdateState.Verifying, UpdateState.Downloading);
    public void MarkReadyToApply() => Move(UpdateState.ReadyToApply, UpdateState.Verifying);
    public void BeginApplying() => Move(UpdateState.Applying, UpdateState.Verifying, UpdateState.ReadyToApply);
    public void BeginStartupHealthCheck() => Move(UpdateState.StartupHealthCheck, UpdateState.Applying);
    public void Complete() => Move(UpdateState.Complete, UpdateState.StartupHealthCheck);

    public void CancelChecking()
    {
        if (State != UpdateState.Checking) throw new InvalidOperationException($"An update check cannot be cancelled from {State}.");
        ErrorStage = State;
        State = UpdateState.Cancelled;
        ErrorKind = UpdateErrorKind.Cancelled;
        RecoveryMessage = "更新检查已取消。";
    }

    public bool Cancel()
    {
        if (!CanCancel) return false;
        ErrorStage = State;
        State = UpdateState.Cancelled;
        ErrorKind = UpdateErrorKind.Cancelled;
        RecoveryMessage = "下载已取消，未应用更新。可随时重新开始下载。";
        return true;
    }

    public void Fail(UpdateErrorKind kind)
    {
        if (State is UpdateState.Idle or UpdateState.Complete or UpdateState.Cancelled or UpdateState.Error)
            throw new InvalidOperationException($"An update error cannot be reported from {State}.");
        ErrorStage = State;
        State = UpdateState.Error;
        ErrorKind = kind;
        RecoveryMessage = GetRecoveryMessage(kind);
    }

    private void Move(UpdateState next, params UpdateState[] allowedFrom)
    {
        if (!allowedFrom.Contains(State))
            throw new InvalidOperationException($"Invalid update transition: {State} → {next}.");
        State = next;
        ErrorStage = null;
        ErrorKind = null;
        RecoveryMessage = null;
    }

    private static string GetRecoveryMessage(UpdateErrorKind kind) => kind switch
    {
        UpdateErrorKind.Network => "连接更新服务失败。请检查网络后重试。",
        UpdateErrorKind.InvalidRelease or UpdateErrorKind.InvalidManifest => "更新信息未通过安全校验。请稍后重试或使用官方安装包。",
        UpdateErrorKind.DownloadFailed => "更新文件下载失败。检查网络后可重新下载。",
        UpdateErrorKind.VerificationFailed => "下载文件未通过校验，已丢弃暂存文件。请重新检查更新。",
        UpdateErrorKind.InsufficientDiskSpace => "可用磁盘空间不足。释放空间后重试。",
        UpdateErrorKind.InstallDirectoryNotWritable => "应用目录不可写。请使用安装包升级，或手动替换便携版。",
        UpdateErrorKind.ApplicationExitFailed => "应用未能安全退出，程序文件未更改。关闭应用后重试。",
        UpdateErrorKind.ApplyFailed => "更新未能完成，恢复旧版本后可重新尝试。",
        UpdateErrorKind.StartupHealthCheckFailed => "新版未能完成启动检查，已请求恢复旧版本。",
        UpdateErrorKind.Cancelled => "更新操作已取消。",
        _ => "更新操作失败。请重试或使用官方安装包。"
    };
}

