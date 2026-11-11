namespace ClipboardManager.Core.Updates;

public enum UpdateErrorKind
{
    Network,
    InvalidRelease,
    InvalidManifest,
    DownloadFailed,
    VerificationFailed,
    InsufficientDiskSpace,
    InstallDirectoryNotWritable,
    ApplicationExitFailed,
    ApplyFailed,
    StartupHealthCheckFailed,
    Cancelled
}
