namespace ClipboardManager.Core.Updates;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Verifying,
    ReadyToApply,
    Applying,
    StartupHealthCheck,
    Complete,
    Cancelled,
    Error
}

