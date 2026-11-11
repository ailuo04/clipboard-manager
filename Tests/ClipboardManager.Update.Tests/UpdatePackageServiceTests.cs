using System.IO.Compression;
using System.Security.Cryptography;
using ClipboardManager.App.Services;
using ClipboardManager.App.Utilities;
using ClipboardManager.Core.Updates;
using Xunit;

namespace ClipboardManager.Update.Tests;

public sealed class UpdatePackageServiceTests
{
    [Fact]
    public async Task Downloads_each_unique_blob_once_verifies_hashes_and_extracts_only_manifest_files()
    {
        using var fixture = new Fixture();
        byte[] contents = "verified payload"u8.ToArray();
        byte[] compressed = Gzip(contents);
        string hash = Convert.ToHexStringLower(SHA256.HashData(contents));
        UpdateCheckResult update = MakeUpdate(
            new UpdateManifestFile("ClipboardManager.exe", hash, contents.Length, compressed.Length),
            new UpdateManifestFile("subfolder/duplicate.bin", hash, contents.Length, compressed.Length));
        var release = new FakeReleaseService(compressed);
        var service = new UpdatePackageService(release, fixture.Paths);
        string workspace = service.CreateStagingDirectory(fixture.Paths.UpdateStagingRoot);
        try
        {
            var progress = new List<UpdateProgress>();
            await service.DownloadAsync(update, workspace, new InlineProgress(value => progress.Add(value)), CancellationToken.None);
            await service.VerifyAndExtractAsync(update, workspace, CancellationToken.None);

            Assert.Equal(1, release.DownloadCalls);
            Assert.Equal(compressed.LongLength, progress[^1].BytesTransferred);
            Assert.Equal(update.DownloadSize, progress[^1].TotalBytes);
            Assert.Equal(contents, await File.ReadAllBytesAsync(Path.Combine(workspace, "ClipboardManager.exe")));
            Assert.Equal(contents, await File.ReadAllBytesAsync(Path.Combine(workspace, "subfolder", "duplicate.bin")));
            Assert.False(Directory.Exists(Path.Combine(workspace, ".blobs")));
        }
        finally { service.Cleanup(workspace); }
    }

    [Fact]
    public async Task Rejects_payload_hash_mismatch_before_it_becomes_a_staged_program_file()
    {
        using var fixture = new Fixture();
        byte[] contents = "untrusted payload"u8.ToArray();
        byte[] compressed = Gzip(contents);
        string expectedHash = new string('a', 64);
        UpdateCheckResult update = MakeUpdate(new UpdateManifestFile("ClipboardManager.exe", expectedHash, contents.Length, compressed.Length));
        var service = new UpdatePackageService(new FakeReleaseService(compressed), fixture.Paths);
        string workspace = service.CreateStagingDirectory(fixture.Paths.UpdateStagingRoot);
        try
        {
            var progress = new List<UpdateProgress>();
            await service.DownloadAsync(update, workspace, new InlineProgress(value => progress.Add(value)), CancellationToken.None);

            UpdateOperationException error = await Assert.ThrowsAsync<UpdateOperationException>(
                () => service.VerifyAndExtractAsync(update, workspace, CancellationToken.None));

            Assert.Equal(UpdateErrorKind.VerificationFailed, error.Kind);
            Assert.False(File.Exists(Path.Combine(workspace, "ClipboardManager.exe")));
            Assert.False(File.Exists(Path.Combine(workspace, "ClipboardManager.exe.partial")));
        }
        finally { service.Cleanup(workspace); }
    }

