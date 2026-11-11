using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClipboardManager.App.Services;
using ClipboardManager.Core.Updates;
using Xunit;

namespace ClipboardManager.Update.Tests;

public sealed class ReleaseValidationTests
{
    private const string Version = "1.2.3";
    private const string ManifestName = "ClipboardManager-1.2.3-win-x64-update.json";
    private const string FileHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BlobName = "update-" + FileHash + ".gz";
    private const string ManifestJson = "{\"schema\":1,\"application\":\"ClipboardManager\",\"version\":\"1.2.3\",\"platform\":\"win-x64\",\"packageKind\":\"winui3\",\"files\":[{\"path\":\"ClipboardManager.exe\",\"sha256\":\"" + FileHash + "\",\"size\":10,\"compressedSize\":5}]}";

    [Fact]
    public async Task Reads_only_the_fixed_repository_and_validates_release_asset_identity()
    {
        var handler = new QueueHandler(
            ReleaseResponse(ManifestJson),
            JsonResponse(ManifestJson));
        using var client = new GitHubReleaseClient(handler);

        UpdateCheckResult result = await client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\users\\data", CancellationToken.None);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(Version, result.AvailableVersion);
        Assert.Equal(5, result.DownloadSize);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://api.github.com/repos/ailuo04/clipboard-manager/releases/latest", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("/repos/ailuo04/clipboard-manager/releases/assets/", handler.Requests[1].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("application/octet-stream", handler.Requests[1].Headers.Accept.Single().MediaType);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.DoesNotContain("C:\\users\\data", request.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Rejects_draft_and_prerelease_releases(bool draft, bool prerelease)
    {
        var handler = new QueueHandler(ReleaseResponse(ManifestJson, draft: draft, prerelease: prerelease));
        using var client = new GitHubReleaseClient(handler);

        UpdateOperationException error = await Assert.ThrowsAsync<UpdateOperationException>(
            () => client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data", CancellationToken.None));

        Assert.Equal(UpdateErrorKind.InvalidRelease, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Classifies_a_temporary_server_failure_as_a_network_error()
    {
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new GitHubReleaseClient(handler);

        UpdateOperationException error = await Assert.ThrowsAsync<UpdateOperationException>(
            () => client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data", CancellationToken.None));

        Assert.Equal(UpdateErrorKind.Network, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Rejects_release_asset_size_that_disagrees_with_the_manifest()
    {
        var handler = new QueueHandler(ReleaseResponse(ManifestJson, blobSize: 6), JsonResponse(ManifestJson));
        using var client = new GitHubReleaseClient(handler);

        UpdateOperationException error = await Assert.ThrowsAsync<UpdateOperationException>(
            () => client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data", CancellationToken.None));

        Assert.Equal(UpdateErrorKind.InvalidManifest, error.Kind);
    }

    [Fact]
    public async Task Sends_etag_and_uses_cached_validated_result_after_not_modified_response()
    {
        var handler = new QueueHandler(
            ReleaseResponse(ManifestJson), JsonResponse(ManifestJson),
            new HttpResponseMessage(HttpStatusCode.NotModified));
        using var client = new GitHubReleaseClient(handler);
        UpdateCheckResult first = await client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data", CancellationToken.None);
        UpdateCheckResult second = await client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data", CancellationToken.None);

        Assert.Equal(first.AvailableVersion, second.AvailableVersion);
        Assert.True(second.CheckedAtUtc >= first.CheckedAtUtc);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("\"release-v1\"", handler.Requests[2].Headers.IfNoneMatch.Single().Tag);
    }

    [Fact]
    public async Task Rejects_a_redirect_to_a_domain_outside_the_https_allow_list_before_following_it()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://example.com/credential-stealing-asset");
        var handler = new QueueHandler(ReleaseResponse(ManifestJson), redirect);
        using var client = new GitHubReleaseClient(handler);

        UpdateOperationException error = await Assert.ThrowsAsync<UpdateOperationException>(
            () => client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data", CancellationToken.None));

        Assert.Equal(UpdateErrorKind.InvalidRelease, error.Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Returns_up_to_date_without_downloading_a_manifest()
    {
        var handler = new QueueHandler(ReleaseResponse(ManifestJson));
        using var client = new GitHubReleaseClient(handler);

        UpdateCheckResult result = await client.CheckLatestAsync(Version, "C:\\app", "C:\\data", CancellationToken.None);

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.Manifest);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Rejects_release_metadata_larger_than_four_mib_before_parsing()
    {
        var oversized = new string('x', UpdateManifest.MaximumMetadataBytes + 1);
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json")
        });
        using var client = new GitHubReleaseClient(handler);

        UpdateOperationException error = await Assert.ThrowsAsync<UpdateOperationException>(
            () => client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data", CancellationToken.None));

        Assert.Equal(UpdateErrorKind.InvalidRelease, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Does_not_reuse_an_etag_validated_for_a_different_user_data_path()
    {
        var handler = new QueueHandler(ReleaseResponse(ManifestJson), JsonResponse(ManifestJson),
            ReleaseResponse(ManifestJson), JsonResponse(ManifestJson));
        using var client = new GitHubReleaseClient(handler);
        await client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data-a", CancellationToken.None);
        await client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data-b", CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Empty(handler.Requests[2].Headers.IfNoneMatch);
    }
    [Fact]
    public async Task Rejects_a_release_asset_url_for_another_repository_before_downloading_the_manifest()
    {
        var handler = new QueueHandler(ReleaseResponse(ManifestJson, blobBrowserUrl: "https://github.com/attacker/other/releases/download/v1.2.3/" + BlobName));
        using var client = new GitHubReleaseClient(handler);

        UpdateOperationException error = await Assert.ThrowsAsync<UpdateOperationException>(
            () => client.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data", CancellationToken.None));

        Assert.Equal(UpdateErrorKind.InvalidRelease, error.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Does_not_delete_a_preexisting_partial_file_when_a_download_cannot_create_its_own()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-download-partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "asset.gz");
        string partial = destination + ".partial";
        await File.WriteAllTextAsync(partial, "preserve-existing-file");
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4, 5])
        });
        using var client = new GitHubReleaseClient(handler);
        try
        {
            var asset = new UpdateAsset(12, "asset.gz", 5);
            await Assert.ThrowsAsync<UpdateOperationException>(() =>
                client.DownloadAssetAsync(asset, destination, 5, null, CancellationToken.None));
            Assert.Equal("preserve-existing-file", await File.ReadAllTextAsync(partial));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task Rejects_a_non_semver_release_tag_and_duplicate_asset_ids()
    {
        using (var badTagClient = new GitHubReleaseClient(new QueueHandler(ReleaseResponse(ManifestJson, tagName: "release-v1.2.3"))))
        {
            UpdateOperationException tagError = await Assert.ThrowsAsync<UpdateOperationException>(
                () => badTagClient.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data", CancellationToken.None));
            Assert.Equal(UpdateErrorKind.InvalidRelease, tagError.Kind);
        }

        using var duplicateIdClient = new GitHubReleaseClient(new QueueHandler(ReleaseResponse(ManifestJson, duplicateAssetId: true)));
        UpdateOperationException idError = await Assert.ThrowsAsync<UpdateOperationException>(
            () => duplicateIdClient.CheckLatestAsync("1.0.0", "C:\\app", "C:\\data", CancellationToken.None));
        Assert.Equal(UpdateErrorKind.InvalidRelease, idError.Kind);
    }

    [Fact]
    public async Task Cancelling_an_asset_transfer_removes_only_its_partial_download()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-download-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "asset.gz");
        var source = new BlockingReadStream();
        using var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(source)
        });
        handler.PeekResponse().Content.Headers.ContentLength = 5;
        using var client = new GitHubReleaseClient(handler);
        using var cancellation = new CancellationTokenSource();
        try
        {
            Task transfer = client.DownloadAssetAsync(new UpdateAsset(12, "asset.gz", 5), destination, 5, null, cancellation.Token);
            await source.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static HttpResponseMessage ReleaseResponse(string manifest, bool draft = false, bool prerelease = false, long? blobSize = null, string? blobBrowserUrl = null, string? tagName = null, bool duplicateAssetId = false)
    {
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var json = new
        {
            tag_name = tagName ?? "v" + Version,
            draft,
            prerelease,
            body = "Bug fixes",
            published_at = "2026-11-01T12:00:00Z",
            assets = new object[]
            {
                new { id = 10, name = ManifestName, size = manifestBytes.Length, state = "uploaded", browser_download_url = "https://github.com/ailuo04/clipboard-manager/releases/download/v1.2.3/" + ManifestName },
                new { id = duplicateAssetId ? 10 : 11, name = BlobName, size = blobSize ?? 5, state = "uploaded", browser_download_url = blobBrowserUrl ?? "https://github.com/ailuo04/clipboard-manager/releases/download/v1.2.3/" + BlobName }
            }
        };
        var response = JsonResponse(JsonSerializer.Serialize(json));
        response.Headers.ETag = new EntityTagHeaderValue("\"release-v1\"");
        return response;
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class BlockingReadStream : Stream
    {
        private int _readCount;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 5;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                buffer.Span[0] = 42;
                FirstRead.TrySetResult();
                return 1;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];
        public HttpResponseMessage PeekResponse() => _responses.Peek();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            if (_responses.Count == 0) throw new InvalidOperationException("Unexpected HTTP request.");
            HttpResponseMessage response = _responses.Dequeue();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }
}












