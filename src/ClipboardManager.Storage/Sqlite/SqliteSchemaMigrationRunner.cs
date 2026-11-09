using ClipboardManager.Core.Settings;
using ClipboardManager.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Storage.Sqlite;

public sealed class SqliteSchemaMigrationRunner : IDatabaseSchemaMigrator
{
    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS records (
            id TEXT NOT NULL PRIMARY KEY,
            kind INTEGER NOT NULL CHECK (kind IN (1, 2)),
            text_value TEXT NULL,
            image_file_name TEXT NULL,
            byte_length INTEGER NOT NULL CHECK (byte_length >= 0),
            image_width INTEGER NOT NULL,
            image_height INTEGER NOT NULL,
            content_hash TEXT NOT NULL,
            copied_at_ticks INTEGER NOT NULL,
            is_favorite INTEGER NOT NULL CHECK (is_favorite IN (0, 1))
        );
        CREATE INDEX IF NOT EXISTS ix_records_content_hash ON records(content_hash);
        CREATE INDEX IF NOT EXISTS ix_records_copied_at_ticks ON records(copied_at_ticks);
        CREATE INDEX IF NOT EXISTS ix_records_kind ON records(kind);
        CREATE INDEX IF NOT EXISTS ix_records_is_favorite ON records(is_favorite);

        CREATE TABLE IF NOT EXISTS settings (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            max_records INTEGER NOT NULL,
            max_storage_mb INTEGER NOT NULL,
            always_on_top INTEGER NOT NULL CHECK (always_on_top IN (0, 1)),
            auto_check_updates INTEGER NOT NULL CHECK (auto_check_updates IN (0, 1)),
            minimize_to_tray INTEGER NOT NULL CHECK (minimize_to_tray IN (0, 1)),
            hide_after_copy INTEGER NOT NULL CHECK (hide_after_copy IN (0, 1)),
            theme_preference TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS pending_image_deletes (
            file_name TEXT NOT NULL PRIMARY KEY,
            queued_at_ticks INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS migration_history (
            migration_id TEXT NOT NULL PRIMARY KEY,
            imported_at_ticks INTEGER NOT NULL
        );
        """;

    private static readonly AppSettings DefaultSettings = new(500, 512, false, true, false, true, "Light");
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteSchemaMigrationRunner(SqliteConnectionFactory connectionFactory)
        => _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public bool Fts5TrigramAvailable { get; private set; }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int currentVersion = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (currentVersion > DatabaseSchema.CurrentVersion)
            throw new SqliteStorageException(
                $"Database schema version {currentVersion} is newer than supported version {DatabaseSchema.CurrentVersion}; the database was left unchanged.");

        if (currentVersion < DatabaseSchema.CurrentVersion)
        {
            if (currentVersion != 0)
                throw new SqliteStorageException($"No SQLite migration is available from schema version {currentVersion}.");

            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, SchemaV1, cancellationToken).ConfigureAwait(false);
            await SeedSettingsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                $"PRAGMA user_version = {DatabaseSchema.CurrentVersion};", cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }

        Fts5TrigramAvailable = await ProbeFts5TrigramAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task SeedSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings (
                singleton_id, max_records, max_storage_mb, always_on_top,
                auto_check_updates, minimize_to_tray, hide_after_copy, theme_preference)
            VALUES (1, @max_records, @max_storage_mb, @always_on_top,
                @auto_check_updates, @minimize_to_tray, @hide_after_copy, @theme_preference)
            ON CONFLICT(singleton_id) DO NOTHING;
            """;
        SqliteHistoryRepository.AddSettingsParameters(command, DefaultSettings);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ProbeFts5TrigramAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE VIRTUAL TABLE temp.__clipboard_fts5_trigram_probe
                USING fts5(value, tokenize='trigram');
                DROP TABLE temp.__clipboard_fts5_trigram_probe;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
