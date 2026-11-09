using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Core.Storage;
using ClipboardManager.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClipboardManager.Storage.Tests;

public sealed class SqliteHistoryRepositoryTests
{
    [Fact]
    public async Task Initialize_creates_current_schema_and_required_indexes()
    {
        await using var database = new SqliteTestDatabase();
        var repository = new SqliteHistoryRepository(database.ConnectionFactory);

        await repository.InitializeAsync(CancellationToken.None);

        await using var connection = await database.ConnectionFactory.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(DatabaseSchema.CurrentVersion, await ScalarIntAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(5000, await ScalarIntAsync(connection, "PRAGMA busy_timeout;"));

        var tables = await ReadStringsAsync(connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");
        Assert.Contains("records", tables);
        Assert.Contains("settings", tables);
        Assert.Contains("pending_image_deletes", tables);
        Assert.Contains("migration_history", tables);

        var indexes = await ReadStringsAsync(connection,
            "SELECT name FROM sqlite_master WHERE type = 'index' ORDER BY name;");
        Assert.Contains("ix_records_content_hash", indexes);
        Assert.Contains("ix_records_copied_at_ticks", indexes);
        Assert.Contains("ix_records_kind", indexes);
        Assert.Contains("ix_records_is_favorite", indexes);
        Assert.Equal(new AppSettings(500, 512, false, true, false, true, "Light"),
            await repository.LoadSettingsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_is_idempotent_and_does_not_reset_persisted_data()
    {
        await using var database = new SqliteTestDatabase();
        var repository = new SqliteHistoryRepository(database.ConnectionFactory);
        await repository.InitializeAsync(CancellationToken.None);

        var record = TextRecord("persistent after reinitialize");
        var settings = Settings(maxRecords: 731, theme: "Dark");
        await repository.ApplyAsync(new HistoryMutation(record, [], ["retry.png"], [], settings), CancellationToken.None);

        await repository.InitializeAsync(CancellationToken.None);
        var reopenedRepository = new SqliteHistoryRepository(database.ConnectionFactory);
        await reopenedRepository.InitializeAsync(CancellationToken.None);
        var page = await reopenedRepository.SearchAsync(new HistoryQuery("persistent", HistoryKindFilter.All, false, 0, 20), CancellationToken.None);

        Assert.Equal([record.Id], page.Records.Select(item => item.Id));
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(settings, await reopenedRepository.LoadSettingsAsync(CancellationToken.None));
        Assert.Equal(["retry.png"], await reopenedRepository.LoadPendingImageDeletesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_rejects_unknown_schema_version_without_changing_database_bytes()
    {
        await using var database = new SqliteTestDatabase();
        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA user_version = {DatabaseSchema.CurrentVersion + 17};";
            await command.ExecuteNonQueryAsync();
        }
        var before = await File.ReadAllBytesAsync(database.DatabasePath);
        var repository = new SqliteHistoryRepository(database.ConnectionFactory);

        await Assert.ThrowsAsync<SqliteStorageException>(
            () => repository.InitializeAsync(CancellationToken.None));

        Assert.Equal(before, await File.ReadAllBytesAsync(database.DatabasePath));
    }

    [Fact]
    public async Task ApplyAsync_rolls_back_record_settings_and_pending_deletes_when_a_write_aborts()
    {
        await using var database = new SqliteTestDatabase();
        var repository = new SqliteHistoryRepository(database.ConnectionFactory);
        await repository.InitializeAsync(CancellationToken.None);
        var originalRecord = TextRecord("committed before mutation");
        var originalSettings = Settings(maxRecords: 500, theme: "Light");
        await repository.ApplyAsync(
            new HistoryMutation(originalRecord, [], ["already-pending.png"], [], originalSettings),
            CancellationToken.None);

        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER abort_selected_pending_delete
                BEFORE INSERT ON pending_image_deletes
                WHEN NEW.file_name = 'abort.png'
                BEGIN
                    SELECT RAISE(ABORT, 'injected storage failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var attemptedRecord = TextRecord("must roll back");
        var attemptedSettings = Settings(maxRecords: 987, theme: "Dark");
        await Assert.ThrowsAsync<SqliteException>(() => repository.ApplyAsync(
            new HistoryMutation(
                attemptedRecord,
                [],
                ["abort.png"],
                ["already-pending.png"],
                attemptedSettings),
            CancellationToken.None));

        var page = await repository.SearchAsync(new HistoryQuery("", HistoryKindFilter.All, false, 0, 20), CancellationToken.None);
        Assert.Equal([originalRecord.Id], page.Records.Select(item => item.Id));
        Assert.Equal(originalSettings, await repository.LoadSettingsAsync(CancellationToken.None));
        Assert.Equal(["already-pending.png"], await repository.LoadPendingImageDeletesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ImportLegacyAsync_imports_records_settings_pending_deletes_and_unique_id_atomically_and_once()
    {
        await using var database = new SqliteTestDatabase();
        var repository = new SqliteHistoryRepository(database.ConnectionFactory);
        await repository.InitializeAsync(CancellationToken.None);
        var record = TextRecord("legacy import");
        var settings = Settings(maxRecords: 810, theme: "Dark");

        await repository.ImportLegacyAsync("legacy-v1", [record], settings, ["legacy.png"], CancellationToken.None);
        Assert.True(await repository.HasImportedMigrationAsync("legacy-v1", CancellationToken.None));

        await repository.ImportLegacyAsync(
            "legacy-v1",
            [TextRecord("must not be imported twice")],
            Settings(maxRecords: 900, theme: "Light"),
            ["second.png"],
            CancellationToken.None);

        var page = await repository.SearchAsync(new HistoryQuery("", HistoryKindFilter.All, false, 0, 20), CancellationToken.None);
        Assert.Equal([record.Id], page.Records.Select(item => item.Id));
        Assert.Equal(settings, await repository.LoadSettingsAsync(CancellationToken.None));
        Assert.Equal(["legacy.png"], await repository.LoadPendingImageDeletesAsync(CancellationToken.None));
        Assert.False(await repository.HasImportedMigrationAsync("different-id", CancellationToken.None));
    }

    [Fact]
    public async Task ImportLegacyAsync_rolls_back_records_settings_queue_and_migration_id_when_a_write_aborts()
    {
        await using var database = new SqliteTestDatabase();
        var repository = new SqliteHistoryRepository(database.ConnectionFactory);
        await repository.InitializeAsync(CancellationToken.None);
        var originalRecord = TextRecord("before failed import");
        var originalSettings = Settings(maxRecords: 500, theme: "Light");
        await repository.ApplyAsync(new HistoryMutation(originalRecord, [], ["original.png"], [], originalSettings), CancellationToken.None);

        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER abort_legacy_pending_insert
                BEFORE INSERT ON pending_image_deletes
                WHEN NEW.file_name = 'abort-import.png'
                BEGIN
                    SELECT RAISE(ABORT, 'injected legacy import failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var importedRecord = TextRecord("must not remain after failed import");
        await Assert.ThrowsAsync<SqliteException>(() => repository.ImportLegacyAsync(
            "legacy-atomic",
            [importedRecord],
            Settings(maxRecords: 900, theme: "Dark"),
            ["abort-import.png"],
            CancellationToken.None));

        var page = await repository.SearchAsync(new HistoryQuery("", HistoryKindFilter.All, false, 0, 20), CancellationToken.None);
        Assert.Equal([originalRecord.Id], page.Records.Select(item => item.Id));
        Assert.Equal(originalSettings, await repository.LoadSettingsAsync(CancellationToken.None));
        Assert.Equal(["original.png"], await repository.LoadPendingImageDeletesAsync(CancellationToken.None));
        Assert.False(await repository.HasImportedMigrationAsync("legacy-atomic", CancellationToken.None));
    }

    [Fact]
    public async Task FindDuplicateAsync_uses_content_kind_and_hash()
    {
        await using var database = new SqliteTestDatabase();
        var repository = new SqliteHistoryRepository(database.ConnectionFactory);
        await repository.InitializeAsync(CancellationToken.None);
        var text = TextRecord("duplicate text") with { ContentHash = "same-hash" };
        var image = new ClipboardRecord(Guid.NewGuid(), ClipboardContentKind.Image, null, "image.png", 1, 1, 1,
            "same-hash", DateTime.UtcNow, false);
        await repository.ApplyAsync(new HistoryMutation(text, [], [], [], null), CancellationToken.None);
        await repository.ApplyAsync(new HistoryMutation(image, [], [], [], null), CancellationToken.None);

        var duplicate = await repository.FindDuplicateAsync(ClipboardContentKind.Text, "same-hash", CancellationToken.None);
        var missing = await repository.FindDuplicateAsync(ClipboardContentKind.Text, "missing", CancellationToken.None);

        Assert.Equal(text.Id, duplicate?.Id);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Schema_migration_runner_implements_the_core_migrator_contract()
    {
        await using var database = new SqliteTestDatabase();
        IDatabaseSchemaMigrator migrator = new SqliteSchemaMigrationRunner(database.ConnectionFactory);

        await migrator.MigrateAsync(CancellationToken.None);

        await using var connection = await database.ConnectionFactory.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(DatabaseSchema.CurrentVersion, await ScalarIntAsync(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task Search_applies_kind_favorite_paging_total_count_and_stable_order()
    {
        await using var database = new SqliteTestDatabase();
        var repository = new SqliteHistoryRepository(database.ConnectionFactory);
        await repository.InitializeAsync(CancellationToken.None);
        var sameTick = DateTime.UnixEpoch.AddTicks(99).ToUniversalTime();
        var textA = TextRecord("match alpha", copiedAtUtc: sameTick, id: Guid.Parse("00000000-0000-0000-0000-000000000002"), favorite: true);
        var textB = TextRecord("MATCH beta", copiedAtUtc: sameTick, id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var image = ImageRecord("match image", copiedAtUtc: sameTick.AddTicks(1));
        foreach (var item in new[] { textA, textB, image })
            await repository.ApplyAsync(new HistoryMutation(item, [], [], [], null), CancellationToken.None);

        var page = await repository.SearchAsync(
            new HistoryQuery("", HistoryKindFilter.Text, false, 0, 1),
            CancellationToken.None);
        var favorites = await repository.SearchAsync(
            new HistoryQuery("", HistoryKindFilter.All, true, 0, 20),
            CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(textB.Id, Assert.Single(page.Records).Id);
        Assert.Equal(0, page.Offset);
        Assert.Equal(1, page.Limit);
        Assert.Equal([textA.Id], favorites.Records.Select(item => item.Id));
        Assert.Equal(1, favorites.TotalCount);
    }

    internal static ClipboardRecord TextRecord(
        string text,
        DateTime? copiedAtUtc = null,
        Guid? id = null,
        bool favorite = false)
        => new(id ?? Guid.NewGuid(), ClipboardContentKind.Text, text, null,
            System.Text.Encoding.UTF8.GetByteCount(text), 0, 0, new string('a', 64),
            copiedAtUtc ?? DateTime.UtcNow, favorite);

    private static ClipboardRecord ImageRecord(string fileName, DateTime? copiedAtUtc = null)
        => new(Guid.NewGuid(), ClipboardContentKind.Image, null, fileName, 42, 4, 5, new string('b', 64),
            copiedAtUtc ?? DateTime.UtcNow, false);

    private static AppSettings Settings(int maxRecords, string theme)
        => new(maxRecords, 512, false, true, false, true, theme);

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<List<string>> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }
}

internal sealed class SqliteTestDatabase : IAsyncDisposable
{
    private readonly string _directoryPath;

    public SqliteTestDatabase()
    {
        _directoryPath = Path.Combine(Path.GetTempPath(), "ClipboardManager.Storage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directoryPath);
        DatabasePath = Path.Combine(_directoryPath, "history.db");
        ConnectionFactory = new SqliteConnectionFactory(DatabasePath);
    }

    public string DatabasePath { get; }
    public SqliteConnectionFactory ConnectionFactory { get; }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directoryPath)) Directory.Delete(_directoryPath, recursive: true);
        return ValueTask.CompletedTask;
    }
}
