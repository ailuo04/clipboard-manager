using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipboardManager.Services;
using ClipboardManager.Updates;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class StaticUpdateTests
{
    private const string LatestUrl = "https://github.com/ailuo04/clipboard-manager/releases/latest/download/latest-win-x64.json";

    [Fact]
    public async Task CheckAndPrepareUseStaticManifestAndVersionPinnedDownloads()
    {
        using var fixture = new UpdateFixture();
        var requests = new List<string>();
        using var service = fixture.CreateService(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            requests.Add(url);
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("X-GitHub-Api-Version"));
            if (url == LatestUrl) return Json(fixture.Next);
            var changed = fixture.Next.Files[0];
            Assert.Equal($"https://github.com/ailuo04/clipboard-manager/releases/download/v999.0.0/update-{changed.Sha256}.gz", url);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.NewCompressed) };
        });

        var update = Assert.IsType<AvailableUpdate>(await service.CheckAsync());
        Assert.Single(update.Plan.Changes);
        Assert.Equal(fixture.NewCompressed.Length, update.DownloadSize);
        Assert.Single(requests);
        await service.PrepareAsync(update, null);
        Assert.Equal(2, requests.Count);
        Assert.Equal("new payload", File.ReadAllText(Path.Combine(fixture.Root, UpdateFiles.WorkspaceName, "new", "ClipboardManager.exe")));
        Assert.Equal("old payload", File.ReadAllText(Path.Combine(fixture.Root, "ClipboardManager.exe")));
        service.CancelPrepared();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameOrOlderVersionDoesNotOfferUpdate(bool older)
    {
        using var fixture = new UpdateFixture();
        fixture.Next.Version = older ? "0.0.0" : UpdateService.CurrentVersion;
        using var service = fixture.CreateService(request =>
        {
            Assert.Equal(LatestUrl, request.RequestUri!.AbsoluteUri);
            return Json(fixture.Next);
        });
        Assert.Null(await service.CheckAsync());
    }

    [Fact]
    public async Task NotModifiedReusesCachedStaticManifest()
    {
        using var fixture = new UpdateFixture();
        int count = 0;
        using var service = fixture.CreateService(request =>
        {
            Assert.Equal(LatestUrl, request.RequestUri!.AbsoluteUri);
            if (++count == 1)
            {
                var response = Json(fixture.Next);
                response.Headers.ETag = new EntityTagHeaderValue("\"manifest-1\"");
                return response;
            }
            Assert.Equal("\"manifest-1\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        Assert.NotNull(await service.CheckAsync());
        Assert.NotNull(await service.CheckAsync());
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("path")]
    [InlineData("hash")]
    [InlineData("packaging")]
    public async Task InvalidManifestIsRejectedBeforeDownloadingFiles(string invalidField)
    {
        using var fixture = new UpdateFixture();
        switch (invalidField)
        {
            case "version": fixture.Next.Version = "../other"; break;
            case "path": fixture.Next.Files[0].Path = "../outside.exe"; break;
            case "hash": fixture.Next.Files[0].Sha256 = "invalid"; break;
            case "packaging": fixture.Next.Packaging = "offline-bundled-runtime"; break;
        }
        using var service = fixture.CreateService(request =>
        {
            Assert.Equal(LatestUrl, request.RequestUri!.AbsoluteUri);
            return Json(fixture.Next);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task MissingStaticManifestReportsFailureWithoutApiFallback()
    {
        using var fixture = new UpdateFixture();
        int count = 0;
        using var service = fixture.CreateService(request =>
        {
            count++;
            Assert.Equal(LatestUrl, request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync());
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OversizedStaticManifestIsRejected()
    {
        using var fixture = new UpdateFixture();
        using var service = fixture.CreateService(request =>
        {
            Assert.Equal(LatestUrl, request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]) };
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task CorruptDownloadIsRejectedAndStagingIsCleaned()
    {
        using var fixture = new UpdateFixture();
        fixture.Next.Files[0].Sha256 = new string('0', 64);
        using var service = fixture.CreateService(request =>
        {
            if (request.RequestUri!.AbsoluteUri == LatestUrl) return Json(fixture.Next);
            Assert.Equal("https://github.com/ailuo04/clipboard-manager/releases/download/v999.0.0/update-" + new string('0', 64) + ".gz", request.RequestUri.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.NewCompressed) };
        });
        var update = Assert.IsType<AvailableUpdate>(await service.CheckAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(update, null));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, UpdateFiles.WorkspaceName)));
        Assert.Equal("old payload", File.ReadAllText(Path.Combine(fixture.Root, "ClipboardManager.exe")));
    }

    private static HttpResponseMessage Json(UpdateManifest manifest) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(respond(request));
    }

    private sealed class UpdateFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "clipboard-static-update-" + Guid.NewGuid().ToString("N"));
        public UpdateManifest Next { get; }
        public byte[] NewCompressed { get; }

        public UpdateFixture()
        {
            Directory.CreateDirectory(Root);
            var current = Manifest(UpdateService.CurrentVersion);
            Next = Manifest("999.0.0");
            byte[] oldBytes = Encoding.UTF8.GetBytes("old payload");
            byte[] newBytes = Encoding.UTF8.GetBytes("new payload");
            NewCompressed = Compress(newBytes);
            foreach (string name in new[] { "ClipboardManager.exe", "ClipboardManager.dll", "ClipboardManager.runtimeconfig.json", "ClipboardManager.Update.exe" })
            {
                File.WriteAllBytes(Path.Combine(Root, name), oldBytes);
                current.Files.Add(FileEntry(name, oldBytes));
                Next.Files.Add(FileEntry(name, name == "ClipboardManager.exe" ? newBytes : oldBytes));
            }
            UpdateFiles.WriteJson(Path.Combine(Root, UpdateFiles.ManifestName), current);
        }

        public UpdateService CreateService(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(Root, new Handler(respond));
        public void Dispose() => Directory.Delete(Root, true);
        private static UpdateManifest Manifest(string version) => new() { Schema = 1, Version = version, Packaging = "sdk-self-contained" };
        private static UpdateFile FileEntry(string name, byte[] bytes) => new()
        {
            Path = name, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Size = bytes.Length, CompressedSize = Compress(bytes).Length
        };
        private static byte[] Compress(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true)) gzip.Write(bytes);
            return output.ToArray();
        }
    }
}
