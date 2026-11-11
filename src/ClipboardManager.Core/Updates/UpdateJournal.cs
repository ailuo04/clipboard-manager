namespace ClipboardManager.Core.Updates;

public enum UpdateTransactionState
{
    Prepared,
    Applying,
    HealthPending,
    RollingBack,
    RollbackPending,
    RolledBack,
    Complete
}

public sealed record UpdateJournal(
    int Schema,
    Guid TransactionId,
    UpdateTransactionState State,
    IReadOnlyList<UpdateOperation> Operations,
    DateTimeOffset UpdatedAtUtc);

public sealed record UpdateTransactionResult(Guid TransactionId, UpdateTransactionState State);

