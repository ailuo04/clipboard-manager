using System.Security.Cryptography;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Storage.Migration;
using ClipboardManager.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClipboardManager.Storage.Tests;

public sealed class LegacyMigrationTests
{
    private static readonly string PngFixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-image.png");

    [Fact]
    public async Task RunAsync_imports_records_favorites_settings_and_preserves_sources()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string databasePath = Path.Combine(fixture.Root, "new", "history.db");
        var repository = new SqliteHistoryRepository(databasePath);

        await fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None);

        var page = await repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, 10), CancellationToken.None);
        Assert.Equal(2, page.TotalCount);
        var textRecord = page.Records.Single(record => record.Text is not null);
        var imageRecord = page.Records.Single(record => record.Kind == ClipboardManager.Core.History.ClipboardContentKind.Image);
        Assert.Equal("favorite legacy text", textRecord.Text);
        Assert.Equal("FB5523BF9758A8DDF29FA28A142B6FB5024910EC9CC520494B0B79EEF6AFD03C", textRecord.ContentHash);
        Assert.Equal(20, textRecord.ByteLength);
        Assert.True(textRecord.IsFavorite);
        Assert.Equal("4FF6AB670A58C14270E034E2090D9A432CAA263A14E0A25785386B0C12F880B5", imageRecord.ContentHash);
        Assert.Equal("00000000000000000000000000000012.png", imageRecord.ImageFileName);
        Assert.Equal(70, imageRecord.ByteLength);
        Assert.Equal(1, imageRecord.ImageWidth);
        Assert.Equal(1, imageRecord.ImageHeight);
        Assert.Equal([imageRecord.Id, textRecord.Id], page.Records.Select(record => record.Id));
        Assert.Equal(new AppSettings(321, 256, true, false, true, false, "Dark"), await repository.LoadSettingsAsync(CancellationToken.None));
        Assert.Equal(["00000000000000000000000000000013.png"], await repository.LoadPendingImageDeletesAsync(CancellationToken.None));
        Assert.True(File.Exists(fixture.CompletionPath));
        Assert.Equal(fixture.SourceHashes, fixture.HashSources());
        string backupDirectory = Directory.GetDirectories(fixture.BackupRoot).Single();
        foreach (var source in fixture.SourceHashes)
        {
            string relative = source.Key[("legacy" + Path.DirectorySeparatorChar).Length..];
            string backedUp = Path.Combine(backupDirectory, relative);
            Assert.Equal(source.Value, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(backedUp))));
        }

        await File.WriteAllTextAsync(fixture.HistoryPath, "not JSON; a valid completion marker must avoid rereading it");
        await fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None);
        string marker = await File.ReadAllTextAsync(fixture.CompletionPath);
        await File.WriteAllTextAsync(fixture.CompletionPath, marker.Replace(
            "00000000-0000-0000-0000-000000000012", "00000000-0000-0000-0000-000000000011", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));
    }

    [Fact]
    public void ComputeMigrationId_is_stable_and_independent_of_input_order()
    {
        KeyValuePair<string, string>[] sources =
        [
            new("history.json", "ABCD"),
            new("images/a.png", "1234"),
            new("theme.json", "EF01")
        ];

        Assert.Equal(LegacyMigrationCoordinator.ComputeMigrationId(sources),
            LegacyMigrationCoordinator.ComputeMigrationId(sources.Reverse()));
    }

    [Fact]
    public async Task RunAsync_recovers_commit_before_completion_marker_without_duplicate_import()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string databasePath = Path.Combine(fixture.Root, "new", "history.db");
        var repository = new SqliteHistoryRepository(databasePath);
        var interrupted = new LegacyMigrationCoordinator(fixture.HistoryPath, fixture.ThemePath, fixture.ImagesPath,
            fixture.BackupRoot, fixture.CompletionPath, repository, stage =>
            {
                if (stage == MigrationStage.AfterImport) throw new IOException("simulated process interruption");
            });

        await Assert.ThrowsAsync<IOException>(() => interrupted.RunAsync(CancellationToken.None));
        Assert.False(File.Exists(fixture.CompletionPath));
        await fixture.CreateCoordinator(new SqliteHistoryRepository(databasePath)).RunAsync(CancellationToken.None);

        Assert.True(File.Exists(fixture.CompletionPath));
        Assert.Equal(2, (await repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, 10), CancellationToken.None)).TotalCount);
    }

    [Fact]
    public async Task RunAsync_accepts_a_valid_completion_marker_after_user_deletes_migrated_records()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string databasePath = Path.Combine(fixture.Root, "new", "history.db");
        var repository = new SqliteHistoryRepository(databasePath);
        await fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None);
        var page = await repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, 10), CancellationToken.None);
        await repository.ApplyAsync(new HistoryMutation(null, page.Records.Select(record => record.Id).ToArray(), [], [], null), CancellationToken.None);
        await File.WriteAllTextAsync(fixture.HistoryPath, "legacy JSON should not be read after successful migration");

        await fixture.CreateCoordinator(new SqliteHistoryRepository(databasePath)).RunAsync(CancellationToken.None);

        Assert.Equal(0, (await repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, 10), CancellationToken.None)).TotalCount);
    }

    [Fact]
    public async Task RunAsync_rolls_back_failed_import_preserves_sources_and_retries_once()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        var originalHashes = fixture.HashSources();
        string databasePath = Path.Combine(fixture.Root, "new", "history.db");
        var repository = new SqliteHistoryRepository(databasePath);
        await repository.InitializeAsync(CancellationToken.None);
        var connectionFactory = new SqliteConnectionFactory(databasePath);
        await using (var connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER abort_legacy_migration
                BEFORE INSERT ON pending_image_deletes
                WHEN NEW.file_name = '00000000000000000000000000000013.png'
                BEGIN SELECT RAISE(ABORT, 'injected migration failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));

        Assert.Equal(0, (await repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, 10), CancellationToken.None)).TotalCount);
        Assert.False(await repository.HasImportedMigrationAsync(Path.GetFileName(Directory.GetDirectories(fixture.BackupRoot).Single()), CancellationToken.None));
        Assert.False(File.Exists(fixture.CompletionPath));
        Assert.Equal(originalHashes, fixture.HashSources());

        await using (var connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER abort_legacy_migration;";
            await command.ExecuteNonQueryAsync();
        }
        await fixture.CreateCoordinator(new SqliteHistoryRepository(databasePath)).RunAsync(CancellationToken.None);

        Assert.True(await repository.HasImportedMigrationAsync(Path.GetFileName(Directory.GetDirectories(fixture.BackupRoot).Single()), CancellationToken.None));
        Assert.Equal(2, (await repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, 10), CancellationToken.None)).TotalCount);
    }

    [Fact]
    public async Task Missing_referenced_image_aborts_without_completion_marker()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs(includeImage: false);
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));

        Assert.False(File.Exists(fixture.CompletionPath));
        Assert.Equal(fixture.SourceHashes, fixture.HashSources());
    }

    [Theory]
    [InlineData(MigrationStage.BackupCompleted)]
    [InlineData(MigrationStage.SchemaCreated)]
    [InlineData(MigrationStage.BeforeImport)]
    [InlineData(MigrationStage.AfterImport)]
    [InlineData(MigrationStage.AfterValidation)]
    [InlineData(MigrationStage.BeforeCompletionMarker)]
    public async Task RunAsync_recovers_after_each_injected_stage(MigrationStage stopAt)
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string databasePath = Path.Combine(fixture.Root, "new", "history.db");
        var interrupted = new LegacyMigrationCoordinator(fixture.HistoryPath, fixture.ThemePath, fixture.ImagesPath,
            fixture.BackupRoot, fixture.CompletionPath, new SqliteHistoryRepository(databasePath), stage =>
            {
                if (stage == stopAt) throw new IOException("simulated interruption");
            });

        await Assert.ThrowsAsync<IOException>(() => interrupted.RunAsync(CancellationToken.None));
        await fixture.CreateCoordinator(new SqliteHistoryRepository(databasePath)).RunAsync(CancellationToken.None);

        Assert.True(File.Exists(fixture.CompletionPath));
        Assert.Equal(2, (await new SqliteHistoryRepository(databasePath).SearchAsync(
            new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, 10), CancellationToken.None)).TotalCount);
    }

    [Fact]
    public async Task RunAsync_does_not_complete_corrupt_json()
    {
        using var fixture = new MigrationFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.HistoryPath)!);
        await File.WriteAllTextAsync(fixture.HistoryPath, "{ invalid json");
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));

        Assert.False(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task RunAsync_rejects_duplicate_record_ids_without_marking_complete()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string json = await File.ReadAllTextAsync(fixture.HistoryPath);
        await File.WriteAllTextAsync(fixture.HistoryPath, json.Replace("00000000-0000-0000-0000-000000000012", "00000000-0000-0000-0000-000000000011", StringComparison.Ordinal));
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));

        Assert.False(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task RunAsync_rejects_an_unsafe_image_reference_without_creating_files_outside_image_directory()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string json = await File.ReadAllTextAsync(fixture.HistoryPath);
        await File.WriteAllTextAsync(fixture.HistoryPath, json.Replace(
            "00000000000000000000000000000012.png", "..\\escape.png", StringComparison.Ordinal));
        string escapedPath = Path.Combine(fixture.Root, "legacy", "escape.png");
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));

        Assert.False(File.Exists(escapedPath));
        Assert.False(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task RunAsync_does_not_queue_a_pending_delete_for_an_image_still_referenced_by_a_record()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string json = await File.ReadAllTextAsync(fixture.HistoryPath);
        await File.WriteAllTextAsync(fixture.HistoryPath, json.Replace(
            "00000000000000000000000000000013.png", "00000000000000000000000000000012.png", StringComparison.Ordinal));
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None);

        Assert.Empty(await repository.LoadPendingImageDeletesAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(fixture.ImagesPath, "00000000000000000000000000000012.png")));
    }

    [Fact]
    public async Task RunAsync_stops_for_an_invalid_png_without_changing_legacy_files()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string imagePath = Path.Combine(fixture.ImagesPath, "00000000000000000000000000000012.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4]);
        var hashes = fixture.HashSources();
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));

        Assert.False(File.Exists(fixture.CompletionPath));
        Assert.Equal(hashes, fixture.HashSources());
    }

    [Fact]
    public async Task RunAsync_rejects_truncated_png_with_valid_signature_and_ihdr_tag()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string imagePath = Path.Combine(fixture.ImagesPath, "00000000000000000000000000000012.png");
        byte[] bytes = await File.ReadAllBytesAsync(PngFixturePath);
        await File.WriteAllBytesAsync(imagePath, bytes[..33]);
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));

        Assert.False(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task RunAsync_rejects_png_with_corrupt_chunk_crc()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string imagePath = Path.Combine(fixture.ImagesPath, "00000000000000000000000000000012.png");
        byte[] bytes = await File.ReadAllBytesAsync(PngFixturePath);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(imagePath, bytes);
        var hashes = fixture.HashSources();
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));

        Assert.False(File.Exists(fixture.CompletionPath));
        Assert.Equal(hashes, fixture.HashSources());
    }

    [Fact]
    public async Task RunAsync_rejects_transparency_chunk_after_image_data()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string imagePath = Path.Combine(fixture.ImagesPath, "00000000000000000000000000000012.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "png-trns-after-idat.png"), imagePath, overwrite: true);
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));

        Assert.False(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task RunAsync_accepts_enum_names_in_legacy_json()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string json = await File.ReadAllTextAsync(fixture.HistoryPath);
        await File.WriteAllTextAsync(fixture.HistoryPath, json.Replace("\"kind\": 1", "\"kind\": \"Image\"", StringComparison.Ordinal));
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None);

        Assert.Equal(2, (await repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, 10), CancellationToken.None)).TotalCount);
    }

    [Fact]
    public async Task RunAsync_treats_offsetless_legacy_utc_timestamps_as_utc()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string json = await File.ReadAllTextAsync(fixture.HistoryPath);
        await File.WriteAllTextAsync(fixture.HistoryPath, json.Replace("2025-01-02T03:04:05Z", "2025-01-02T03:04:05", StringComparison.Ordinal));
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None);

        var page = await repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.Text, false, 0, 10), CancellationToken.None);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), Assert.Single(page.Records).CopiedAtUtc);
    }

    [Fact]
    public async Task RunAsync_initializes_empty_database_when_history_is_missing()
    {
        using var fixture = new MigrationFixture();
        string databasePath = Path.Combine(fixture.Root, "new", "history.db");
        var repository = new SqliteHistoryRepository(databasePath);

        await fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None);

        Assert.True(File.Exists(databasePath));
        Assert.Equal(0, (await repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, 10), CancellationToken.None)).TotalCount);
        Assert.False(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task RunAsync_does_not_overwrite_an_unrecognized_database_when_history_is_missing()
    {
        using var fixture = new MigrationFixture();
        string databasePath = Path.Combine(fixture.Root, "new", "history.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        byte[] original = [0x43, 0x4D, 0x2D, 0x44, 0x42, 0x2D, 0x55, 0x4E, 0x4B];
        await File.WriteAllBytesAsync(databasePath, original);
        var repository = new SqliteHistoryRepository(databasePath);

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(databasePath));
        Assert.False(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task RunAsync_uses_light_theme_fallback_when_legacy_theme_json_is_invalid()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        await File.WriteAllTextAsync(fixture.ThemePath, "{ invalid");
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None);

        Assert.Equal("Light", (await repository.LoadSettingsAsync(CancellationToken.None)).ThemePreference);
        Assert.True(File.Exists(Path.Combine(Directory.GetDirectories(fixture.BackupRoot).Single(), "theme.json")));
    }

    [Fact]
    public async Task RunAsync_uses_defaults_when_legacy_settings_are_missing_fields()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateLegacyInputs();
        string json = await File.ReadAllTextAsync(fixture.HistoryPath);
        int settingsStart = json.IndexOf("\"settings\"", StringComparison.Ordinal);
        int pendingStart = json.IndexOf("\"pendingImageDeletes\"", settingsStart, StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.HistoryPath, json[..settingsStart] + "\"settings\": {},\n  " + json[pendingStart..]);
        var repository = new SqliteHistoryRepository(Path.Combine(fixture.Root, "new", "history.db"));

        await fixture.CreateCoordinator(repository).RunAsync(CancellationToken.None);

        var settings = await repository.LoadSettingsAsync(CancellationToken.None);
        Assert.Equal(500, settings.MaxRecords);
        Assert.Equal(512, settings.MaxStorageMb);
        Assert.Equal("Dark", settings.ThemePreference);
    }

    private sealed class MigrationFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cm-migration-tests", Guid.NewGuid().ToString("N"));
        public string HistoryPath => Path.Combine(Root, "legacy", "history.json");
        public string ThemePath => Path.Combine(Root, "legacy", "theme.json");
        public string ImagesPath => Path.Combine(Root, "legacy", "images");
        public string BackupRoot => Path.Combine(Root, "backup");
        public string CompletionPath => Path.Combine(Root, "new", "migration.json");
        public IReadOnlyDictionary<string, string> SourceHashes { get; private set; } = new Dictionary<string, string>();

        public LegacyMigrationCoordinator CreateCoordinator(ClipboardManager.Core.History.IHistoryRepository repository)
            => new(HistoryPath, ThemePath, ImagesPath, BackupRoot, CompletionPath, repository);

        public void CreateLegacyInputs(bool includeImage = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            Directory.CreateDirectory(ImagesPath);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-history.json"), HistoryPath);
            File.WriteAllText(ThemePath, "{\"theme\":\"Dark\"}");
            if (includeImage) File.Copy(PngFixturePath, Path.Combine(ImagesPath, "00000000000000000000000000000012.png"));
            SourceHashes = HashSources();
        }

        public IReadOnlyDictionary<string, string> HashSources()
        {
            var paths = new[] { HistoryPath, ThemePath }.Where(File.Exists)
                .Concat(Directory.Exists(ImagesPath) ? Directory.EnumerateFiles(ImagesPath, "*.png") : []);
            return paths.ToDictionary(path => Path.GetRelativePath(Root, path), path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

