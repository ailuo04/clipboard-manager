using System.Security.Cryptography;
using System.Text;
using ClipboardManager.App.Services;
using ClipboardManager.App.Utilities;
using ClipboardManager.App.ViewModels;
using ClipboardManager.Core.Updates;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class UpdatesViewModelTests
{
    [Fact]
    public async Task Check_maps_release_metadata_to_version_size_notes_and_status()
    {
        using var fixture = new Fixture();

        await fixture.ViewModel.CheckForUpdatesAsync();

        Assert.Equal("1.0.0", fixture.ViewModel.CurrentVersion);
        Assert.Equal("1.1.0", fixture.ViewModel.AvailableVersion);
        Assert.Equal("修复更新与启动稳定性。", fixture.ViewModel.ReleaseNotes);
        Assert.Contains("2.0 MB", fixture.ViewModel.DownloadSizeText, StringComparison.Ordinal);
        Assert.NotNull(fixture.ViewModel.LastCheckedText);
        Assert.Contains("新版本", fixture.ViewModel.StatusText, StringComparison.Ordinal);
        Assert.True(fixture.ViewModel.CanDownload);
    }

    [Fact]
    public async Task Concurrent_check_requests_start_only_one_release_operation()
    {
        using var fixture = new Fixture();
        fixture.Releases.BlockChecks = true;

        Task first = fixture.ViewModel.CheckForUpdatesAsync();
        await fixture.Releases.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.ViewModel.CheckForUpdatesAsync();

        Assert.Equal(1, fixture.Releases.CheckCalls);
        fixture.Releases.ReleaseCheck.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(UpdateState.Available, fixture.ViewModel.State);
    }

    [Fact]
    public async Task Duplicate_download_is_blocked_and_active_download_can_be_cancelled()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.CheckForUpdatesAsync();
        fixture.Packages.BlockDownload = true;

        Task first = fixture.ViewModel.DownloadAndUpdateAsync();
        await fixture.Packages.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.ViewModel.DownloadAndUpdateAsync();

        Assert.Equal(1, fixture.Packages.DownloadCalls);
        Assert.Equal(UpdateState.Downloading, fixture.ViewModel.State);
        Assert.True(fixture.ViewModel.CanCancel);
        Assert.True(fixture.ViewModel.CancelDownload());
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(UpdateState.Cancelled, fixture.ViewModel.State);
        Assert.True(fixture.ViewModel.CanDownload);
    }

    [Fact]
    public async Task Duplicate_action_during_verification_does_not_start_a_second_download()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.CheckForUpdatesAsync();
        fixture.Packages.BlockVerify = true;

        Task first = fixture.ViewModel.DownloadAndUpdateAsync();
        await fixture.Packages.VerifyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.ViewModel.DownloadAndUpdateAsync();

        Assert.Equal(1, fixture.Packages.DownloadCalls);
        Assert.Equal(1, fixture.Packages.VerifyCalls);
        Assert.Equal(UpdateState.Verifying, fixture.ViewModel.State);
        Assert.True(fixture.ViewModel.IsVerifying);
        Assert.True(fixture.ViewModel.CanCancel);
        Assert.True(fixture.ViewModel.ProgressPercent is >= 0 and <= 100);
        Assert.True(fixture.ViewModel.CancelDownload());
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(UpdateState.Cancelled, fixture.ViewModel.State);
    }
    [Fact]
    public async Task Verification_failure_exposes_stage_recovery_copy_and_retry_action()
    {
        using var fixture = new Fixture();
        fixture.Packages.VerifyFailure = new UpdateOperationException(UpdateErrorKind.VerificationFailed, "synthetic failure");
        await fixture.ViewModel.CheckForUpdatesAsync();

        await fixture.ViewModel.DownloadAndUpdateAsync();

        Assert.Equal(UpdateState.Error, fixture.ViewModel.State);
        Assert.Equal(UpdateState.Verifying, fixture.ViewModel.ErrorStage);
        Assert.True(fixture.ViewModel.CanRetry);
        Assert.Contains("校验", fixture.ViewModel.StageText, StringComparison.Ordinal);
        Assert.Contains("重试", fixture.ViewModel.DownloadButtonText, StringComparison.Ordinal);
        Assert.Contains("校验", fixture.ViewModel.RecoveryMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auto_check_preference_is_saved_and_reverts_when_persistence_fails()
    {
        using var fixture = new Fixture();

        await fixture.ViewModel.SetAutoCheckUpdatesAsync(false);

        Assert.False(fixture.ViewModel.AutoCheckUpdates);
        Assert.Equal(1, fixture.PreferenceSaveCalls);
        Assert.Contains("已关闭", fixture.ViewModel.AutoCheckMessage, StringComparison.Ordinal);

        fixture.PreferenceSaveFailure = true;
        await fixture.ViewModel.SetAutoCheckUpdatesAsync(true);

        Assert.False(fixture.ViewModel.AutoCheckUpdates);
        Assert.Equal(2, fixture.PreferenceSaveCalls);
        Assert.Contains("保存失败", fixture.ViewModel.AutoCheckMessage, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cm-updates-vm-{Guid.NewGuid():N}");
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var manifest = new UpdateManifest(1, "ClipboardManager", "1.1.0", "win-x64", UpdateManifest.SupportedPackageKind,
                [Entry("ClipboardManager.App.exe", "synthetic update bytes")]);
            var assets = new Dictionary<string, UpdateAsset>(StringComparer.OrdinalIgnoreCase)
            {
                [manifest.Files[0].Sha256] = new(17, "update-app.gz", 2 * 1024 * 1024)
            };
            Releases = new FakeReleaseService(new UpdateCheckResult("1.0.0", "1.1.0", "修复更新与启动稳定性。",
                2 * 1024 * 1024, DateTimeOffset.UtcNow, manifest, assets));
            Packages = new FakePackageService();
            Coordinator = new UpdateCoordinator(Releases, Packages, new FakeApplyService(), new AppPaths(Path.Combine(_root, "data")),
                new AppLogger(Path.Combine(_root, "app.log"), "1.0.0"), "1.0.0");
            ViewModel = new UpdatesViewModel(Coordinator, autoCheckUpdates: true, SavePreferenceAsync);
        }

        public FakeReleaseService Releases { get; }
        public FakePackageService Packages { get; }
        public UpdateCoordinator Coordinator { get; }
        public UpdatesViewModel ViewModel { get; }
        public int PreferenceSaveCalls { get; private set; }
        public bool PreferenceSaveFailure { get; set; }

        private Task<UpdateCheckPreferenceResult> SavePreferenceAsync(bool enabled, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            PreferenceSaveCalls++;
            return Task.FromResult(PreferenceSaveFailure
                ? new UpdateCheckPreferenceResult(false, false, "自动检查设置保存失败，请重试。")
                : new UpdateCheckPreferenceResult(enabled, true, enabled ? "自动检查已开启。" : "自动检查已关闭。"));
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            Coordinator.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static UpdateManifestFile Entry(string path, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            return new UpdateManifestFile(path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, bytes.Length);
        }
    }

    private sealed class FakeReleaseService(UpdateCheckResult result) : IUpdateReleaseService
    {
        public int CheckCalls { get; private set; }
        public bool BlockChecks { get; set; }
        public TaskCompletionSource CheckStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCheck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UpdateCheckResult> CheckLatestAsync(string currentVersion, string installDirectory, string userDataDirectory, CancellationToken cancellationToken)
        {
            CheckCalls++;
            CheckStarted.TrySetResult();
            if (BlockChecks) await ReleaseCheck.Task.WaitAsync(cancellationToken);
            return result with { CurrentVersion = currentVersion };
        }

        public Task DownloadAssetAsync(UpdateAsset asset, string destinationPath, long expectedSize, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakePackageService : IUpdatePackageService
    {
        public int DownloadCalls { get; private set; }
        public bool BlockDownload { get; set; }
        public Exception? VerifyFailure { get; set; }
        public bool BlockVerify { get; set; }
        public int VerifyCalls { get; private set; }
        public TaskCompletionSource VerifyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CreateStagingDirectory(string stagingRoot) => Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        public async Task DownloadAsync(UpdateCheckResult update, string stagingDirectory, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            DownloadStarted.TrySetResult();
            progress?.Report(new UpdateProgress(update.DownloadSize / 2, update.DownloadSize, "update-app.gz"));
            if (BlockDownload) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public async Task VerifyAndExtractAsync(UpdateCheckResult update, string stagingDirectory, CancellationToken cancellationToken)
        {
            VerifyCalls++;
            VerifyStarted.TrySetResult();
            if (BlockVerify) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (VerifyFailure is not null) throw VerifyFailure;
        }
        public void Cleanup(string stagingDirectory) { }
    }

    private sealed class FakeApplyService : IUpdateApplyService
    {
        public Task StartAsync(UpdateManifest manifest, string verifiedStagingDirectory, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
