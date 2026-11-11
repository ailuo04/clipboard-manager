using ClipboardManager.Core.Updates;

namespace ClipboardManager.Core.Updates;

public static class UpdateProtocol
{
    public const string WorkspaceDirectoryName = ".clipboard-update";
    public const string HealthRequestFileName = "health-request.json";
    public const string HealthReadyFileName = "health-ready.json";
    public const string WorkerErrorFileName = "worker-error.json";
    public const string SingleInstanceMutexName = "Local\\ClipboardManager.Desktop.v1";
    public const string HealthArgument = "--update-health";
}

public sealed record UpdateHealthMarker(Guid TransactionId, string Token);

public sealed record UpdateWorkerDiagnostic(
    DateTimeOffset TimestampUtc,
    Guid TransactionId,
    string Phase,
    string ExceptionType,
    Guid CorrelationId);
