namespace ClipboardManager.Core.Updates;

public enum UpdateOperationKind
{
    Replace,
    Delete
}

public sealed record UpdateOperation(
    string Path,
    UpdateOperationKind Kind,
    bool HadOriginal,
    string? OriginalSha256,
    long OriginalSize,
    string? NewSha256,
    long NewSize,
    bool BackupSaved = false,
    bool Applied = false);

