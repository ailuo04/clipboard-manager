namespace ClipboardManager.Core.History;

public sealed record HistoryChangeResult(
    IReadOnlyList<ClipboardRecord> Upserted,
    IReadOnlyList<Guid> DeletedIds,
    string? Warning);

public sealed record RetentionResult(IReadOnlyList<Guid> DeleteIds, string? CapacityWarning);
