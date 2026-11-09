using ClipboardManager.Core.Settings;

namespace ClipboardManager.Core.History;

public interface IHistoryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<HistoryPage> SearchAsync(HistoryQuery query, CancellationToken cancellationToken);
    Task<ClipboardRecord?> FindDuplicateAsync(
        ClipboardContentKind kind,
        string contentHash,
        CancellationToken cancellationToken);
    Task ApplyAsync(HistoryMutation mutation, CancellationToken cancellationToken);
    Task<bool> HasImportedMigrationAsync(string migrationId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically imports all supplied legacy data and records the migration ID in the same transaction.
    /// A migration ID is unique; retrying an already committed ID must not import the data a second time.
    /// </summary>
    Task ImportLegacyAsync(
        string migrationId,
        IReadOnlyList<ClipboardRecord> records,
        AppSettings settings,
        IReadOnlyList<string> pendingImageDeletes,
        CancellationToken cancellationToken);

    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> LoadPendingImageDeletesAsync(CancellationToken cancellationToken);
}
