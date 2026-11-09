namespace ClipboardManager.Core.Storage;

public interface IDatabaseSchemaMigrator
{
    Task MigrateAsync(CancellationToken cancellationToken);
}
