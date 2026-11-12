using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ClipboardManager.Updates;

namespace ClipboardManager.Services;

/// <summary>Owns the HttpClient and static release manifest requests, ETag caching, and verified file downloads.</summary>
internal sealed class GitHubReleaseClient : IDisposable
{
    private readonly HttpClient _client;
    private string? _latestManifestETag;
    private byte[]? _latestManifestBytes;
    private bool _disposed;

    public GitHubReleaseClient(HttpMessageHandler? handler = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(30);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ClipboardManager/" + UpdateService.CurrentVersion);
    }

    public async Task<byte[]> GetLatestManifestAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        token = timeout.Token;
        using var request = CreateRequest($"https://github.com/{UpdateService.Repository}/releases/latest/download/latest-win-x64.json");
        if (_latestManifestETag is not null) request.Headers.IfNoneMatch.ParseAdd(_latestManifestETag);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return _latestManifestBytes ?? throw new InvalidDataException("GitHub 返回了未修改状态，但本地没有缓存的更新清单。");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("最新正式版尚未提供固定更新清单（latest-win-x64.json）。请在发布包含该清单的版本后重试。", null, response.StatusCode);
        GitHubHttp.EnsureSuccess(response);
        _latestManifestBytes = await ReadContentAsync(response.Content, token);
        _latestManifestETag = response.Headers.ETag?.ToString();
        return _latestManifestBytes;
    }

    public async Task DownloadToAsync(UpdateAsset asset, UpdateFile file, string path, long completed, long total,
        IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        ThrowIfDisposed();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        token = timeout.Token;
        using var request = CreateRequest(asset.DownloadUrl);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        GitHubHttp.EnsureSuccess(response);
        if (response.Content.Headers.ContentLength is long length && length != file.CompressedSize) throw new InvalidDataException("下载长度不符：" + file.Path);
        await using var network = await response.Content.ReadAsStreamAsync(token);
        using var limited = new BoundedReadStream(network, file.CompressedSize);
        using var gzip = new GZipStream(limited, CompressionMode.Decompress, leaveOpen: true);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long written = 0;
        var reportTime = Stopwatch.StartNew();
        int count;
        progress?.Report(new UpdateProgress("正在下载 " + file.Path, completed, total));
        while ((count = await gzip.ReadAsync(buffer, token)) != 0)
        {
            written = checked(written + count);
            if (written > file.Size) throw new InvalidDataException("解压长度超过清单限制：" + file.Path);
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            if (reportTime.ElapsedMilliseconds >= 100)
            {
                progress?.Report(new UpdateProgress("正在下载 " + file.Path, completed + limited.BytesRead, total));
                reportTime.Restart();
            }
        }
        if (written != file.Size || limited.BytesRead != file.CompressedSize || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != file.Sha256)
            throw new InvalidDataException("更新文件 SHA256 校验失败：" + file.Path);
        await output.FlushAsync(token);
        output.Flush(true);
        progress?.Report(new UpdateProgress("已校验 " + file.Path, completed + file.CompressedSize, total));
    }

    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return request;
    }

    private static async Task<byte[]> ReadContentAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("更新元数据超过大小限制。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GitHubReleaseClient));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }

    private sealed class BoundedReadStream(Stream stream, long limit) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = stream.Read(buffer, offset, count);
            Count(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            Count(read);
            return read;
        }
        private void Count(int read) { BytesRead = checked(BytesRead + read); if (BytesRead > limit) throw new InvalidDataException("压缩文件超过大小限制。"); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
