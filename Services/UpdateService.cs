using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using ClipboardManager.Updates;
using ClipboardManager.Utilities;

namespace ClipboardManager.Services;

public sealed record UpdateProgress(string Status, long Downloaded, long Total);
public sealed record UpdateAsset(long Size, string DownloadUrl);
public sealed record AvailableUpdate(UpdateManifest Manifest, byte[] ManifestBytes, UpdatePlan Plan, IReadOnlyDictionary<string, UpdateAsset> Assets)
{
    public long DownloadSize => Plan.Changes.Sum(file => file.CompressedSize);
}

/// <summary>Orchestrates update checks and installs on top of GitHubReleaseClient and UpdateFiles.</summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "ailuo04/clipboard-manager";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly GitHubReleaseClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _root;
    private bool _prepared;
    private bool _disposed;
    public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.3";

    public UpdateService(string? root = null, HttpMessageHandler? handler = null)
    {
        _root = Path.GetFullPath(root ?? AppContext.BaseDirectory);
        _http = new GitHubReleaseClient(handler);
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token);
        try
        {
            var current = LoadCurrentManifest();
            byte[] manifestBytes = await _http.GetLatestManifestAsync(token);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonOptions) ?? throw new InvalidDataException("更新清单为空。");
            UpdateFiles.ValidateManifest(manifest);
            if (Version.Parse(manifest.Version) <= Version.Parse(current.Version)) return null;
            // Pin every blob to the manifest's version, even if Latest changes during download.
            var assets = new Dictionary<string, UpdateAsset>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                string url = $"https://github.com/{Repository}/releases/download/v{manifest.Version}/{file.AssetName}";
                if (!assets.TryAdd(file.AssetName, new UpdateAsset(file.CompressedSize, url))
                    && assets[file.AssetName].Size != file.CompressedSize)
                    throw new InvalidDataException("相同增量文件的压缩大小不一致：" + file.Path);
            }
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
            _prepared = false;
            workspace = UpdateFiles.ResolvePath(_root, UpdateFiles.WorkspaceName);
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
                throw new IOException($"可用空间不足，本次增量更新至少需要 {Formatters.FormatSize(required)} 临时空间。");
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
                await _http.DownloadToAsync(update.Assets[file.AssetName], file, path, downloaded, total, progress, token);
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
        bool wasPrepared = _prepared;
        _prepared = false;
        try
        {
            string workspace = UpdateFiles.ResolvePath(_root, UpdateFiles.WorkspaceName);
            if (wasPrepared || File.Exists(Path.Combine(workspace, "prepared")))
                UpdateFiles.CleanWorkspace(_root);
        }
        finally { _prepared = false; }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _http.Dispose();
    }
}
