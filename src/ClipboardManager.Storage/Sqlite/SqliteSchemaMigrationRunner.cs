using ClipboardManager.Core.Settings;
using ClipboardManager.Core.Storage;
using Microsoft.Data.Sqlite;
using System.Text;

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
    private readonly Action? _afterSchemaObjectsCreated;

    public SqliteSchemaMigrationRunner(SqliteConnectionFactory connectionFactory, Action? afterSchemaObjectsCreated = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _afterSchemaObjectsCreated = afterSchemaObjectsCreated;
    }

    public bool Fts5TrigramAvailable { get; private set; }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_connectionFactory.DatabasePath))
        {
            if (new[] { "-journal", "-wal", "-shm" }.Any(suffix => File.Exists(_connectionFactory.DatabasePath + suffix)))
                throw new SqliteStorageException("SQLite sidecar files exist without their database; all files were left unchanged and require recovery.");
            await CreateInitialDatabaseAtomicallyAsync(cancellationToken).ConfigureAwait(false);
        }
        ValidateExistingDatabaseHeader();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        ValidateExistingDatabaseHeader();
        int currentVersion = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (currentVersion > DatabaseSchema.CurrentVersion)
            throw new SqliteStorageException(
                $"Database schema version {currentVersion} is newer than supported version {DatabaseSchema.CurrentVersion}; the database was left unchanged.");

        if (currentVersion < DatabaseSchema.CurrentVersion)
        {
            if (currentVersion != 0)
                throw new SqliteStorageException($"No SQLite migration is available from schema version {currentVersion}.");

            if (await HasUserObjectsAsync(connection, cancellationToken).ConfigureAwait(false))
                throw new SqliteStorageException("An unversioned SQLite database already contains user objects; it was left unchanged and requires recovery.");

            using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, SchemaV1, cancellationToken).ConfigureAwait(false);
            _afterSchemaObjectsCreated?.Invoke();
            await SeedSettingsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                $"PRAGMA user_version = {DatabaseSchema.CurrentVersion};", cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        else
        {
            await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        Fts5TrigramAvailable = await ProbeFts5TrigramAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateCurrentSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        string[] requiredTables = ["records", "settings", "pending_image_deletes", "migration_history"];
        var tableNames = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tableNames.Add(reader.GetString(0));
        }

        if (requiredTables.Any(name => !tableNames.Contains(name)))
            throw new SqliteStorageException("The database claims a supported schema version but is missing required tables; it was left unchanged and requires recovery.");

        var expectedColumns = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["records"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = "TEXT", ["kind"] = "INTEGER", ["text_value"] = "TEXT", ["image_file_name"] = "TEXT",
                ["byte_length"] = "INTEGER", ["image_width"] = "INTEGER", ["image_height"] = "INTEGER",
                ["content_hash"] = "TEXT", ["copied_at_ticks"] = "INTEGER", ["is_favorite"] = "INTEGER"
            },
            ["settings"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["singleton_id"] = "INTEGER", ["max_records"] = "INTEGER", ["max_storage_mb"] = "INTEGER",
                ["always_on_top"] = "INTEGER", ["auto_check_updates"] = "INTEGER", ["minimize_to_tray"] = "INTEGER",
                ["hide_after_copy"] = "INTEGER", ["theme_preference"] = "TEXT"
            },
            ["pending_image_deletes"] = new(StringComparer.OrdinalIgnoreCase) { ["file_name"] = "TEXT", ["queued_at_ticks"] = "INTEGER" },
            ["migration_history"] = new(StringComparer.OrdinalIgnoreCase) { ["migration_id"] = "TEXT", ["imported_at_ticks"] = "INTEGER" }
        };
        var primaryKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["records"] = "id", ["settings"] = "singleton_id", ["pending_image_deletes"] = "file_name", ["migration_history"] = "migration_id"
        };
        foreach (var table in expectedColumns)
        {
            var columns = new Dictionary<string, (string Type, int PrimaryKeyOrder)>(StringComparer.OrdinalIgnoreCase);
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table.Key}\");";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                columns[reader.GetString(1)] = (reader.GetString(2), reader.GetInt32(5));
            if (table.Value.Any(column => !columns.TryGetValue(column.Key, out var actual)
                    || !string.Equals(actual.Type, column.Value, StringComparison.OrdinalIgnoreCase)))
                throw new SqliteStorageException($"The database table '{table.Key}' has incompatible columns; it was left unchanged and requires recovery.");
            string[] actualPrimaryKey = columns.Where(column => column.Value.PrimaryKeyOrder > 0)
                .OrderBy(column => column.Value.PrimaryKeyOrder).Select(column => column.Key).ToArray();
            if (!actualPrimaryKey.SequenceEqual([primaryKeys[table.Key]], StringComparer.OrdinalIgnoreCase))
                throw new SqliteStorageException($"The database table '{table.Key}' has an incompatible schema; it was left unchanged and requires recovery.");
        }
    }

    private async Task CreateInitialDatabaseAtomicallyAsync(CancellationToken cancellationToken)
    {
        string databasePath = _connectionFactory.DatabasePath;
        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = databasePath + ".init." + Guid.NewGuid().ToString("N");
        try
        {
            var temporaryFactory = new SqliteConnectionFactory(temporaryPath, _connectionFactory.BusyTimeoutMilliseconds);
            await using (var connection = await temporaryFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            {
                using var transaction = connection.BeginTransaction();
                await ExecuteAsync(connection, transaction, SchemaV1, cancellationToken).ConfigureAwait(false);
                _afterSchemaObjectsCreated?.Invoke();
                await SeedSettingsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction,
                    $"PRAGMA user_version = {DatabaseSchema.CurrentVersion};", cancellationToken).ConfigureAwait(false);
                transaction.Commit();
            }

            File.Move(temporaryPath, databasePath);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
            DeleteIfExists(temporaryPath + "-journal");
            DeleteIfExists(temporaryPath + "-wal");
            DeleteIfExists(temporaryPath + "-shm");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private void ValidateExistingDatabaseHeader()
    {
        string path = _connectionFactory.DatabasePath;
        if (!File.Exists(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < 100)
        {
            if (File.Exists(path + "-journal")) return;
            throw new SqliteStorageException("An existing SQLite database is truncated or invalid; it was left unchanged and requires recovery.");
        }
        Span<byte> header = stackalloc byte[16];
        if (stream.Read(header) != header.Length || !header.SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")))
            throw new SqliteStorageException("An existing database is not a valid SQLite file; it was left unchanged and requires recovery.");
    }

    private static async Task<bool> HasUserObjectsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name NOT LIKE 'sqlite_%');";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
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
