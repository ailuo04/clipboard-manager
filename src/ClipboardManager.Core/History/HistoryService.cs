using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.Settings;

namespace ClipboardManager.Core.History;

public sealed class HistoryService : IHistoryService
{
    private const int MaxSearchTextLength = 1024;
    private const int MaxPageSize = 500;
    private const long MaxImagePixels = 40_000_000;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private readonly IHistoryRepository _repository;
    private readonly IImageStore _imageStore;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public HistoryService(IHistoryRepository repository, IImageStore imageStore)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _imageStore = imageStore ?? throw new ArgumentNullException(nameof(imageStore));
    }

    public Task<HistoryPage> SearchAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        string searchText = query.SearchText ?? string.Empty;
        if (searchText.Length > MaxSearchTextLength)
            throw new ArgumentOutOfRangeException(nameof(query), $"Search text cannot exceed {MaxSearchTextLength} characters.");
        if (query.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Offset cannot be negative.");
        if (query.Limit is < 1 or > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(query), $"Page size must be between 1 and {MaxPageSize}.");
        if (!Enum.IsDefined(query.Kind))
            throw new ArgumentOutOfRangeException(nameof(query), "History kind filter is invalid.");
        return _repository.SearchAsync(query with { SearchText = searchText }, cancellationToken);
    }

    public async Task<HistoryChangeResult> AddAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PreparedSnapshot prepared = await Task.Run(() => Prepare(snapshot), cancellationToken).ConfigureAwait(false);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings settings = (await _repository.LoadSettingsAsync(cancellationToken).ConfigureAwait(false)).Normalize();
            List<ClipboardRecord> existingRecords = await ReadAllRecordsAsync(cancellationToken).ConfigureAwait(false);
            ClipboardRecord? duplicate = await _repository.FindDuplicateAsync(prepared.Kind, prepared.ContentHash, cancellationToken).ConfigureAwait(false);
            ClipboardRecord record = duplicate is not null
                ? duplicate with { CopiedAtUtc = snapshot.CapturedAtUtc }
                : new ClipboardRecord(Guid.NewGuid(), prepared.Kind, prepared.Text, null, prepared.ByteLength, prepared.Width,
                    prepared.Height, prepared.ContentHash, snapshot.CapturedAtUtc, false);

            var nextRecords = existingRecords.Where(item => item.Id != record.Id).Append(record).ToList();
            RetentionResult retention = RetentionPolicy.Trim(nextRecords, settings);
            Guid[] deleteIds = retention.DeleteIds.ToArray();
            bool recordRetained = !deleteIds.Contains(record.Id);
            string? imageFileName = null;
            bool savedNewImage = false;
            bool restoredMissingDuplicateImage = false;
            bool needsImageWrite = false;
            if (prepared.PngBytes is not null)
            {
                if (duplicate is null)
                {
                    needsImageWrite = true;
                    savedNewImage = true;
                    imageFileName = $"{record.Id:N}.png";
                }
                else
                {
                    string? duplicateImageFileName = duplicate.ImageFileName;
                    if (!IsSafeImageFileName(duplicateImageFileName) || !string.Equals(duplicateImageFileName, $"{duplicate.Id:N}.png", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Duplicate image record has an invalid image file name.");
                    imageFileName = duplicateImageFileName!;
                    needsImageWrite = !File.Exists(_imageStore.GetPath(imageFileName));
                    restoredMissingDuplicateImage = needsImageWrite;
                }
            }

            if (needsImageWrite)
            {
                string safeImageFileName = imageFileName!;
                record = record with { ImageFileName = imageFileName };
                nextRecords[nextRecords.FindIndex(item => item.Id == record.Id)] = record;
                try
                {
                    await _imageStore.SavePngAsync(record.Id, prepared.PngBytes, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    try { await _imageStore.DeleteAsync(safeImageFileName, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* Keep the original save failure; a partial image has no committed record. */ }
                    throw;
                }
            }

            ClipboardRecord[] removedRecords = existingRecords.Concat([record])
                .Where(item => deleteIds.Contains(item.Id)).DistinctBy(item => item.Id).ToArray();
            string[] pendingDeletes = ImageNames(removedRecords);
            try
            {
                await _repository.ApplyAsync(new HistoryMutation(record, deleteIds, pendingDeletes, [], settings), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (savedNewImage && !restoredMissingDuplicateImage && imageFileName is not null)
                {
                    try { await _imageStore.DeleteAsync(imageFileName, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* Keep the repository failure as the primary error. */ }
                }
                throw;
            }

            string? cleanupWarning = await CleanupPendingAsync(cancellationToken).ConfigureAwait(false);
            string? warning = JoinWarnings(cleanupWarning, retention.CapacityWarning);
            if (!recordRetained) warning = JoinWarnings(warning, "当前容量不足，新记录未被保留；收藏记录不会被自动清理。");
            return new HistoryChangeResult(recordRetained ? [record] : [], deleteIds, warning);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<HistoryChangeResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadAllRecordsAsync(cancellationToken).ConfigureAwait(false);
            ClipboardRecord? target = records.FirstOrDefault(record => record.Id == id);
            if (target is null)
                return new HistoryChangeResult([], [], await CleanupPendingAsync(cancellationToken).ConfigureAwait(false));
            await _repository.ApplyAsync(new HistoryMutation(null, [id], ImageNames([target]), [], null), cancellationToken).ConfigureAwait(false);
            string? warning = await CleanupPendingAsync(cancellationToken).ConfigureAwait(false);
            return new HistoryChangeResult([], [id], warning);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<HistoryChangeResult> ClearAsync(CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadAllRecordsAsync(cancellationToken).ConfigureAwait(false);
            Guid[] deleteIds = records.Select(record => record.Id).ToArray();
            await _repository.ApplyAsync(new HistoryMutation(null, deleteIds, ImageNames(records), [], null), cancellationToken).ConfigureAwait(false);
            string? warning = await CleanupPendingAsync(cancellationToken).ConfigureAwait(false);
            return new HistoryChangeResult([], deleteIds, warning);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<HistoryChangeResult> SetFavoriteAsync(Guid id, bool isFavorite, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingRecords = await ReadAllRecordsAsync(cancellationToken).ConfigureAwait(false);
            ClipboardRecord? existing = existingRecords.FirstOrDefault(record => record.Id == id);
            if (existing is null)
                return new HistoryChangeResult([], [], await CleanupPendingAsync(cancellationToken).ConfigureAwait(false));

            AppSettings settings = (await _repository.LoadSettingsAsync(cancellationToken).ConfigureAwait(false)).Normalize();
            ClipboardRecord updated = existing with { IsFavorite = isFavorite };
            var nextRecords = existingRecords.Select(record => record.Id == id ? updated : record).ToList();
            RetentionResult retention = RetentionPolicy.Trim(nextRecords, settings);
            Guid[] deleteIds = retention.DeleteIds.ToArray();
            ClipboardRecord[] removedRecords = nextRecords.Where(record => deleteIds.Contains(record.Id)).ToArray();
            await _repository.ApplyAsync(new HistoryMutation(updated, deleteIds, ImageNames(removedRecords), [], null), cancellationToken).ConfigureAwait(false);

            string? warning = JoinWarnings(await CleanupPendingAsync(cancellationToken).ConfigureAwait(false), retention.CapacityWarning);
            return new HistoryChangeResult(deleteIds.Contains(id) ? [] : [updated], deleteIds, warning);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken)
        => (await _repository.LoadSettingsAsync(cancellationToken).ConfigureAwait(false)).Normalize();

    public async Task<HistoryChangeResult> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings normalized = settings.Normalize();
            var records = await ReadAllRecordsAsync(cancellationToken).ConfigureAwait(false);
            RetentionResult retention = RetentionPolicy.Trim(records, normalized);
            Guid[] deleteIds = retention.DeleteIds.ToArray();
            ClipboardRecord[] removedRecords = records.Where(record => deleteIds.Contains(record.Id)).ToArray();
            await _repository.ApplyAsync(new HistoryMutation(null, deleteIds, ImageNames(removedRecords), [], normalized), cancellationToken).ConfigureAwait(false);

            string? warning = JoinWarnings(await CleanupPendingAsync(cancellationToken).ConfigureAwait(false), retention.CapacityWarning);
            return new HistoryChangeResult([], deleteIds, warning);
        }
        finally { _mutationGate.Release(); }
    }

    private async Task<List<ClipboardRecord>> ReadAllRecordsAsync(CancellationToken cancellationToken)
    {
        var page = await _repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, int.MaxValue), cancellationToken).ConfigureAwait(false);
        return page.Records.ToList();
    }

    private async Task<string?> CleanupPendingAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> pending = await _repository.LoadPendingImageDeletesAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0) return null;

        var activeImages = (await ReadAllRecordsAsync(cancellationToken).ConfigureAwait(false))
            .Where(record => record.Kind == ClipboardContentKind.Image && record.ImageFileName is not null)
            .Select(record => record.ImageFileName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completed = new List<string>();
        int failures = 0;
        foreach (string fileName in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeImageFileName(fileName) || activeImages.Contains(fileName))
            {
                failures++;
                continue;
            }
            try
            {
                await _imageStore.DeleteAsync(fileName, cancellationToken).ConfigureAwait(false);
                completed.Add(fileName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { failures++; }
        }

        if (completed.Count > 0)
        {
            try
            {
                await _repository.ApplyAsync(new HistoryMutation(null, [], [], completed, null), cancellationToken).ConfigureAwait(false);
            }
            catch { failures += completed.Count; }
        }
        return failures == 0 ? null : $"{failures} 个图片文件暂时无法删除，已保留在重试队列。";
    }

    private static PreparedSnapshot Prepare(ClipboardSnapshot snapshot)
    {
        if (snapshot.Kind == ClipboardContentKind.Text)
        {
            if (string.IsNullOrEmpty(snapshot.Text)) throw new InvalidDataException("Text clipboard content is empty.");
            byte[] bytes = Encoding.UTF8.GetBytes(snapshot.Text);
            return new PreparedSnapshot(snapshot.Kind, snapshot.Text, null, bytes.LongLength, 0, 0,
                Convert.ToHexString(SHA256.HashData(bytes)));
        }
        if (snapshot.Kind != ClipboardContentKind.Image)
            throw new InvalidDataException("Clipboard content kind is invalid.");

        byte[] png = snapshot.PngBytes.ToArray();
        (int width, int height) = ReadPngDimensions(png);
        if (snapshot.ImageWidth != 0 || snapshot.ImageHeight != 0)
        {
            if (snapshot.ImageWidth <= 0 || snapshot.ImageHeight <= 0
                || snapshot.ImageWidth != width || snapshot.ImageHeight != height)
                throw new InvalidDataException("Image codec dimensions do not match the PNG header.");
        }
        string imageHash = snapshot.ImageContentHash is null
            ? Convert.ToHexString(SHA256.HashData(png))
            : NormalizeHash(snapshot.ImageContentHash);
        return new PreparedSnapshot(snapshot.Kind, null, png, png.LongLength, width, height,
            imageHash);
    }

    private static string NormalizeHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Canonical image content hash must be a 64-character SHA256 value.");
        return hash.ToUpperInvariant();
    }

    private static (int Width, int Height) ReadPngDimensions(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33 || !png[..8].SequenceEqual(PngSignature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(png.Slice(8, 4)) != 13)
            throw new InvalidDataException("Image clipboard data is not a valid PNG header.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue || (long)width * height > MaxImagePixels)
            throw new InvalidDataException("Image dimensions are invalid or exceed the 40-megapixel limit.");
        return ((int)width, (int)height);
    }

    private static string[] ImageNames(IEnumerable<ClipboardRecord> records)
        => records.Where(record => record.Kind == ClipboardContentKind.Image
                && IsSafeImageFileName(record.ImageFileName)
                && string.Equals(record.ImageFileName, $"{record.Id:N}.png", StringComparison.OrdinalIgnoreCase))
            .Select(record => record.ImageFileName!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsSafeImageFileName(string? fileName)
        => fileName is { Length: 36 } && fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(fileName[..32], "N", out _);

    private static string? JoinWarnings(string? first, string? second)
        => string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : first + " " + second;

    private sealed record PreparedSnapshot(ClipboardContentKind Kind, string? Text, byte[]? PngBytes,
        long ByteLength, int Width, int Height, string ContentHash);
}
