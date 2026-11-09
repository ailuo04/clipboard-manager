using System.Data.Common;

namespace ClipboardManager.Core.Storage;

public interface IDatabaseMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    Task ApplyAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}
