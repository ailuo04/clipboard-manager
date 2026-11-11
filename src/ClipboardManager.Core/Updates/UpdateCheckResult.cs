using System.Text.Json.Serialization;

namespace ClipboardManager.Core.Updates;

public sealed record UpdateAsset(long Id, string Name, long Size);

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? AvailableVersion,
    string ReleaseNotes,
    long DownloadSize,
    DateTimeOffset CheckedAtUtc,
    UpdateManifest? Manifest,
    IReadOnlyDictionary<string, UpdateAsset> Assets)
{
    public bool IsUpdateAvailable => AvailableVersion is not null && Manifest is not null;
}

public sealed record UpdateProgress(long BytesTransferred, long TotalBytes, string? CurrentFile);

public sealed class UpdateOperationException(UpdateErrorKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public UpdateErrorKind Kind { get; } = kind;
}


