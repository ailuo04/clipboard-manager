using ClipboardManager.App.Services;
using ClipboardManager.App.Utilities;
using ClipboardManager.Core.Updates;
using Xunit;

namespace ClipboardManager.Update.Tests;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public async Task Automatic_style_check_only_discovers_an_update_and_never_downloads_it()
    {
        using var fixture = new Fixture();
        using UpdateCoordinator coordinator = fixture.CreateCoordinator();

        await coordinator.CheckForUpdatesAsync();

        Assert.Equal(UpdateState.Available, coordinator.State);
        Assert.True(coordinator.CurrentUpdate!.IsUpdateAvailable);
        Assert.Equal(0, fixture.Package.DownloadCalls);
        Assert.Null(coordinator.StagedDirectory);
    }

    [Fact]
    public async Task Explicit_download_verifies_package_and_stops_at_ready_to_apply()
    {
        using var fixture = new Fixture();
        using UpdateCoordinator coordinator = fixture.CreateCoordinator();
        await coordinator.CheckForUpdatesAsync();

        await coordinator.DownloadAndVerifyAsync();

        Assert.Equal(UpdateState.Applying, coordinator.State);
        Assert.Equal(1, fixture.Package.DownloadCalls);
        Assert.Equal(1, fixture.Package.VerifyCalls);
        Assert.Equal(1, fixture.Package.CleanupCalls);
        Assert.Null(coordinator.StagedDirectory);
        Assert.Equal(1, fixture.Apply.Calls);
    }

    [Fact]
    public async Task Cancelling_download_cancels_work_and_removes_the_incomplete_staging_directory()
    {
        using var fixture = new Fixture();
        fixture.Package.BlockDownload = true;
        using UpdateCoordinator coordinator = fixture.CreateCoordinator();
        await coordinator.CheckForUpdatesAsync();
        Task operation = coordinator.DownloadAndVerifyAsync();
        await fixture.Package.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(coordinator.CancelDownload());
        await operation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(UpdateState.Cancelled, coordinator.State);
        Assert.Equal(1, fixture.Package.CleanupCalls);
        Assert.Null(coordinator.StagedDirectory);
    }

    [Fact]
    public async Task Verification_failure_reports_a_recovery_error_and_cleans_the_workspace()
    {
        using var fixture = new Fixture();
        fixture.Package.VerifyFailure = new UpdateOperationException(UpdateErrorKind.VerificationFailed, "synthetic sensitive clipboard sample");
        using UpdateCoordinator coordinator = fixture.CreateCoordinator();
        await coordinator.CheckForUpdatesAsync();

        await coordinator.DownloadAndVerifyAsync();

        Assert.Equal(UpdateState.Error, coordinator.State);
        Assert.Equal(UpdateErrorKind.VerificationFailed, coordinator.ErrorKind);
        Assert.True(coordinator.CanRetry);
        Assert.Equal(1, fixture.Package.CleanupCalls);
        Assert.DoesNotContain("synthetic sensitive clipboard sample", File.ReadAllText(fixture.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_verification_removes_the_staging_directory()
    {
        using var fixture = new Fixture();
        fixture.Package.BlockVerify = true;
        using UpdateCoordinator coordinator = fixture.CreateCoordinator();
        await coordinator.CheckForUpdatesAsync();
        Task operation = coordinator.DownloadAndVerifyAsync();
        await fixture.Package.VerifyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(coordinator.CancelDownload());
        await operation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(UpdateState.Cancelled, coordinator.State);
        Assert.Equal(1, fixture.Package.CleanupCalls);
        Assert.Null(coordinator.StagedDirectory);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cm-update-test-{Guid.NewGuid():N}");
        public FakePackageService Package { get; } = new();
        public FakeUpdateApplyService Apply { get; } = new();
        public string LogPath => Path.Combine(_root, "test.log");

        public UpdateCoordinator CreateCoordinator()
        {
            Directory.CreateDirectory(_root);
            return new UpdateCoordinator(new FakeReleaseService(Result()), Package, Apply, new AppPaths(Path.Combine(_root, "data")),
                new AppLogger(LogPath, "1.0.0"), "1.0.0");
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

        private static UpdateCheckResult Result()
        {
            var file = new UpdateManifestFile("ClipboardManager.exe", new string('a', 64), 10, 5);
            var manifest = new UpdateManifest(1, "ClipboardManager", "1.1.0", "win-x64", "winui3", [file]);
            var assets = new Dictionary<string, UpdateAsset>(StringComparer.OrdinalIgnoreCase)
            {
                [file.Sha256] = new(7, $"update-{file.Sha256}.gz", 5)
            };
            return new UpdateCheckResult("1.0.0", "1.1.0", "notes", 5, DateTimeOffset.UtcNow, manifest, assets);
        }
    }

    private sealed class FakeUpdateApplyService : IUpdateApplyService
    {
        public int Calls { get; private set; }
        public Task StartAsync(UpdateManifest manifest, string verifiedStagingDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
    private sealed class FakeReleaseService(UpdateCheckResult result) : IUpdateReleaseService
    {
        public Task<UpdateCheckResult> CheckLatestAsync(string currentVersion, string installDirectory, string userDataDirectory, CancellationToken cancellationToken)
            => Task.FromResult(result with { CurrentVersion = currentVersion });
        public Task DownloadAssetAsync(UpdateAsset asset, string destinationPath, long expectedSize, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakePackageService : IUpdatePackageService
    {
        public int DownloadCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        public int CleanupCalls { get; private set; }
        public bool BlockDownload { get; set; }
        public bool BlockVerify { get; set; }
        public Exception? VerifyFailure { get; set; }
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource VerifyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string CreateStagingDirectory(string stagingRoot) => Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        public async Task DownloadAsync(UpdateCheckResult update, string stagingDirectory, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            DownloadStarted.TrySetResult();
            if (BlockDownload) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public Task VerifyAndExtractAsync(UpdateCheckResult update, string stagingDirectory, CancellationToken cancellationToken)
        {
            VerifyCalls++;
            VerifyStarted.TrySetResult();
            if (VerifyFailure is not null) return Task.FromException(VerifyFailure);
            return BlockVerify ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken) : Task.CompletedTask;
        }
        public void Cleanup(string stagingDirectory) => CleanupCalls++;
    }
}







