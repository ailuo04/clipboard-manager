using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ClipboardManager.Updates;
using ClipboardManager.Utilities;

namespace ClipboardManager.Services;

public sealed record UpdateProgress(string Status, long Downloaded, long Total);
public sealed record UpdateAsset(long Id, long Size);
public sealed record AvailableUpdate(UpdateManifest Manifest, byte[] ManifestBytes, UpdatePlan Plan, IReadOnlyDictionary<string, UpdateAsset> Assets)
{
    public long DownloadSize => Plan.Changes.Sum(file => file.CompressedSize);
}

public sealed class UpdateService : IDisposable
{
    public const string Repository = "ailuo04/clipboard-manager";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _client;
    private readonly Func<string?> _getToken;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _root;
    private bool _prepared;
    public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.3";

    public UpdateService(string? root = null, HttpMessageHandler? handler = null, Func<string?>? tokenProvider = null)
    {
        _root = Path.GetFullPath(root ?? AppContext.BaseDirectory);
        _getToken = tokenProvider ?? UpdateCredentials.Read;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(30);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ClipboardManager/" + CurrentVersion);
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token);
        try
        {
            var current = LoadCurrentManifest();
            byte[] metadata = await GetMetadataAsync($"https://api.github.com/repos/{Repository}/releases/latest", false, token);
            using var release = JsonDocument.Parse(metadata);
            var data = release.RootElement;
            if (data.GetProperty("draft").GetBoolean() || data.GetProperty("prerelease").GetBoolean()) return null;
            string tag = data.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var nextVersion)) throw new InvalidDataException("发布版本号不正确。");
            if (nextVersion <= Version.Parse(current.Version)) return null;
            var assets = new Dictionary<string, UpdateAsset>(StringComparer.Ordinal);
            foreach (var asset in data.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                long id = asset.GetProperty("id").GetInt64();
                long size = asset.GetProperty("size").GetInt64();
                if (id <= 0 || size < 0 || !assets.TryAdd(name, new UpdateAsset(id, size))) throw new InvalidDataException("发行附件信息不正确。");
            }
            string manifestName = $"ClipboardManager-{tag[1..]}-win-x64-update.json";
            if (!assets.TryGetValue(manifestName, out var manifestAsset)) throw new InvalidDataException("此发行版没有增量更新清单，不会下载完整安装包。");
            if (manifestAsset.Size > 4 * 1024 * 1024) throw new InvalidDataException("更新清单过大。");
            byte[] manifestBytes = await GetMetadataAsync(AssetUrl(manifestAsset), true, token);
            if (manifestBytes.LongLength != manifestAsset.Size) throw new InvalidDataException("更新清单长度不符。");
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonOptions) ?? throw new InvalidDataException("更新清单为空。");
            UpdateFiles.ValidateManifest(manifest);
            if (manifest.Version != tag[1..]) throw new InvalidDataException("发行版与更新清单版本不一致。");
            foreach (var file in manifest.Files)
                if (!assets.TryGetValue(file.AssetName, out var asset) || asset.Size != file.CompressedSize)
                    throw new InvalidDataException("增量更新文件缺失或大小不符：" + file.Path);
            var plan = await Task.Run(() => UpdateFiles.Plan(_root, current, manifest, token), token);
            return new AvailableUpdate(manifest, manifestBytes, plan, assets);
        }
        finally { _gate.Release(); }
    }

    private UpdateManifest LoadCurrentManifest()
    {
        string path = UpdateFiles.ResolvePath(_root, UpdateFiles.ManifestName);
        if (!File.Exists(path)) throw new InvalidDataException("当前程序不包含增量更新清单；需先使用带更新功能的发行版。");
        var manifest = UpdateFiles.ReadJson<UpdateManifest>(path);
        UpdateFiles.ValidateManifest(manifest);
        if (manifest.Version != CurrentVersion) throw new InvalidDataException("程序版本与本地更新清单不一致，已停止更新。");
        return manifest;
    }

    public async Task PrepareAsync(AvailableUpdate update, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token);
        string workspace = "";
        bool created = false;
        try
        {
            workspace = UpdateFiles.ResolvePath(_root, UpdateFiles.WorkspaceName);
            _prepared = false;
            if (Directory.Exists(workspace))
            {
                if (File.Exists(Path.Combine(workspace, "journal.json")) && !File.Exists(Path.Combine(workspace, "complete")))
                    throw new IOException("有未完成的更新，请关闭并重新启动程序以恢复。");
                UpdateFiles.CleanWorkspace(_root);
            }
            var current = LoadCurrentManifest();
            if (Version.Parse(update.Manifest.Version) <= Version.Parse(current.Version)) throw new InvalidDataException("更新版本必须高于当前版本。");
            var plan = await Task.Run(() => UpdateFiles.Plan(_root, current, update.Manifest, token), token);
            long required = checked(plan.Changes.Sum(file => file.Size) + 4 * 1024 * 1024);
            if (new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace < required)
                throw new IOException($"可用空间不足，本次增量更新至少需要 {FormatSize(required)} 临时空间。");
            var helper = current.Files.Single(file => file.Path == "ClipboardManager.Update.exe");
            if (!UpdateFiles.Matches(UpdateFiles.ResolvePath(_root, helper.Path), helper)) throw new InvalidDataException("更新工具校验失败。");
            Directory.CreateDirectory(workspace);
            created = true;
            Directory.CreateDirectory(Path.Combine(workspace, "new"));
            File.Copy(UpdateFiles.ResolvePath(_root, helper.Path), Path.Combine(workspace, "worker.exe"));
            File.Copy(UpdateFiles.ResolvePath(_root, UpdateFiles.ManifestName), Path.Combine(workspace, "old-manifest.json"));
            await File.WriteAllBytesAsync(Path.Combine(workspace, "new", UpdateFiles.ManifestName), update.ManifestBytes, token);
            long downloaded = 0;
            long total = plan.Changes.Sum(file => file.CompressedSize);
            foreach (var file in plan.Changes)
            {
                token.ThrowIfCancellationRequested();
                string path = UpdateFiles.ResolvePath(Path.Combine(workspace, "new"), file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await DownloadAsync(update.Assets[file.AssetName], file, path, downloaded, total, progress, token);
                downloaded += file.CompressedSize;
            }
            await Task.Run(() => UpdateFiles.PrepareOperations(_root, workspace), token);
            token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(workspace, "prepared"), "");
            _prepared = true;
            progress?.Report(new UpdateProgress("下载与校验完成", total, total));
        }
        catch (Exception failure)
        {
            try { if (created) UpdateFiles.CleanWorkspace(_root); }
            catch (Exception cleanup) { throw new AggregateException("更新未完成，原程序未被修改；临时文件清理也失败。", failure, cleanup); }
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task DownloadAsync(UpdateAsset asset, UpdateFile file, string path, long completed, long total,
        IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        token = timeout.Token;
        using var request = CreateRequest(AssetUrl(asset), true);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        EnsureSuccess(response);
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

    public void LaunchPrepared()
    {
        string workspace = UpdateFiles.ResolvePath(_root, UpdateFiles.WorkspaceName);
        if (!_prepared || !File.Exists(Path.Combine(workspace, "prepared"))) throw new InvalidOperationException("增量更新尚未准备完成。");
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.Combine(workspace, "worker.exe")) { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply");
        start.ArgumentList.Add(current.Id.ToString());
        start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString());
        using var worker = Process.Start(start) ?? throw new IOException("无法启动更新工具。");
        _prepared = false;
    }

    public void CancelPrepared()
    {
        if (!_prepared) return;
        UpdateFiles.CleanWorkspace(_root);
        _prepared = false;
    }

    public void MarkStartupReady()
    {
        string workspace = UpdateFiles.ResolvePath(_root, UpdateFiles.WorkspaceName);
        if (File.Exists(Path.Combine(workspace, "applied")) && File.Exists(Path.Combine(workspace, "journal.json"))
            && !File.Exists(Path.Combine(workspace, "complete")))
            File.WriteAllText(Path.Combine(workspace, "ready"), "");
    }

    public async Task CleanFinishedAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token);
        try
        {
            string workspace = UpdateFiles.ResolvePath(_root, UpdateFiles.WorkspaceName);
            for (int attempt = 0; attempt < 60 && Directory.Exists(workspace); attempt++)
            {
                token.ThrowIfCancellationRequested();
                bool busy = false;
                using (var gate = new Mutex(false, UpdateFiles.LockName(_root)))
                {
                    bool entered;
                    try { entered = gate.WaitOne(0); }
                    catch (AbandonedMutexException) { entered = true; }
                    if (entered)
                    {
                        try
                        {
                            if (File.Exists(Path.Combine(workspace, "complete")) || !File.Exists(Path.Combine(workspace, "journal.json")))
                            {
                                if (File.Exists(Path.Combine(workspace, "complete")))
                                {
                                    var oldManifest = UpdateFiles.ReadJson<UpdateManifest>(Path.Combine(workspace, "old-manifest.json"));
                                    UpdateFiles.ValidateManifest(oldManifest);
                                    UpdateFiles.RemoveEmptyDirectories(_root, oldManifest.Files.Select(file => file.Path));
                                }
                                UpdateFiles.CleanWorkspace(_root);
                                return;
                            }
                        }
                        finally { gate.ReleaseMutex(); }
                    }
                    else busy = true;
                }
                if (!busy) return;
                await Task.Delay(500, token);
            }
        }
        finally { _gate.Release(); }
    }

    private static string AssetUrl(UpdateAsset asset) => $"https://api.github.com/repos/{Repository}/releases/assets/{asset.Id}";

    private HttpRequestMessage CreateRequest(string url, bool asset)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(asset ? "application/octet-stream" : "application/vnd.github+json"));
        string? token = _getToken();
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<byte[]> GetMetadataAsync(string url, bool asset, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        token = timeout.Token;
        using var request = CreateRequest(url, asset);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
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

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound) throw new HttpRequestException("发布源不可访问或暂无发行版；私有仓库需要只读访问令牌。");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("更新访问被拒绝或请求达到频率限制，请检查令牌权限或稍后重试。");
        response.EnsureSuccessStatusCode();
    }

    public static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes / (1024d * 1024):0.##} MB";
    public void Dispose() { _lifetime.Cancel(); _client.Dispose(); }

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
