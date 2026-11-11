using System.Security.Cryptography;
using System.Text;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using Xunit;

namespace ClipboardManager.Core.Tests;

public sealed class HistoryServiceTests
{
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task AddAsync_persists_text_and_image_metadata_and_deduplicates_by_content()
    {
        var repository = new FakeHistoryRepository();
        using var images = new FakeImageStore();
        var service = new HistoryService(repository, images);
        DateTime firstTime = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        DateTime secondTime = firstTime.AddMinutes(1);

        await service.AddAsync(new ClipboardSnapshot(ClipboardContentKind.Text, "hello", ReadOnlyMemory<byte>.Empty, firstTime), CancellationToken.None);
        await service.AddAsync(new ClipboardSnapshot(ClipboardContentKind.Text, "hello", ReadOnlyMemory<byte>.Empty, secondTime), CancellationToken.None);
        byte[] png = Convert.FromBase64String(OnePixelPng);
        await service.AddAsync(new ClipboardSnapshot(ClipboardContentKind.Image, null, png, secondTime), CancellationToken.None);
        DateTime imageDuplicateTime = secondTime.AddMinutes(1);
        await service.AddAsync(new ClipboardSnapshot(ClipboardContentKind.Image, null, png, imageDuplicateTime), CancellationToken.None);

        var text = Assert.Single(repository.Records, record => record.Kind == ClipboardContentKind.Text);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hello"))), text.ContentHash);
        Assert.Equal(secondTime, text.CopiedAtUtc);
        Assert.Equal(5, text.ByteLength);
        var image = Assert.Single(repository.Records, record => record.Kind == ClipboardContentKind.Image);
        Assert.Equal($"{image.Id:N}.png", image.ImageFileName);
        Assert.Equal(png.Length, image.ByteLength);
        Assert.Equal(1, image.ImageWidth);
        Assert.Equal(1, image.ImageHeight);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(png)), image.ContentHash);
        Assert.Equal(imageDuplicateTime, image.CopiedAtUtc);
        Assert.Equal(1, images.SaveCount);
        Assert.Single(images.Files);
    }

    [Fact]
    public async Task AddAsync_cleans_partial_image_when_image_store_save_fails()
    {
        var repository = new FakeHistoryRepository();
        using var images = new FakeImageStore { FailSaveAfterWriting = true };
        var service = new HistoryService(repository, images);

        await Assert.ThrowsAsync<IOException>(() => service.AddAsync(
            new ClipboardSnapshot(ClipboardContentKind.Image, null, Convert.FromBase64String(OnePixelPng), DateTime.UtcNow), CancellationToken.None));

        Assert.Empty(images.Files);
        Assert.Empty(repository.Records);
    }

    [Fact]
    public async Task AddAsync_deletes_a_new_image_when_repository_commit_fails()
    {
        var repository = new FakeHistoryRepository { FailNextApply = true };
        using var images = new FakeImageStore();
        var service = new HistoryService(repository, images);
        byte[] png = Convert.FromBase64String(OnePixelPng);

        await Assert.ThrowsAsync<IOException>(() => service.AddAsync(
            new ClipboardSnapshot(ClipboardContentKind.Image, null, png, DateTime.UtcNow), CancellationToken.None));

        Assert.Empty(repository.Records);
        Assert.Empty(images.Files);
    }

    [Fact]
    public async Task AddAsync_restores_a_missing_file_for_a_duplicate_image()
    {
        byte[] png = Convert.FromBase64String(OnePixelPng);
        Guid id = Guid.NewGuid();
        string hash = Convert.ToHexString(SHA256.HashData(png));
        var existing = new ClipboardRecord(id, ClipboardContentKind.Image, null, $"{id:N}.png", png.Length, 1, 1,
            hash, DateTime.UtcNow.AddMinutes(-1), false);
        var repository = new FakeHistoryRepository();
        repository.Records.Add(existing);
        using var images = new FakeImageStore();
        var service = new HistoryService(repository, images);

        await service.AddAsync(new ClipboardSnapshot(ClipboardContentKind.Image, null, png, DateTime.UtcNow), CancellationToken.None);

        Assert.True(images.Files.ContainsKey($"{id:N}.png"));
        Assert.Equal($"{id:N}.png", Assert.Single(repository.Records).ImageFileName);
    }

    [Fact]
    public async Task AddAsync_uses_canonical_pixel_hash_for_image_deduplication_across_png_encodings()
    {
        byte[] png = Convert.FromBase64String(OnePixelPng);
        byte[] alternatePngEncoding = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4AWP4z8DwHwAFAAH/e+m+7wAAAABJRU5ErkJggg==");
        const string pixelHash = "0E3AECC82B10453FED444E77469E02D8162846ACCDA693F65AEADFFBF6998BFE";
        var repository = new FakeHistoryRepository();
        using var images = new FakeImageStore();
        var service = new HistoryService(repository, images);
        DateTime firstTime = DateTime.UtcNow.AddMinutes(-1);
        DateTime secondTime = DateTime.UtcNow;

        await service.AddAsync(new ClipboardSnapshot(ClipboardContentKind.Image, null, png, firstTime)
        {
            ImageContentHash = pixelHash,
            ImageWidth = 1,
            ImageHeight = 1
        }, CancellationToken.None);
        await service.AddAsync(new ClipboardSnapshot(ClipboardContentKind.Image, null, alternatePngEncoding, secondTime)
        {
            ImageContentHash = pixelHash,
            ImageWidth = 1,
            ImageHeight = 1
        }, CancellationToken.None);

        var record = Assert.Single(repository.Records);
        Assert.Equal(pixelHash, record.ContentHash);
        Assert.Equal(secondTime, record.CopiedAtUtc);
        Assert.Equal(1, images.SaveCount);
    }

    [Fact]
    public async Task DeleteAsync_deletes_favorites_explicitly_and_queues_image_cleanup_until_success()
    {
        var repository = new FakeHistoryRepository();
        using var images = new FakeImageStore { FailDeletes = true };
        var service = new HistoryService(repository, images);
        var favorite = ImageRecord(favorite: true);
        repository.Records.Add(favorite);
        images.Files[$"{favorite.Id:N}.png"] = [1, 2, 3];

        var result = await service.DeleteAsync(favorite.Id, CancellationToken.None);

        Assert.Empty(repository.Records);
        Assert.Equal([favorite.Id], result.DeletedIds);
        Assert.Contains($"{favorite.Id:N}.png", repository.PendingDeletes);
        Assert.Contains("重试队列", result.Warning);
        Assert.Contains($"{favorite.Id:N}.png", images.Files.Keys);

        images.FailDeletes = false;
        await service.UpdateSettingsAsync(repository.Settings, CancellationToken.None);

        Assert.Empty(repository.PendingDeletes);
        Assert.Empty(images.Files);
    }

    [Fact]
    public async Task DeleteAsync_does_not_queue_an_image_name_that_does_not_match_its_record_id()
    {
        var repository = new FakeHistoryRepository();
        using var images = new FakeImageStore();
        var service = new HistoryService(repository, images);
        var record = ImageRecord() with { ImageFileName = $"{Guid.NewGuid():N}.png" };
        repository.Records.Add(record);

        await service.DeleteAsync(record.Id, CancellationToken.None);

        Assert.Empty(repository.PendingDeletes);
        Assert.Empty(images.Files);
    }

    [Fact]
    public async Task UpdateSettingsAsync_trims_oldest_non_favorite_but_never_auto_deletes_favorites()
    {
        var repository = new FakeHistoryRepository { Settings = DefaultSettings with { MaxRecords = 100 } };
        using var images = new FakeImageStore();
        var service = new HistoryService(repository, images);
        DateTime origin = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var favorite = TextRecord("favorite", origin, favorite: true);
        var oldestRegular = TextRecord("oldest", origin.AddSeconds(1));
        repository.Records.Add(favorite);
        repository.Records.Add(oldestRegular);
        for (int index = 2; index < 51; index++)
            repository.Records.Add(TextRecord($"record-{index}", origin.AddSeconds(index)));

        var result = await service.UpdateSettingsAsync(DefaultSettings with { MaxRecords = 50 }, CancellationToken.None);

        Assert.Equal(50, repository.Records.Count);
        Assert.Contains(favorite, repository.Records);
        Assert.DoesNotContain(oldestRegular, repository.Records);
        Assert.Equal([oldestRegular.Id], result.DeletedIds);
    }

    [Fact]
    public async Task UpdateSettingsAsync_trims_oldest_records_until_storage_budget_is_met()
    {
        var repository = new FakeHistoryRepository { Settings = DefaultSettings };
        using var images = new FakeImageStore();
        var service = new HistoryService(repository, images);
        DateTime origin = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldest = TextRecord("oldest", origin) with { ByteLength = 40L * 1024 * 1024 };
        var newer = TextRecord("newer", origin.AddSeconds(1)) with { ByteLength = 40L * 1024 * 1024 };
        repository.Records.AddRange([oldest, newer]);

        var result = await service.UpdateSettingsAsync(DefaultSettings with { MaxStorageMb = 64 }, CancellationToken.None);

        Assert.Equal([oldest.Id], result.DeletedIds);
        Assert.Equal([newer], repository.Records);
    }

    [Fact]
    public async Task ClearAsync_removes_all_records_including_favorites_and_cleans_images_after_commit()
    {
        var repository = new FakeHistoryRepository();
        using var images = new FakeImageStore();
        var service = new HistoryService(repository, images);
        var favoriteText = TextRecord("favorite", DateTime.UtcNow, favorite: true);
        var favoriteImage = ImageRecord(favorite: true);
        repository.Records.AddRange([favoriteText, favoriteImage]);
        images.Files[$"{favoriteImage.Id:N}.png"] = [9, 8, 7];

        var result = await service.ClearAsync(CancellationToken.None);

        Assert.Empty(repository.Records);
        Assert.Equal(2, result.DeletedIds.Count);
        Assert.Contains(favoriteText.Id, result.DeletedIds);
        Assert.Contains(favoriteImage.Id, result.DeletedIds);
        Assert.Empty(repository.PendingDeletes);
        Assert.Empty(images.Files);
    }

    [Fact]
    public async Task SetFavoriteAsync_changes_favorite_state_without_removing_the_record()
    {
        var repository = new FakeHistoryRepository();
        using var images = new FakeImageStore();
        var service = new HistoryService(repository, images);
        var record = TextRecord("toggle me", DateTime.UtcNow);
        repository.Records.Add(record);

        var favorited = await service.SetFavoriteAsync(record.Id, true, CancellationToken.None);
        var unfavorited = await service.SetFavoriteAsync(record.Id, false, CancellationToken.None);

        Assert.True(Assert.Single(favorited.Upserted).IsFavorite);
        Assert.False(Assert.Single(unfavorited.Upserted).IsFavorite);
        Assert.Equal(record.Id, Assert.Single(repository.Records).Id);
    }

    [Fact]
    public async Task SearchAsync_rejects_out_of_range_paging_and_unbounded_search_text()
    {
        using var images = new FakeImageStore();
        var service = new HistoryService(new FakeHistoryRepository(), images);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync(
            new HistoryQuery("", HistoryKindFilter.All, false, -1, 20), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync(
            new HistoryQuery("", HistoryKindFilter.All, false, 0, 501), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync(
            new HistoryQuery(new string('x', 1025), HistoryKindFilter.All, false, 0, 20), CancellationToken.None));
    }

    private static readonly AppSettings DefaultSettings = new(500, 512, false, true, false, true, "Light");

    private static ClipboardRecord TextRecord(string text, DateTime time, bool favorite = false)
        => new(Guid.NewGuid(), ClipboardContentKind.Text, text, null, Encoding.UTF8.GetByteCount(text), 0, 0,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), time, favorite);

    private static ClipboardRecord ImageRecord(bool favorite = false)
    {
        Guid id = Guid.NewGuid();
        return new ClipboardRecord(id, ClipboardContentKind.Image, null, $"{id:N}.png", 70, 1, 1,
            new string('A', 64), DateTime.UtcNow, favorite);
    }

    private sealed class FakeHistoryRepository : IHistoryRepository
    {
        public List<ClipboardRecord> Records { get; } = [];
        public HashSet<string> PendingDeletes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public AppSettings Settings { get; set; } = DefaultSettings;
        public bool FailNextApply { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<HistoryPage> SearchAsync(HistoryQuery query, CancellationToken cancellationToken)
        {
            var matches = Records.Where(record => query.Kind switch
                {
                    HistoryKindFilter.Text => record.Kind == ClipboardContentKind.Text,
                    HistoryKindFilter.Image => record.Kind == ClipboardContentKind.Image,
                    _ => true
                })
                .Where(record => !query.FavoritesOnly || record.IsFavorite)
                .Where(record => string.IsNullOrEmpty(query.SearchText) || (record.Text?.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(record => record.CopiedAtUtc.Ticks).ThenBy(record => record.Id).ToArray();
            return Task.FromResult(new HistoryPage(matches.Skip(query.Offset).Take(query.Limit).ToArray(), matches.Length, query.Offset, query.Limit));
        }

        public Task<ClipboardRecord?> FindDuplicateAsync(ClipboardContentKind kind, string contentHash, CancellationToken cancellationToken)
            => Task.FromResult(Records.FirstOrDefault(record => record.Kind == kind && record.ContentHash == contentHash));

        public Task ApplyAsync(HistoryMutation mutation, CancellationToken cancellationToken)
        {
            if (FailNextApply)
            {
                FailNextApply = false;
                throw new IOException("injected repository failure");
            }
            if (mutation.Upsert is not null)
            {
                Records.RemoveAll(record => record.Id == mutation.Upsert.Id);
                Records.Add(mutation.Upsert);
            }
            Records.RemoveAll(record => mutation.DeleteIds.Contains(record.Id));
            if (mutation.Settings is not null) Settings = mutation.Settings;
            PendingDeletes.UnionWith(mutation.PendingImageDeletes);
            PendingDeletes.ExceptWith(mutation.CompletedImageDeletes);
            return Task.CompletedTask;
        }

        public Task<bool> HasImportedMigrationAsync(string migrationId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task ImportLegacyAsync(string migrationId, IReadOnlyList<ClipboardRecord> records, AppSettings settings,
            IReadOnlyList<string> pendingImageDeletes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
        public Task<IReadOnlyList<string>> LoadPendingImageDeletesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(PendingDeletes.ToArray());
    }

    private sealed class FakeImageStore : IImageStore, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-core-image-tests", Guid.NewGuid().ToString("N"));
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FailDeletes { get; set; }
        public bool FailSaveAfterWriting { get; set; }
        public int SaveCount { get; private set; }

        public async Task SavePngAsync(Guid id, ReadOnlyMemory<byte> png, CancellationToken cancellationToken)
        {
            string fileName = $"{id:N}.png";
            Directory.CreateDirectory(_root);
            await File.WriteAllBytesAsync(GetPath(fileName), png.ToArray(), cancellationToken);
            Files[fileName] = png.ToArray();
            SaveCount++;
            if (FailSaveAfterWriting) throw new IOException("injected partial image write failure");
        }

        public Task DeleteAsync(string fileName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailDeletes) throw new IOException("injected file delete failure");
            Files.Remove(fileName);
            if (File.Exists(GetPath(fileName))) File.Delete(GetPath(fileName));
            return Task.CompletedTask;
        }

        public string GetPath(string fileName) => Path.Combine(_root, fileName);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