    [Fact]
    public async Task Does_not_delete_a_preexisting_partial_payload_when_verification_cannot_create_its_own()
    {
        using var fixture = new Fixture();
        byte[] contents = "expected verified payload"u8.ToArray();
        byte[] compressed = Gzip(contents);
        string hash = Convert.ToHexStringLower(SHA256.HashData(contents));
        UpdateCheckResult update = MakeUpdate(new UpdateManifestFile("folder/ClipboardManager.exe", hash, contents.Length, compressed.Length));
        var service = new UpdatePackageService(new FakeReleaseService(compressed), fixture.Paths);
        string workspace = service.CreateStagingDirectory(fixture.Paths.UpdateStagingRoot);
        string directory = Path.Combine(workspace, "folder");
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(directory, "ClipboardManager.exe.partial");
        await File.WriteAllTextAsync(partial, "preserve-existing-file");
        try
        {
            var progress = new List<UpdateProgress>();
            await service.DownloadAsync(update, workspace, new InlineProgress(value => progress.Add(value)), CancellationToken.None);
            await Assert.ThrowsAsync<UpdateOperationException>(() => service.VerifyAndExtractAsync(update, workspace, CancellationToken.None));
            Assert.Equal("preserve-existing-file", await File.ReadAllTextAsync(partial));
        }
        finally { service.Cleanup(workspace); }
    }
    private static UpdateCheckResult MakeUpdate(params UpdateManifestFile[] files)
    {
        var manifest = new UpdateManifest(1, "ClipboardManager", "1.1.0", "win-x64", "winui3", files);
        var assets = files.DistinctBy(file => file.Sha256).ToDictionary(file => file.Sha256,
            file => new UpdateAsset(files.ToList().IndexOf(file) + 1, $"update-{file.Sha256}.gz", file.CompressedSize), StringComparer.OrdinalIgnoreCase);
        return new UpdateCheckResult("1.0.0", "1.1.0", "notes", assets.Values.Sum(asset => asset.Size), DateTimeOffset.UtcNow, manifest, assets);
    }

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(content);
        return output.ToArray();
    }

    [Fact]
    public async Task Rejects_a_package_when_required_space_exceeds_available_space_without_changing_user_data()
    {
        using var fixture = new Fixture();
        AppPaths paths = fixture.Paths;
        Directory.CreateDirectory(paths.RootDirectory);
        string sentinel = Path.Combine(paths.RootDirectory, "synthetic-history.db");
        await File.WriteAllTextAsync(sentinel, "synthetic-user-data-must-remain-unchanged");
        string beforeHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(sentinel)));
        var service = new UpdatePackageService(new FakeReleaseService([]), paths);
        string workspace = service.CreateStagingDirectory(paths.UpdateStagingRoot);
        try
        {
            UpdateCheckResult update = MakeUpdate(new UpdateManifestFile("ClipboardManager.exe", new string('a', 64), 10, 5))
                with { DownloadSize = long.MaxValue - 20 };

            UpdateOperationException error = await Assert.ThrowsAsync<UpdateOperationException>(
                () => service.DownloadAsync(update, workspace, null, CancellationToken.None));

            Assert.Equal(UpdateErrorKind.InsufficientDiskSpace, error.Kind);
            Assert.Empty(Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories));
            string afterHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(sentinel)));
            Assert.Equal(beforeHash, afterHash);
        }
        finally { service.Cleanup(workspace); }
    }

    [Fact]
    public void Rejects_an_update_staging_root_inside_the_user_data_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-update-data-overlap-{Guid.NewGuid():N}");
        var paths = new AppPaths(root, Path.Combine(root, "updates"));

        Assert.Throws<ArgumentException>(() => new UpdatePackageService(new FakeReleaseService([]), paths));
    }
    [Fact]
    public async Task Rejects_a_staging_root_that_becomes_a_reparse_point_before_download()
    {
        using var fixture = new Fixture();
        bool linked = false;
        var service = new UpdatePackageService(new FakeReleaseService([]), fixture.Paths,
            path => linked && path.Equals(fixture.Paths.UpdateStagingRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        string workspace = service.CreateStagingDirectory(fixture.Paths.UpdateStagingRoot);
        linked = true;
        try
        {
            UpdateOperationException error = await Assert.ThrowsAsync<UpdateOperationException>(
                () => service.DownloadAsync(MakeUpdate(new UpdateManifestFile("ClipboardManager.exe", new string('a', 64), 1, 1)),
                    workspace, null, CancellationToken.None));

            Assert.Equal(UpdateErrorKind.DownloadFailed, error.Kind);
        }
        finally { linked = false; Directory.Delete(workspace, recursive: true); }
    }
    private sealed class InlineProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cm-update-package-test-{Guid.NewGuid():N}");
        public Fixture() => Directory.CreateDirectory(_root);
        public AppPaths Paths => new(Path.Combine(_root, "data"), Path.Combine(_root, "staging"));
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }

    private sealed class FakeReleaseService(byte[] compressed) : IUpdateReleaseService
    {
        public int DownloadCalls { get; private set; }
        public Task<UpdateCheckResult> CheckLatestAsync(string currentVersion, string installDirectory, string userDataDirectory, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public async Task DownloadAssetAsync(UpdateAsset asset, string destinationPath, long expectedSize, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCalls++;
            Assert.Equal(expectedSize, compressed.LongLength);
            await File.WriteAllBytesAsync(destinationPath, compressed, cancellationToken);
            progress?.Report(new UpdateProgress(expectedSize, expectedSize, asset.Name));
        }
    }
}







