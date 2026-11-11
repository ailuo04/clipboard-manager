using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ClipboardManager.Core.Updates;

namespace ClipboardManager.App.Services;

/// <summary>Reads official releases from one fixed public GitHub repository and streams assets without implicit redirects.</summary>
public sealed class GitHubReleaseClient : IUpdateReleaseService, IDisposable
{
    public const string RepositoryOwner = "ailuo04";
    public const string RepositoryName = "clipboard-manager";
    private static readonly Uri ApiRoot = new("https://api.github.com/");
    private static readonly HashSet<string> AllowedAssetHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"
    };
    private readonly HttpClient _http;
    private readonly object _cacheGate = new();
    private string? _etag;
    private UpdateCheckResult? _cached;
    private string? _cachedInstallDirectory;
    private string? _cachedUserDataDirectory;

    public GitHubReleaseClient(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ClipboardManager-Update/1.0");
    }

    public async Task<UpdateCheckResult> CheckLatestAsync(string currentVersion, string installDirectory,
        string userDataDirectory, CancellationToken cancellationToken)
    {
        if (!UpdateManifest.IsVersion(currentVersion))
            throw new ArgumentException("Current version must use numeric major.minor.patch form.", nameof(currentVersion));
        var releaseUri = new Uri(ApiRoot, $"repos/{RepositoryOwner}/{RepositoryName}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, releaseUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        string installRoot = Path.GetFullPath(installDirectory);
        string dataRoot = Path.GetFullPath(userDataDirectory);
        string? knownEtag;
        lock (_cacheGate)
            knownEtag = _cached is not null && string.Equals(_cached.CurrentVersion, currentVersion, StringComparison.Ordinal) &&
                string.Equals(_cachedInstallDirectory, installRoot, PathComparison) && string.Equals(_cachedUserDataDirectory, dataRoot, PathComparison) ? _etag : null;
        if (EntityTagHeaderValue.TryParse(knownEtag, out EntityTagHeaderValue? etag)) request.Headers.IfNoneMatch.Add(etag);

        using HttpResponseMessage response = await SendFollowingRedirectsAsync(request, ApiRoot, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            lock (_cacheGate)
            {
                if (_cached is not null && string.Equals(_cached.CurrentVersion, currentVersion, StringComparison.Ordinal) &&
                    string.Equals(_cachedInstallDirectory, installRoot, PathComparison) && string.Equals(_cachedUserDataDirectory, dataRoot, PathComparison))
                {
                    _cached = _cached with { CheckedAtUtc = DateTimeOffset.UtcNow };
                    return _cached;
                }
            }
            throw new UpdateOperationException(UpdateErrorKind.Network, "The release service returned 304 without a validated cache.");
        }
        if (response.StatusCode != HttpStatusCode.OK)
            throw new UpdateOperationException(UpdateErrorKind.Network, "Could not read the official release metadata.");

        byte[] releaseBytes = await ReadBoundedContentAsync(response.Content, UpdateManifest.MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        ReleaseDescriptor release = ParseRelease(releaseBytes);
        int versionOrder = CompareVersions(release.Version, currentVersion);
        UpdateCheckResult result;
        if (versionOrder <= 0)
        {
            result = new UpdateCheckResult(currentVersion, null, string.Empty, 0, DateTimeOffset.UtcNow, null,
                new Dictionary<string, UpdateAsset>(StringComparer.Ordinal));
        }
        else
        {
            string manifestName = $"ClipboardManager-{release.Version}-win-x64-update.json";
            if (!release.Assets.TryGetValue(manifestName, out UpdateAsset? manifestAsset) ||
                manifestAsset.Size is <= 0 or > UpdateManifest.MaximumMetadataBytes)
                throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "The release has no valid update manifest asset.");

            byte[] manifestBytes = await ReadAssetBytesAsync(manifestAsset, UpdateManifest.MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
            if (manifestBytes.LongLength != manifestAsset.Size)
                throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "Update manifest attachment size does not match its metadata.");
            UpdateManifest manifest;
            try
            {
                manifest = UpdateManifest.ParseAndValidate(manifestBytes, release.Version, "win-x64", installDirectory, userDataDirectory);
            }
            catch (InvalidDataException exception)
            {
                throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "The release update manifest did not pass validation.", exception);
            }

            var contentAssets = new Dictionary<string, UpdateAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (UpdateManifestFile file in manifest.Files)
            {
                string assetName = $"update-{file.Sha256}.gz";
                if (!release.Assets.TryGetValue(assetName, out UpdateAsset? asset) || asset.Size != file.CompressedSize)
                    throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "An update blob is missing or has a mismatched size.");
                if (contentAssets.TryGetValue(file.Sha256, out UpdateAsset? existing) && existing.Id != asset.Id)
                    throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "A content hash maps to inconsistent release attachments.");
                contentAssets[file.Sha256] = asset;
            }
            long downloadSize = 0;
            try { foreach (UpdateAsset asset in contentAssets.Values) downloadSize = checked(downloadSize + asset.Size); }
            catch (OverflowException exception) { throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "Release attachment sizes exceed supported limits.", exception); }
            result = new UpdateCheckResult(currentVersion, release.Version, release.Notes, downloadSize,
                DateTimeOffset.UtcNow, manifest, contentAssets);
        }

        lock (_cacheGate)
        {
            _cached = result;
            _cachedInstallDirectory = installRoot;
            _cachedUserDataDirectory = dataRoot;
            _etag = response.Headers.ETag?.ToString();
        }
        return result;
    }

    public async Task DownloadAssetAsync(UpdateAsset asset, string destinationPath, long expectedSize,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (asset.Id <= 0 || expectedSize < 0 || expectedSize > UpdateManifest.MaximumFileBytes || asset.Size != expectedSize)
            throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "The requested release attachment is invalid.");

        string destination = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(destination);
        if (directory is null) throw new UpdateOperationException(UpdateErrorKind.DownloadFailed, "The update destination is invalid.");
        Directory.CreateDirectory(directory);
        string partial = destination + ".partial";
        bool createdPartial = false;
        try
        {
            var assetApiUri = new Uri(ApiRoot, $"repos/{RepositoryOwner}/{RepositoryName}/releases/assets/{asset.Id}");
            using var request = new HttpRequestMessage(HttpMethod.Get, assetApiUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using HttpResponseMessage response = await SendFollowingRedirectsAsync(request, ApiRoot, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new UpdateOperationException(UpdateErrorKind.DownloadFailed, "The release attachment could not be downloaded.");
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != expectedSize)
                throw new UpdateOperationException(UpdateErrorKind.VerificationFailed, "The downloaded attachment size does not match the release metadata.");

            long transferred = 0;
            await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                createdPartial = true;
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    transferred = checked(transferred + read);
                    if (transferred > expectedSize)
                        throw new UpdateOperationException(UpdateErrorKind.VerificationFailed, "The downloaded attachment exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new UpdateProgress(transferred, expectedSize, asset.Name));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (transferred != expectedSize)
                throw new UpdateOperationException(UpdateErrorKind.VerificationFailed, "The downloaded attachment is truncated.");
            File.Move(partial, destination);
        }
        catch (OperationCanceledException)
        {
            if (createdPartial) TryDelete(partial);
            throw;
        }
        catch (Exception exception)
        {
            if (createdPartial) TryDelete(partial);
            if (exception is UpdateOperationException) throw;
            throw new UpdateOperationException(UpdateErrorKind.DownloadFailed, "The release attachment download failed.", exception);
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<byte[]> ReadAssetBytesAsync(UpdateAsset asset, int maxBytes, CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(Path.GetTempPath(), $"ClipboardManager-{Guid.NewGuid():N}.manifest");
        try
        {
            await DownloadAssetAsync(asset, temporary, Math.Min(asset.Size, maxBytes), null, cancellationToken).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateOperationException exception) when (exception.Kind == UpdateErrorKind.VerificationFailed)
        {
            throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "The manifest attachment size is invalid.", exception);
        }
        finally { TryDelete(temporary); }
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(HttpRequestMessage initialRequest, Uri apiOrigin,
        CancellationToken cancellationToken)
    {
        Uri current = initialRequest.RequestUri ?? throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "An HTTP request has no destination.");
        for (int redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            if (!IsAllowedRequestUri(current, apiOrigin))
                throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "A release attachment points outside the trusted HTTPS sources.");
            using var request = new HttpRequestMessage(initialRequest.Method, current);
            bool sameApiHost = current.Host.Equals(apiOrigin.Host, StringComparison.OrdinalIgnoreCase);
            foreach (var header in initialRequest.Headers)
            {
                if (sameApiHost || header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { throw new UpdateOperationException(UpdateErrorKind.Network, "The update service request failed.", exception); }
            if (!IsRedirect(response.StatusCode)) return response;
            Uri? next = response.Headers.Location is Uri location ? (location.IsAbsoluteUri ? location : new Uri(current, location)) : null;
            response.Dispose();
            if (next is null || redirectCount == 5 || !IsAllowedRequestUri(next, apiOrigin))
                throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "The release service returned an untrusted redirect.");
            current = next;
        }
        throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "The release service returned too many redirects.");
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsAllowedRequestUri(Uri uri, Uri apiOrigin)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443) return false;
        if (uri.Host.Equals(apiOrigin.Host, StringComparison.OrdinalIgnoreCase))
            return uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
                   uri.AbsolutePath.StartsWith($"/repos/{RepositoryOwner}/{RepositoryName}/", StringComparison.Ordinal);
        if (!AllowedAssetHosts.Contains(uri.Host)) return false;
        return !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.StartsWith($"/{RepositoryOwner}/{RepositoryName}/releases/download/", StringComparison.Ordinal);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<byte[]> ReadBoundedContentAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && (length < 0 || length > maxBytes))
            throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "Release metadata exceeds the configured size limit.");
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream(Math.Min(maxBytes, (int)(content.Headers.ContentLength ?? 0)));
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (result.Length + read > maxBytes)
                throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "Release metadata exceeds the configured size limit.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    private static ReleaseDescriptor ParseRelease(byte[] json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "The release metadata is not an object.");
            EnsureNoDuplicateProperties(root);
            string tag = ReadString(root, "tag_name");
            if (!tag.StartsWith('v') || !UpdateManifest.IsVersion(tag[1..]) || ReadRequiredBoolean(root, "draft") || ReadRequiredBoolean(root, "prerelease"))
                throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "The latest release is not a valid stable version.");
            string version = tag[1..];
            string notes = root.TryGetProperty("body", out JsonElement body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty;
            if (notes.Length > 256 * 1024) notes = notes[..(256 * 1024)];
            var assets = new Dictionary<string, UpdateAsset>(StringComparer.Ordinal);
            var ids = new HashSet<long>();
            if (!root.TryGetProperty("assets", out JsonElement items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 10_000)
                throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "The release asset list is invalid.");
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "The release contains a non-object attachment.");
                EnsureNoDuplicateProperties(item);
                long id = ReadInt64(item, "id");
                string name = ReadString(item, "name");
                long size = ReadInt64(item, "size");
                string state = ReadString(item, "state");
                string urlText = ReadString(item, "browser_download_url");
                if (id <= 0 || size < 0 || !string.Equals(state, "uploaded", StringComparison.Ordinal) ||
                    !Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url) || !AllowedAssetHosts.Contains(url.Host) || url.Scheme != Uri.UriSchemeHttps || url.Port != 443 || !string.IsNullOrEmpty(url.UserInfo) || !IsValidBrowserDownloadUrl(url, version, name) ||
                    !ids.Add(id) || !assets.TryAdd(name, new UpdateAsset(id, name, size)))
                    throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "The release contains invalid or duplicate attachment metadata.");
            }
            return new ReleaseDescriptor(version, notes, assets);
        }
        catch (JsonException exception)
        {
            throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "Release metadata is not valid JSON.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new UpdateOperationException(UpdateErrorKind.InvalidRelease, "Release metadata is incomplete.", exception);
        }
    }

    private static bool IsValidBrowserDownloadUrl(Uri url, string version, string assetName)
    {
        if (!url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || url.Query.Length != 0 || url.Fragment.Length != 0) return false;
        string prefix = $"/{RepositoryOwner}/{RepositoryName}/releases/download/v{version}/";
        return url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) &&
               Uri.UnescapeDataString(url.AbsolutePath[prefix.Length..]).Equals(assetName, StringComparison.Ordinal);
    }

    private static int CompareVersions(string left, string right)
    {
        Version.TryParse(left, out Version? leftVersion);
        Version.TryParse(right, out Version? rightVersion);
        return leftVersion!.CompareTo(rightVersion);
    }

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Release metadata contains duplicate JSON properties.");
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidDataException($"Missing {name}.");
    private static bool ReadRequiredBoolean(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new InvalidDataException($"Missing {name}.");
    private static long ReadInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : throw new InvalidDataException($"Missing {name}.");
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed record ReleaseDescriptor(string Version, string Notes, IReadOnlyDictionary<string, UpdateAsset> Assets);
}






















