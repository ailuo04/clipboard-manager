using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Storage.Sqlite;

public sealed class SqliteHistoryRepository : IHistoryRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteSchemaMigrationRunner _migrationRunner;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public SqliteHistoryRepository(string databasePath)
        : this(new SqliteConnectionFactory(databasePath))
    {
    }

    public SqliteHistoryRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _migrationRunner = new SqliteSchemaMigrationRunner(_connectionFactory);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await _migrationRunner.MigrateAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<HistoryPage> SearchAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0) throw new ArgumentOutOfRangeException(nameof(query), "Offset cannot be negative.");
        if (query.Limit < 0) throw new ArgumentOutOfRangeException(nameof(query), "Limit cannot be negative.");
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await ReadRecordsAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
        string searchText = query.SearchText ?? string.Empty;

        // FTS5 trigram tokenization does not establish recall-safety for every
        // OrdinalIgnoreCase Unicode query, so all queries use this full scan.
        var matches = candidates
            .Where(record => query.Kind switch
            {
                HistoryKindFilter.Text => record.Kind == ClipboardContentKind.Text,
                HistoryKindFilter.Image => record.Kind == ClipboardContentKind.Image,
                _ => true
            })
            .Where(record => !query.FavoritesOnly || record.IsFavorite)
            .Where(record => searchText.Length == 0 ||
                (record.Text?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(record => record.CopiedAtUtc.Ticks)
            .ThenBy(record => record.Id)
            .ToArray();

        return new HistoryPage(matches.Skip(query.Offset).Take(query.Limit).ToArray(), matches.Length, query.Offset, query.Limit);
    }

    public async Task<ClipboardRecord?> FindDuplicateAsync(
        ClipboardContentKind kind,
        string contentHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, text_value, image_file_name, byte_length, image_width,
                   image_height, content_hash, copied_at_ticks, is_favorite
            FROM records
            WHERE kind = @kind AND content_hash = @content_hash
            ORDER BY copied_at_ticks DESC, id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@kind", (int)kind);
        command.Parameters.AddWithValue("@content_hash", contentHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public async Task ApplyAsync(HistoryMutation mutation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        if (mutation.Upsert is not null)
            await UpsertRecordAsync(connection, transaction, mutation.Upsert, cancellationToken).ConfigureAwait(false);

        await DeleteRecordsAsync(connection, transaction, mutation.DeleteIds, cancellationToken).ConfigureAwait(false);

        // Keep settings and all queue edits in the same transaction as record changes.
        if (mutation.Settings is not null)
            await SaveSettingsAsync(connection, transaction, mutation.Settings, cancellationToken).ConfigureAwait(false);

        await AddPendingImageDeletesAsync(connection, transaction, mutation.PendingImageDeletes, cancellationToken).ConfigureAwait(false);
        await CompleteImageDeletesAsync(connection, transaction, mutation.CompletedImageDeletes, cancellationToken).ConfigureAwait(false);

        transaction.Commit();
    }

    public async Task<bool> HasImportedMigrationAsync(string migrationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM migration_history WHERE migration_id = @migration_id);";
        command.Parameters.AddWithValue("@migration_id", migrationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public async Task ImportLegacyAsync(
        string migrationId,
        IReadOnlyList<ClipboardRecord> records,
        AppSettings settings,
        IReadOnlyList<string> pendingImageDeletes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pendingImageDeletes);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.Transaction = transaction;
            existsCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM migration_history WHERE migration_id = @migration_id);";
            existsCommand.Parameters.AddWithValue("@migration_id", migrationId);
            if (Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            {
                transaction.Commit();
                return;
            }
        }

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertRecordAsync(connection, transaction, record, cancellationToken, insertOnly: true).ConfigureAwait(false);
        }

        await SaveSettingsAsync(connection, transaction, settings, cancellationToken).ConfigureAwait(false);
        await AddPendingImageDeletesAsync(connection, transaction, pendingImageDeletes, cancellationToken).ConfigureAwait(false);

        await using (var migrationCommand = connection.CreateCommand())
        {
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = """
                INSERT INTO migration_history (migration_id, imported_at_ticks)
                VALUES (@migration_id, @imported_at_ticks);
                """;
            migrationCommand.Parameters.AddWithValue("@migration_id", migrationId);
            migrationCommand.Parameters.AddWithValue("@imported_at_ticks", DateTime.UtcNow.Ticks);
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT max_records, max_storage_mb, always_on_top, auto_check_updates,
                   minimize_to_tray, hide_after_copy, theme_preference
            FROM settings WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new SqliteStorageException("The settings row is missing from the database.");
        return ReadSettings(reader);
    }

    public async Task<IReadOnlyList<string>> LoadPendingImageDeletesAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_name FROM pending_image_deletes ORDER BY file_name ASC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var fileNames = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            fileNames.Add(reader.GetString(0));
        return fileNames;
    }

    internal static void AddSettingsParameters(SqliteCommand command, AppSettings settings)
    {
        command.Parameters.AddWithValue("@max_records", settings.MaxRecords);
        command.Parameters.AddWithValue("@max_storage_mb", settings.MaxStorageMb);
        command.Parameters.AddWithValue("@always_on_top", settings.AlwaysOnTop ? 1 : 0);
        command.Parameters.AddWithValue("@auto_check_updates", settings.AutoCheckUpdates ? 1 : 0);
        command.Parameters.AddWithValue("@minimize_to_tray", settings.MinimizeToTray ? 1 : 0);
        command.Parameters.AddWithValue("@hide_after_copy", settings.HideAfterCopy ? 1 : 0);
        command.Parameters.AddWithValue("@theme_preference", settings.ThemePreference);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardRecord record,
        CancellationToken cancellationToken,
        bool insertOnly = false)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insertOnly
            ? """
                INSERT INTO records (
                    id, kind, text_value, image_file_name, byte_length, image_width,
                    image_height, content_hash, copied_at_ticks, is_favorite)
                VALUES (
                    @id, @kind, @text_value, @image_file_name, @byte_length, @image_width,
                    @image_height, @content_hash, @copied_at_ticks, @is_favorite)
                ON CONFLICT(id) DO NOTHING;
                """
            : """
                INSERT INTO records (
                    id, kind, text_value, image_file_name, byte_length, image_width,
                    image_height, content_hash, copied_at_ticks, is_favorite)
                VALUES (
                    @id, @kind, @text_value, @image_file_name, @byte_length, @image_width,
                    @image_height, @content_hash, @copied_at_ticks, @is_favorite)
                ON CONFLICT(id) DO UPDATE SET
                    kind = excluded.kind,
                    text_value = excluded.text_value,
                    image_file_name = excluded.image_file_name,
                    byte_length = excluded.byte_length,
                    image_width = excluded.image_width,
                    image_height = excluded.image_height,
                    content_hash = excluded.content_hash,
                    copied_at_ticks = excluded.copied_at_ticks,
                    is_favorite = excluded.is_favorite;
                """;
        command.Parameters.AddWithValue("@id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("@kind", (int)record.Kind);
        command.Parameters.AddWithValue("@text_value", (object?)record.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("@image_file_name", (object?)record.ImageFileName ?? DBNull.Value);
        command.Parameters.AddWithValue("@byte_length", record.ByteLength);
        command.Parameters.AddWithValue("@image_width", record.ImageWidth);
        command.Parameters.AddWithValue("@image_height", record.ImageHeight);
        command.Parameters.AddWithValue("@content_hash", record.ContentHash);
        command.Parameters.AddWithValue("@copied_at_ticks", record.CopiedAtUtc.Ticks);
        command.Parameters.AddWithValue("@is_favorite", record.IsFavorite ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteRecordsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        foreach (Guid id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM records WHERE id = @id;";
            command.Parameters.AddWithValue("@id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AddPendingImageDeletesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> fileNames,
        CancellationToken cancellationToken)
    {
        foreach (string fileName in fileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO pending_image_deletes (file_name, queued_at_ticks)
                VALUES (@file_name, @queued_at_ticks)
                ON CONFLICT(file_name) DO NOTHING;
                """;
            command.Parameters.AddWithValue("@file_name", fileName);
            command.Parameters.AddWithValue("@queued_at_ticks", DateTime.UtcNow.Ticks);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CompleteImageDeletesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> fileNames,
        CancellationToken cancellationToken)
    {
        foreach (string fileName in fileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM pending_image_deletes WHERE file_name = @file_name;";
            command.Parameters.AddWithValue("@file_name", fileName);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SaveSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AppSettings settings,
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
            ON CONFLICT(singleton_id) DO UPDATE SET
                max_records = excluded.max_records,
                max_storage_mb = excluded.max_storage_mb,
                always_on_top = excluded.always_on_top,
                auto_check_updates = excluded.auto_check_updates,
                minimize_to_tray = excluded.minimize_to_tray,
                hide_after_copy = excluded.hide_after_copy,
                theme_preference = excluded.theme_preference;
            """;
        AddSettingsParameters(command, settings);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ClipboardRecord[]> ReadRecordsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, kind, text_value, image_file_name, byte_length, image_width,
                   image_height, content_hash, copied_at_ticks, is_favorite
            FROM records;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<ClipboardRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            records.Add(ReadRecord(reader));
        return records.ToArray();
    }

    private static ClipboardRecord ReadRecord(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            (ClipboardContentKind)reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetString(7),
            new DateTime(reader.GetInt64(8), DateTimeKind.Utc),
            reader.GetInt32(9) != 0);

    private static AppSettings ReadSettings(SqliteDataReader reader)
        => new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2) != 0,
            reader.GetInt32(3) != 0,
            reader.GetInt32(4) != 0,
            reader.GetInt32(5) != 0,
            reader.GetString(6));
}
