using System.Security.Cryptography;
using System.Text;
using ClipboardManager.App.Services;
using ClipboardManager.App.Utilities;
using ClipboardManager.App;
using ClipboardManager.Core.Updates;
using Xunit;

namespace ClipboardManager.Update.Tests;

public sealed class UpdateHandoffServiceTests
{
    [Fact]
    public async Task Copies_verified_payload_to_install_volume_starts_helper_and_requests_app_exit()
    {
        using var fixture = new Fixture();
        fixture.Create();
        var launcher = new FakeWorkerLauncher();
        var window = new FakeWindowService();
        var service = new UpdateHandoffService(fixture.Paths, window, launcher, fixture.InstallRoot, Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe"));

        await service.StartAsync(fixture.Manifest, fixture.StagingDirectory, CancellationToken.None);
        string workspace = launcher.Starts.Single().Arguments[1];

        Assert.True(Directory.Exists(workspace));
        Assert.Equal("new-app", File.ReadAllText(Path.Combine(workspace, "new", "ClipboardManager.App.exe")));
        Assert.Equal("new-worker", File.ReadAllText(Path.Combine(workspace, "new", "ClipboardManager.UpdateWorker.exe")));
        Assert.True(File.Exists(Path.Combine(workspace, "new", UpdateTransaction.ManifestFileName)));
        Assert.Equal("old-worker", File.ReadAllText(Path.Combine(workspace, "ClipboardManager.UpdateWorker.exe")));
        Assert.Single(launcher.Starts);
        Assert.Equal(workspace, launcher.Starts[0].Arguments[1]);
        Assert.Equal(fixture.InstallRoot, launcher.Starts[0].Arguments[2]);
        Assert.Equal(fixture.Paths.RootDirectory, launcher.Starts[0].Arguments[5]);
        Assert.Equal(Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe"), launcher.Starts[0].Arguments[6]);
        Assert.Equal(1, window.ExitRequests);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(fixture.Paths.RootDirectory, "history.db")));
    }

    [Fact]
    public async Task Invalid_or_missing_verified_payload_never_starts_helper_or_exits_app()
    {
        using var fixture = new Fixture();
        fixture.Create();
        File.Delete(Path.Combine(fixture.StagingDirectory, "ClipboardManager.App.exe"));
        var launcher = new FakeWorkerLauncher();
        var window = new FakeWindowService();
        var service = new UpdateHandoffService(fixture.Paths, window, launcher, fixture.InstallRoot, Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe"));

        await Assert.ThrowsAsync<UpdateOperationException>(() => service.StartAsync(fixture.Manifest, fixture.StagingDirectory, CancellationToken.None));

        Assert.Empty(launcher.Starts);
        Assert.Equal(0, window.ExitRequests);
        Assert.False(Directory.Exists(Path.Combine(fixture.InstallRoot, UpdateProtocol.WorkspaceDirectoryName)) && Directory.EnumerateFileSystemEntries(Path.Combine(fixture.InstallRoot, UpdateProtocol.WorkspaceDirectoryName)).Any());
    }

    [Fact]
    public async Task Worker_launch_failure_cleans_its_handoff_workspace_and_keeps_the_app_open()
    {
        using var fixture = new Fixture();
        fixture.Create();
        var launcher = new FakeWorkerLauncher { Failure = new IOException("synthetic launch failure") };
        var window = new FakeWindowService();
        var service = new UpdateHandoffService(fixture.Paths, window, launcher, fixture.InstallRoot, Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe"));

        await Assert.ThrowsAsync<UpdateOperationException>(() => service.StartAsync(fixture.Manifest, fixture.StagingDirectory, CancellationToken.None));

        Assert.Equal(0, window.ExitRequests);
        Assert.False(Directory.Exists(Path.Combine(fixture.InstallRoot, UpdateProtocol.WorkspaceDirectoryName)) && Directory.EnumerateFileSystemEntries(Path.Combine(fixture.InstallRoot, UpdateProtocol.WorkspaceDirectoryName)).Any());
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe")));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cm-handoff-test-{Guid.NewGuid():N}");
        public string InstallRoot { get; private set; } = "";
        public AppPaths Paths { get; private set; } = null!;
        public string StagingDirectory { get; private set; } = "";
        public UpdateManifest Manifest { get; private set; } = null!;

        public void Create()
        {
            InstallRoot = Path.Combine(_root, "install");
            string dataRoot = Path.Combine(_root, "data");
            string stagingRoot = Path.Combine(_root, "staging");
            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(stagingRoot);
            Paths = new AppPaths(dataRoot, stagingRoot);
            File.WriteAllText(Path.Combine(InstallRoot, "ClipboardManager.App.exe"), "old-app");
            File.WriteAllText(Path.Combine(InstallRoot, "ClipboardManager.UpdateWorker.exe"), "old-worker");
            File.WriteAllText(Path.Combine(dataRoot, "history.db"), "preserve");
            File.WriteAllText(Path.Combine(InstallRoot, UpdateTransaction.ManifestFileName), Serialize(ManifestFor("1.0.0", "old-app", "old-worker")));
            StagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(StagingDirectory);
            File.WriteAllText(Path.Combine(StagingDirectory, "ClipboardManager.App.exe"), "new-app");
            File.WriteAllText(Path.Combine(StagingDirectory, "ClipboardManager.UpdateWorker.exe"), "new-worker");
            Manifest = ManifestFor("1.1.0", "new-app", "new-worker");
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

        private static UpdateManifest ManifestFor(string version, string app, string worker)
            => new(1, "ClipboardManager", version, "win-x64", UpdateManifest.SupportedPackageKind,
            [Entry("ClipboardManager.App.exe", app), Entry("ClipboardManager.UpdateWorker.exe", worker)]);

        private static UpdateManifestFile Entry(string path, string contents)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(contents);
            return new UpdateManifestFile(path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, bytes.Length);
        }

        private static string Serialize(UpdateManifest manifest)
            => System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }

    private sealed class FakeWorkerLauncher : IUpdateWorkerLauncher
    {
        public List<(string ExecutablePath, string WorkingDirectory, IReadOnlyList<string> Arguments)> Starts { get; } = [];
        public Exception? Failure { get; init; }
        public void Start(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
        {
            if (Failure is not null) throw Failure;
            Starts.Add((executablePath, workingDirectory, arguments));
        }
    }

    private sealed class FakeWindowService : IAppWindowService
    {
        public int ExitRequests { get; private set; }
        public Task ShowMainWindowAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void ApplySettings(ClipboardManager.Core.Settings.AppSettings settings, bool isLightTheme) { }
        public void ApplyTheme(string preference, bool isLightTheme) { }
        public void HideAfterCopyIfEnabled() { }
        public void NotifyHistoryChanged() { }
        public Task OpenDataDirectoryAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public void RequestExit() => ExitRequests++;
    }
}






