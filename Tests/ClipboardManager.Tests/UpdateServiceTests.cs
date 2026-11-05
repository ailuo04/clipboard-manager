using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClipboardManager.Services;
using ClipboardManager.Updates;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckAndPrepare_UsesLatestPublicReleaseAndTransfersOnlyChangedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "clipboard-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var updates = new UpdateService(root);
        try
        {
            var currentFiles = new List<UpdateFile>();
            var nextContents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["ClipboardManager.exe"] = Encoding.UTF8.GetBytes("app-host"),
                ["ClipboardManager.dll"] = Encoding.UTF8.GetBytes("new-application"),
                ["ClipboardManager.runtimeconfig.json"] = Encoding.UTF8.GetBytes("runtime-config"),
                ["ClipboardManager.Update.exe"] = Encoding.UTF8.GetBytes("update-helper")
            };
            foreach (var entry in nextContents)
            {
                string currentContent = entry.Key == "ClipboardManager.dll" ? "old-application" : Encoding.UTF8.GetString(entry.Value);
                File.WriteAllBytes(Path.Combine(root, entry.Key), Encoding.UTF8.GetBytes(currentContent));
                currentFiles.Add(CreateFile(entry.Key, Encoding.UTF8.GetBytes(currentContent)));
            }
            File.WriteAllText(Path.Combine(root, "obsolete-managed-file.txt"), "old-release-file");
            currentFiles.Add(CreateFile("obsolete-managed-file.txt", Encoding.UTF8.GetBytes("old-release-file")));

            string currentVersion = UpdateService.CurrentVersion;
            string nextVersion = NextPatch(currentVersion);
            var currentManifest = CreateManifest(currentVersion, currentFiles);
            UpdateFiles.WriteJson(Path.Combine(root, UpdateFiles.ManifestName), currentManifest);

            var nextFiles = new List<UpdateFile>();
            var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in nextContents)
            {
                byte[] compressed = Compress(entry.Value);
                var file = CreateFile(entry.Key, entry.Value, compressed.Length);
                nextFiles.Add(file);
                blobs.Add(file.AssetName, compressed);
            }

            var nextManifest = CreateManifest(nextVersion, nextFiles);
            string manifestPath = Path.Combine(root, "published-update.json");
            UpdateFiles.WriteJson(manifestPath, nextManifest);
            byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath);
            string tag = "v" + nextVersion;
            string releaseBase = $"https://github.com/{UpdateService.Repository}/releases/download/{tag}/";
            string manifestName = $"ClipboardManager-{nextVersion}-win-x64-update.json";
            string manifestUrl = releaseBase + manifestName;
            string changedAssetName = nextFiles.Single(file => file.Path == "ClipboardManager.dll").AssetName;
            var handler = new PublicReleaseHandler(tag, manifestName, manifestBytes, manifestUrl, blobs);
            using var service = new UpdateService(root, handler);

            AvailableUpdate update = Assert.IsType<AvailableUpdate>(await service.CheckAsync());
            Assert.Equal(nextVersion, update.Manifest.Version);
            Assert.Equal(new[] { "ClipboardManager.dll" }, update.Plan.Changes.Select(file => file.Path));
            Assert.Equal(new[] { "obsolete-managed-file.txt" }, update.Plan.Deletes.Select(file => file.Path));
            Assert.Equal((long)blobs[changedAssetName].Length, update.DownloadSize);
            Assert.Contains(manifestUrl, handler.Requests);
            Assert.DoesNotContain(handler.Requests, url => url.Contains("/releases/tags/updates-", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Requests, url => url.Contains("/releases/assets/", StringComparison.Ordinal));
            Assert.All(handler.Authorization, header => Assert.Null(header));

            await service.PrepareAsync(update, progress: null);
            string stagedDll = Path.Combine(root, UpdateFiles.WorkspaceName, "new", "ClipboardManager.dll");
            Assert.Equal("new-application", await File.ReadAllTextAsync(stagedDll));
            Assert.Equal(new[] { releaseBase + changedAssetName }, handler.BlobRequests);
            service.CancelPrepared();

            await service.CheckAsync();
            Assert.Equal(2, handler.LatestRequests);
            Assert.True(handler.SecondLatestWasConditional);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Check_UsesMatchingPrereleaseWhenLatestReleaseContainsOnlyInstaller()
    {
        string root = Path.Combine(Path.GetTempPath(), "clipboard-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var currentFiles = new List<UpdateFile>();
            var currentContents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["ClipboardManager.exe"] = Encoding.UTF8.GetBytes("app-host"),
                ["ClipboardManager.dll"] = Encoding.UTF8.GetBytes("old-application"),
                ["ClipboardManager.runtimeconfig.json"] = Encoding.UTF8.GetBytes("runtime-config"),
                ["ClipboardManager.Update.exe"] = Encoding.UTF8.GetBytes("update-helper")
            };
            var nextContents = new Dictionary<string, byte[]>(currentContents, StringComparer.Ordinal)
            {
                ["ClipboardManager.dll"] = Encoding.UTF8.GetBytes("new-application")
            };
            foreach (var entry in currentContents)
            {
                File.WriteAllBytes(Path.Combine(root, entry.Key), entry.Value);
                currentFiles.Add(CreateFile(entry.Key, entry.Value));
            }
            string currentVersion = UpdateService.CurrentVersion;
            string nextVersion = NextPatch(currentVersion);
            UpdateFiles.WriteJson(Path.Combine(root, UpdateFiles.ManifestName), CreateManifest(currentVersion, currentFiles));

            var nextFiles = new List<UpdateFile>();
            var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in nextContents)
            {
                byte[] compressed = Compress(entry.Value);
                var file = CreateFile(entry.Key, entry.Value, compressed.Length);
                nextFiles.Add(file);
                blobs.Add(file.AssetName, compressed);
            }
            string updateTag = "updates-v" + nextVersion;
            string updateBase = $"https://github.com/{UpdateService.Repository}/releases/download/{updateTag}/";
            string manifestName = $"ClipboardManager-{nextVersion}-win-x64-update.json";
            string manifestPath = Path.Combine(root, "published-update.json");
            UpdateFiles.WriteJson(manifestPath, CreateManifest(nextVersion, nextFiles));
            byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath);
            string manifestUrl = updateBase + manifestName;
            string changedAssetName = nextFiles.Single(file => file.Path == "ClipboardManager.dll").AssetName;
            var handler = new PublicReleaseHandler("v" + nextVersion, manifestName, manifestBytes, manifestUrl, blobs, separateUpdateRelease: true);
            using var service = new UpdateService(root, handler);

            AvailableUpdate update = Assert.IsType<AvailableUpdate>(await service.CheckAsync());
            Assert.Equal(new[] { "ClipboardManager.dll" }, update.Plan.Changes.Select(file => file.Path));
            Assert.Contains(handler.Requests, url => url.EndsWith("/releases/tags/" + updateTag, StringComparison.Ordinal));
            Assert.Contains(manifestUrl, handler.Requests);
            Assert.DoesNotContain(handler.Requests, url => url.EndsWith("-setup.exe", StringComparison.Ordinal));
            await service.PrepareAsync(update, progress: null);
            Assert.Equal(new[] { updateBase + changedAssetName }, handler.BlobRequests);
            service.CancelPrepared();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Check_RejectsReleaseAssetsOutsideTheConfiguredPublicRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), "clipboard-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new[]
            {
                CreateFile("ClipboardManager.exe", [1]),
                CreateFile("ClipboardManager.dll", [2]),
                CreateFile("ClipboardManager.runtimeconfig.json", [3]),
                CreateFile("ClipboardManager.Update.exe", [4])
            };
            foreach (var file in files) File.WriteAllBytes(Path.Combine(root, file.Path), [1]);
            UpdateFiles.WriteJson(Path.Combine(root, UpdateFiles.ManifestName), CreateManifest(UpdateService.CurrentVersion, files));
            var handler = PublicReleaseHandler.CreateWithUnsafeAssetUrl(NextPatch(UpdateService.CurrentVersion));
            using var service = new UpdateService(root, handler);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
            Assert.Contains("附件信息不正确", error.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static UpdateManifest CreateManifest(string version, IEnumerable<UpdateFile> files) => new()
    {
        Schema = 1,
        Application = "ClipboardManager",
        Version = version,
        Platform = "win-x64",
        Packaging = "sdk-self-contained",
        Files = files.ToList()
    };

    private static UpdateFile CreateFile(string path, byte[] content, long? compressedSize = null) => new()
    {
        Path = path,
        Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
        Size = content.LongLength,
        CompressedSize = compressedSize ?? Math.Max(1, content.LongLength)
    };

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(source);
        return output.ToArray();
    }

    private static string NextPatch(string version)
    {
        Version current = Version.Parse(version);
        return $"{current.Major}.{current.Minor}.{current.Build + 1}";
    }

    private sealed class PublicReleaseHandler : HttpMessageHandler
    {
        private readonly string _releaseJson;
        private readonly string _manifestUrl;
        private readonly string? _updateReleaseJson;
        private readonly byte[] _manifestBytes;
        private readonly Dictionary<string, byte[]> _blobs;

        public PublicReleaseHandler(string tag, string manifestName, byte[] manifestBytes, string manifestUrl, Dictionary<string, byte[]> blobs, bool separateUpdateRelease = false)
        {
            _manifestUrl = manifestUrl;
            _manifestBytes = manifestBytes;
            string assetTag = separateUpdateRelease ? "updates-" + tag : tag;
            _blobs = blobs.ToDictionary(pair => $"https://github.com/{UpdateService.Repository}/releases/download/{assetTag}/{pair.Key}", pair => pair.Value, StringComparer.Ordinal);
            var assets = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = manifestName,
                    ["id"] = 1,
                    ["size"] = manifestBytes.Length,
                    ["browser_download_url"] = manifestUrl
                }
            };
            long id = 2;
            foreach (var blob in _blobs)
                assets.Add(new JsonObject { ["name"] = Path.GetFileName(blob.Key), ["id"] = id++, ["size"] = blob.Value.Length, ["browser_download_url"] = blob.Key });
            if (separateUpdateRelease)
            {
                _updateReleaseJson = new JsonObject { ["tag_name"] = assetTag, ["draft"] = false, ["prerelease"] = true, ["assets"] = assets.DeepClone() }.ToJsonString();
                var installer = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = $"ClipboardManager-{tag[1..]}-win-x64-setup.exe",
                        ["id"] = 1,
                        ["size"] = 4,
                        ["browser_download_url"] = $"https://github.com/{UpdateService.Repository}/releases/download/{tag}/ClipboardManager-{tag[1..]}-win-x64-setup.exe"
                    }
                };
                _releaseJson = new JsonObject { ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = false, ["assets"] = installer }.ToJsonString();
            }
            else
            {
                _updateReleaseJson = null;
                _releaseJson = new JsonObject { ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = false, ["assets"] = assets }.ToJsonString();
            }
        }

        private PublicReleaseHandler(string releaseJson)
        {
            _releaseJson = releaseJson;
            _updateReleaseJson = null;
            _manifestUrl = string.Empty;
            _manifestBytes = [];
            _blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        public List<string> Requests { get; } = [];
        public List<string> BlobRequests { get; } = [];
        public List<string?> Authorization { get; } = [];
        public int LatestRequests { get; private set; }
        public bool SecondLatestWasConditional { get; private set; }

        public static PublicReleaseHandler CreateWithUnsafeAssetUrl(string version)
        {
            string manifestName = $"ClipboardManager-{version}-win-x64-update.json";
            var release = new JsonObject
            {
                ["tag_name"] = "v" + version,
                ["draft"] = false,
                ["prerelease"] = false,
                ["assets"] = new JsonArray
                {
                    new JsonObject { ["name"] = manifestName, ["id"] = 1, ["size"] = 10, ["browser_download_url"] = "https://example.com/stolen-update.json" }
                }
            };
            return new PublicReleaseHandler(release.ToJsonString());
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            Authorization.Add(request.Headers.Authorization?.ToString());
            if (url.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                LatestRequests++;
                if (LatestRequests > 1)
                {
                    SecondLatestWasConditional = request.Headers.IfNoneMatch.Any(header => header.Tag == "\"latest-release-etag\"");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                }
                var latest = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_releaseJson, Encoding.UTF8, "application/json") };
                latest.Headers.ETag = new EntityTagHeaderValue("\"latest-release-etag\"");
                return Task.FromResult(latest);
            }
            if (_updateReleaseJson is not null && url.EndsWith("/releases/tags/updates-" + JsonNode.Parse(_releaseJson)!["tag_name"]!.GetValue<string>(), StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_updateReleaseJson, Encoding.UTF8, "application/json") });
            if (url == _manifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_manifestBytes) });
            if (_blobs.TryGetValue(url, out byte[]? blob))
            {
                BlobRequests.Add(url);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blob) });
            }
            throw new InvalidOperationException("Unexpected updater request: " + url);
        }
    }
}
