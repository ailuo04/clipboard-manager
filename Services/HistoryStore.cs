using System.Text.Json;
using ClipboardManager.Models;
using ClipboardManager.Utilities;

namespace ClipboardManager.Services;

public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public HistoryStore(string? rootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? AppPaths.RootDirectory);
    }

    public string RootDirectory { get; }
    public string ImagesDirectory => Path.Combine(RootDirectory, "images");
    public string HistoryFilePath => Path.Combine(RootDirectory, "history.json");

    public async Task<HistoryDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(ImagesDirectory);
            if (!File.Exists(HistoryFilePath)) return new HistoryDocument();
            await using var stream = new FileStream(HistoryFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, useAsync: true);
            var document = await JsonSerializer.DeserializeAsync<HistoryDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (document is null || document.Version != 1 || document.Records is null || document.Settings is null)
                throw new InvalidDataException("历史记录结构不完整或版本不受支持。");
            document.Settings.Normalize();
            document.PendingImageDeletes ??= [];
            return document;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            throw new HistoryPersistenceException("历史记录读取失败。为保护已有数据，已停止写入；修复数据或权限后可重试。", exception);
        }
    }

    public async Task SaveAsync(HistoryDocument document, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ImagesDirectory);
        string temporaryPath = HistoryFilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, HistoryFilePath, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            throw new HistoryPersistenceException("历史记录保存失败，请检查磁盘空间和应用数据目录的写入权限。", exception);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) { AppLogger.LogException("删除历史记录临时文件失败", exception); }
        }
    }

    public string GetImagePath(string name)
    {
        if (name.Length != 36 || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(name[..32], "N", out _))
            throw new InvalidDataException("历史图片文件名不合法。");
        return Path.Combine(ImagesDirectory, name);
    }
}

public sealed class HistoryPersistenceException : Exception
{
    public HistoryPersistenceException(string message, Exception innerException) : base(message, innerException) { }
}
