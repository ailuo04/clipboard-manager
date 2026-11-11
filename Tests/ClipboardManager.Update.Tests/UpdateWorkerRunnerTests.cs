using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipboardManager.Core.Updates;
using ClipboardManager.UpdateWorker;
using Xunit;

namespace ClipboardManager.Update.Tests;

public sealed class UpdateWorkerRunnerTests
{
    [Fact]
    public void Applies_update_waits_for_health_then_cleans_transaction_backup()
    {
        using var fixture = new RunnerFixture();
        fixture.Create();
        var runtime = new FakeRuntime(reportHealth: true);
        var runner = fixture.CreateRunner(runtime);

        int exitCode = runner.Run(fixture.Command);

        Assert.Equal(0, exitCode);
        Assert.Equal("new-app", File.ReadAllText(Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe")));
        Assert.Equal("1.1.0", JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.InstallRoot, UpdateTransaction.ManifestFileName))).RootElement.GetProperty("version").GetString());
        Assert.False(Directory.Exists(fixture.Workspace));
        Assert.Single(runtime.Started);
        Assert.Equal(new UpdateProcessIdentity(42, 1234), runtime.HealthProcess);
        Assert.Equal(UpdateProtocol.HealthArgument, runtime.Started[0].Arguments[0]);
        Assert.Equal(runtime.Started[0].Arguments[1], runtime.ReadyMarker!.TransactionId.ToString("N"));
        Assert.Equal(runtime.Started[0].Arguments[2], runtime.ReadyMarker.Token);
    }

    [Fact]
    public void Journal_write_failure_leaves_installation_unchanged_and_restarts_the_old_app()
    {
        using var fixture = new RunnerFixture();
        fixture.Create();
        Dictionary<string, string> before = Directory.EnumerateFiles(fixture.InstallRoot)
            .Where(path => !path.Contains(UpdateTransaction.WorkspaceDirectoryName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        var runtime = new FakeRuntime(reportHealth: true);

        int result = fixture.CreateRunner(runtime, new FailJournalWriteFileSystem()).Run(fixture.Command);

        Assert.Equal(1, result);
        Dictionary<string, string> after = Directory.EnumerateFiles(fixture.InstallRoot)
            .Where(path => !path.Contains(UpdateTransaction.WorkspaceDirectoryName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(before, after);
        Assert.Single(runtime.Started);
        Assert.Empty(runtime.Stopped);
        Assert.True(File.Exists(Path.Combine(fixture.Workspace, UpdateProtocol.WorkerErrorFileName)));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(fixture.DataRoot, "history.db")));
    }
    [Fact]
    public void Invalid_workspace_arguments_do_not_write_diagnostics_to_an_untrusted_directory()
    {
        using var fixture = new RunnerFixture();
        fixture.Create();
        string untrusted = Path.Combine(Path.GetTempPath(), $"cm-untrusted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(untrusted);
        try
        {
            var runner = fixture.CreateRunner(new FakeRuntime(reportHealth: false));

            Assert.Equal(1, runner.Run(fixture.Command with { Workspace = untrusted }));

            Assert.False(File.Exists(Path.Combine(untrusted, UpdateProtocol.WorkerErrorFileName)));
        }
        finally
        {
            Directory.Delete(untrusted, recursive: true);
        }
    }
    [Fact]
    public void Health_timeout_stops_only_the_launched_process_rolls_back_and_restarts_old_version()
    {
        using var fixture = new RunnerFixture();
        fixture.Create();
        var runtime = new FakeRuntime(reportHealth: false);
        var runner = fixture.CreateRunner(runtime);

        int exitCode = runner.Run(fixture.Command);

        Assert.Equal(1, exitCode);
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe")));
        Assert.True(File.Exists(Path.Combine(fixture.InstallRoot, UpdateTransaction.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(fixture.Workspace, UpdateProtocol.WorkerErrorFileName)));
        using (JsonDocument diagnostic = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Workspace, UpdateProtocol.WorkerErrorFileName))) )
            Assert.Equal("startup-health-timeout", diagnostic.RootElement.GetProperty("phase").GetString());
        Assert.Equal(2, runtime.Started.Count);
        Assert.Single(runtime.Stopped);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(fixture.DataRoot, "history.db")));
    }

    [Fact]
    public void Application_exit_timeout_leaves_installed_program_and_user_data_hashes_unchanged()
    {
        using var fixture = new RunnerFixture();
        fixture.Create();
        Dictionary<string, string> programBefore = HashFiles(fixture.InstallRoot);
        string userDataPath = Path.Combine(fixture.DataRoot, "history.db");
        string userDataBefore = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(userDataPath)));
        var runtime = new FakeRuntime(reportHealth: true, failWaitForPrevious: true);

        int exitCode = fixture.CreateRunner(runtime).Run(fixture.Command);

        Assert.Equal(1, exitCode);
        Assert.Equal(programBefore, HashFiles(fixture.InstallRoot));
        Assert.Equal(userDataBefore, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(userDataPath))));
        Assert.Empty(runtime.Started);
    }

    private static Dictionary<string, string> HashFiles(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(UpdateTransaction.WorkspaceDirectoryName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(directory, path),
                path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);

    private sealed class RunnerFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cm-worker-runner-{Guid.NewGuid():N}");
        public string InstallRoot { get; private set; } = "";
        public string Workspace { get; private set; } = "";
        public string DataRoot { get; private set; } = "";
        public string ApplicationPath => Path.Combine(InstallRoot, "ClipboardManager.App.exe");
        public string WorkerPath => Path.Combine(Workspace, "ClipboardManager.UpdateWorker.exe");
        public UpdateWorkerCommand Command => new(Workspace, InstallRoot, 15, 1_234, DataRoot, ApplicationPath);

        public void Create()
        {
            InstallRoot = Path.Combine(_root, "app");
            Workspace = Path.Combine(InstallRoot, UpdateTransaction.WorkspaceDirectoryName, Guid.NewGuid().ToString("N"));
            DataRoot = Path.Combine(_root, "data");
            string newRoot = Path.Combine(Workspace, "new");
            Directory.CreateDirectory(newRoot);
            Directory.CreateDirectory(DataRoot);
            File.WriteAllText(ApplicationPath, "old-app");
            File.WriteAllText(Path.Combine(InstallRoot, UpdateTransaction.ManifestFileName), Serialize(Manifest("1.0.0", "old-app")));
            File.WriteAllText(Path.Combine(DataRoot, "history.db"), "preserve");
            File.WriteAllText(Path.Combine(newRoot, "ClipboardManager.App.exe"), "new-app");
            File.WriteAllText(Path.Combine(newRoot, UpdateTransaction.ManifestFileName), Serialize(Manifest("1.1.0", "new-app")));
            File.WriteAllText(WorkerPath, "worker-copy");
        }

        public UpdateWorkerRunner CreateRunner(FakeRuntime runtime, IUpdateFileSystem? fileSystem = null)
            => new(runtime, fileSystem ?? new PhysicalUpdateFileSystem(), DataRoot, WorkerPath);

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

        private static UpdateManifest Manifest(string version, string contents)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(contents);
            return new UpdateManifest(1, "ClipboardManager", version, "win-x64", UpdateManifest.SupportedPackageKind,
                [new UpdateManifestFile("ClipboardManager.App.exe", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, bytes.Length)]);
        }

        private static string Serialize(UpdateManifest manifest)
            => JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class FailJournalWriteFileSystem : PhysicalUpdateFileSystem
    {
        public override void WriteAtomic(string path, ReadOnlySpan<byte> contents)
        {
            if (path.EndsWith(UpdateTransaction.JournalFileName, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("synthetic read-only update workspace");
            base.WriteAtomic(path, contents);
        }
    }
    private sealed class FakeRuntime(bool reportHealth, bool failWaitForPrevious = false) : IUpdateWorkerRuntime
    {
        public List<(string Path, IReadOnlyList<string> Arguments)> Started { get; } = [];
        public List<UpdateProcessIdentity> Stopped { get; } = [];
        public UpdateHealthMarker? ReadyMarker { get; private set; }
        public UpdateProcessIdentity? HealthProcess { get; private set; }

        public IDisposable AcquireMutex(string name, TimeSpan timeout) => new NoopLease();
        public void WaitForPreviousApplication(int processId, long startTimeUtcTicks, TimeSpan timeout)
        {
            if (failWaitForPrevious) throw new TimeoutException("synthetic app-exit timeout");
        }
        public UpdateProcessIdentity StartProcess(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
        {
            Started.Add((executablePath, arguments));
            return new UpdateProcessIdentity(Started.Count == 1 ? 42 : 43, Started.Count == 1 ? 1234 : 2345);
        }
        public bool WaitForHealthReady(string markerPath, UpdateHealthMarker expected, UpdateProcessIdentity expectedProcess, TimeSpan timeout)
        {
            HealthProcess = expectedProcess;
            if (!reportHealth) return false;
            ReadyMarker = expected;
            File.WriteAllText(markerPath, JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return true;
        }
        public void StopProcessIfMatches(UpdateProcessIdentity process, TimeSpan timeout) => Stopped.Add(process);

        private sealed class NoopLease : IDisposable { public void Dispose() { } }
    }
}






