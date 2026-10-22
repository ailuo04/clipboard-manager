using System.Security.Cryptography;
using System.Text;
using ClipboardManager.Models;
using ClipboardManager.Utilities;

namespace ClipboardManager.Services;

public sealed class HistoryService
{
    private readonly HistoryStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ClipboardRecord> _records = [];
    private readonly HashSet<string> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings = new();
    private bool _loaded;

    public HistoryService(HistoryStore store) => _store = store;
    public IReadOnlyList<ClipboardRecord> Records => _records.ToArray();
    public AppSettings Settings => _settings.Clone();
    public bool IsLoaded => _loaded;
    public string RootDirectory => _store.RootDirectory;
    public string GetImagePath(ClipboardRecord record) => _store.GetImagePath(record.ImageFileName ?? string.Empty);

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var valid = new List<ClipboardRecord>();
            int skipped = 0;
            foreach (var record in document.Records)
            {
                if (!IsValidRecord(record) || valid.Any(item => item.Id == record.Id)) { skipped++; continue; }
                if (record.Kind == ClipboardRecordKind.Image && !File.Exists(GetImagePath(record))) { skipped++; continue; }
                valid.Add(record);
            }
            _records = valid.OrderByDescending(record => record.CopiedAtUtc).ToList();
            _settings = document.Settings.Clone();
            _pendingDeletes.Clear();
            foreach (string name in document.PendingImageDeletes)
            {
                try
                {
                    _store.GetImagePath(name);
                    if (!_records.Any(record => record.ImageFileName == name)) _pendingDeletes.Add(name);
                }
                catch (InvalidDataException exception) { AppLogger.LogException("忽略不合法的待删除图片路径", exception); }
            }
            _loaded = true;
            // Only scan orphaned data after successfully reading a fully valid manifest.
            if (skipped == 0)
            {
                await Task.Run(() =>
                {
                    var referenced = _records.Select(record => record.ImageFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (string path in Directory.EnumerateFiles(_store.ImagesDirectory, "*.png"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string name = Path.GetFileName(path);
                        if (name.Length == 36 && Guid.TryParseExact(name[..32], "N", out _) && !referenced.Contains(name))
                            _pendingDeletes.Add(name);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            string? cleanupWarning = await CleanupPendingAsync().ConfigureAwait(false);
            return JoinWarnings(skipped == 0 ? null : $"有 {skipped} 条记录格式异常或图片文件缺失，已跳过；原文件未被覆盖。", cleanupWarning);
        }
        finally { _gate.Release(); }
    }

    public async Task<HistoryChangeResult> AddAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        byte[]? imageBytes = null;
        string hash;
        int width = 0, height = 0;
        if (snapshot.Kind == ClipboardRecordKind.Image)
        {
            var prepared = await Task.Run(() => ImageProcessor.Prepare(snapshot.Image ?? throw new InvalidDataException("图片数据为空。")), cancellationToken).ConfigureAwait(false);
            imageBytes = prepared.Bytes;
            hash = prepared.Hash;
            width = prepared.Width;
            height = prepared.Height;
        }
        else
        {
            if (string.IsNullOrEmpty(snapshot.Text)) throw new InvalidDataException("文本数据为空。");
            hash = await Task.Run(() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Text))), cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var existing = _records.FirstOrDefault(record => record.Kind == snapshot.Kind && record.ContentHash == hash);
            var record = existing is null ? new ClipboardRecord
            {
                Kind = snapshot.Kind, Text = snapshot.Text, ContentHash = hash,
                CopiedAtUtc = snapshot.CapturedAtUtc,
                ImageWidth = width, ImageHeight = height,
                ByteLength = imageBytes?.LongLength ?? Encoding.UTF8.GetByteCount(snapshot.Text!)
            } : existing with { CopiedAtUtc = snapshot.CapturedAtUtc };
            var next = _records.Where(item => item.Id != record.Id).Prepend(record).ToList();
            string? createdFile = null;
            try
            {
                if (imageBytes is not null && (existing is null || !File.Exists(GetImagePath(existing))))
                {
                    record = record with { ImageFileName = $"{record.Id:N}.png" };
                    next[0] = record;
                    createdFile = GetImagePath(record);
                    await Task.Run(() => Directory.CreateDirectory(_store.ImagesDirectory), cancellationToken).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(createdFile, imageBytes, cancellationToken).ConfigureAwait(false);
                }
                var removed = ApplyRetention(next, _settings);
                await CommitAsync(next, _settings, removed, cancellationToken).ConfigureAwait(false);
                createdFile = null;
                string? warning = await CleanupPendingAsync().ConfigureAwait(false);
                bool kept = next.Any(item => item.Id == record.Id);
                return new HistoryChangeResult(kept ? record : null,
                    JoinWarnings(warning, kept ? CapacityWarning() : "当前容量不足，新记录未被保留；固定记录未被清理。"));
            }
            finally
            {
                if (createdFile is not null)
                {
                    try { File.Delete(createdFile); }
                    catch (Exception exception) { AppLogger.LogException("保存失败后清理新图片失败", exception); }
                }
            }
        }
        finally { _gate.Release(); }
    }

    public Task<string?> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => ChangeAsync(records => records.RemoveAll(record => record.Id == id), cancellationToken);

    public Task<string?> ClearAsync(CancellationToken cancellationToken = default)
        => ChangeAsync(records => records.Clear(), cancellationToken);

    public Task<string?> TogglePinAsync(Guid id, CancellationToken cancellationToken = default)
        => ChangeAsync(records =>
        {
            int index = records.FindIndex(record => record.Id == id);
            if (index >= 0) records[index] = records[index] with { IsPinned = !records[index].IsPinned };
        }, cancellationToken);

    public async Task<string?> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings = settings.Clone();
        settings.Normalize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var next = _records.ToList();
            var removed = ApplyRetention(next, settings);
            await CommitAsync(next, settings, removed, cancellationToken).ConfigureAwait(false);
            return JoinWarnings(await CleanupPendingAsync().ConfigureAwait(false), CapacityWarning());
        }
        finally { _gate.Release(); }
    }

    public async Task FlushAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    private async Task<string?> ChangeAsync(Action<List<ClipboardRecord>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var next = _records.ToList();
            change(next);
            var trimmed = ApplyRetention(next, _settings);
            var removed = _records.Where(record => next.All(item => item.Id != record.Id)).Concat(trimmed).ToList();
            await CommitAsync(next, _settings, removed, cancellationToken).ConfigureAwait(false);
            return JoinWarnings(await CleanupPendingAsync().ConfigureAwait(false), CapacityWarning());
        }
        finally { _gate.Release(); }
    }

    private async Task CommitAsync(List<ClipboardRecord> next, AppSettings settings, List<ClipboardRecord> removed, CancellationToken cancellationToken)
    {
        var pending = _pendingDeletes.Concat(removed.Where(record => record.Kind == ClipboardRecordKind.Image)
            .Select(record => record.ImageFileName!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var document = new HistoryDocument { Records = next, Settings = settings.Clone(), PendingImageDeletes = pending };
        // Publish memory state only after the atomic metadata replacement succeeds.
        await Task.Run(() => _store.SaveAsync(document, cancellationToken), cancellationToken).ConfigureAwait(false);
        _records = next;
        _settings = settings.Clone();
        _pendingDeletes.Clear();
        _pendingDeletes.UnionWith(pending);
    }

    private async Task<string?> CleanupPendingAsync()
    {
        int failed = 0;
        foreach (string name in _pendingDeletes.ToArray())
        {
            try
            {
                await Task.Run(() => File.Delete(_store.GetImagePath(name))).ConfigureAwait(false);
                _pendingDeletes.Remove(name);
            }
            catch (Exception exception)
            {
                failed++;
                AppLogger.LogException($"删除图片 {name} 失败，下次操作或启动时重试", exception);
            }
        }
        return failed == 0 ? null : $"{failed} 个图片文件暂时无法删除，已记入重试队列。请检查权限或文件占用。";
    }

    public static List<ClipboardRecord> ApplyRetention(List<ClipboardRecord> records, AppSettings settings)
    {
        var removed = new List<ClipboardRecord>();
        long total = records.Sum(record => record.ByteLength);
        foreach (var candidate in records.Where(record => !record.IsPinned).OrderBy(record => record.CopiedAtUtc).ToArray())
        {
            if (records.Count <= settings.MaxRecords && total <= (long)settings.MaxStorageMb * 1024 * 1024) break;
            records.Remove(candidate);
            total -= candidate.ByteLength;
            removed.Add(candidate);
        }
        return removed;
    }

    private string? CapacityWarning()
        => _records.Count > _settings.MaxRecords || _records.Sum(record => record.ByteLength) > (long)_settings.MaxStorageMb * 1024 * 1024
            ? "固定记录已超过容量限制；它们不会被自动清理。" : null;

    private static bool IsValidRecord(ClipboardRecord? record)
        => record is not null && record.Id != Guid.Empty && record.ContentHash is { Length: 64 } && record.ByteLength >= 0
            && record.CopiedAtUtc.Kind == DateTimeKind.Utc
            && (record.Kind == ClipboardRecordKind.Text ? !string.IsNullOrEmpty(record.Text)
                : record.Kind == ClipboardRecordKind.Image && record.ImageFileName == $"{record.Id:N}.png"
                    && record.ImageWidth > 0 && record.ImageHeight > 0);

    private void EnsureLoaded()
    {
        if (!_loaded) throw new InvalidOperationException("历史记录尚未成功加载，不能覆盖已有数据。");
    }

    private static string? JoinWarnings(string? first, string? second)
        => string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : first + " " + second;
}

public sealed record HistoryChangeResult(ClipboardRecord? Record, string? Warning);
