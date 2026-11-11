using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipboardManager.Core.Updates;
using ClipboardManager.UpdateWorker;
using Xunit;

namespace ClipboardManager.Update.Tests;

public sealed class TransactionRecoveryTests
{
    [Fact]
    public async Task Keeps_backups_until_the_new_version_reports_health()
    {
        using var fixture = new TransactionFixture();
        fixture.CreateInstallAndStagedVersions();
        string dataBefore = fixture.HashDataFiles();
        var transaction = fixture.CreateTransaction();

        UpdateTransactionResult result = await transaction.ApplyAsync(CancellationToken.None);

        Assert.Equal(UpdateTransactionState.HealthPending, result.State);
        Assert.Equal("new-app", await File.ReadAllTextAsync(Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe")));
        Assert.Equal("new-library", await File.ReadAllTextAsync(Path.Combine(fixture.InstallRoot, "ClipboardManager.App.dll")));
        Assert.Equal("1.1.0", JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.InstallRoot, UpdateTransaction.ManifestFileName))).RootElement.GetProperty("version").GetString());
        Assert.Equal("preserve-me", await File.ReadAllTextAsync(Path.Combine(fixture.DataRoot, "history.db")));
        Assert.True(Directory.Exists(fixture.Workspace));
        Assert.Equal(dataBefore, fixture.HashDataFiles());

        await transaction.MarkHealthyAsync(result.TransactionId, CancellationToken.None);

        Assert.Equal(UpdateTransactionState.Complete, transaction.State);
        Assert.False(Directory.Exists(fixture.Workspace));
        Assert.Equal(dataBefore, fixture.HashDataFiles());
        Assert.Equal("preserve-me", await File.ReadAllTextAsync(Path.Combine(fixture.DataRoot, "history.db")));
    }

    [Fact]
    public async Task Replacement_failure_restores_every_original_program_file_and_manifest_hash()
    {
        using var fixture = new TransactionFixture();
        fixture.CreateInstallAndStagedVersions();
        Dictionary<string, string> before = fixture.HashInstalledFiles();
        string dataBefore = fixture.HashDataFiles();
        var fileSystem = new FaultInjectingFileSystem(failReplaceCalls: [2]);
        var transaction = fixture.CreateTransaction(fileSystem);

        await Assert.ThrowsAsync<IOException>(() => transaction.ApplyAsync(CancellationToken.None));

        Assert.Equal(UpdateTransactionState.RolledBack, transaction.State);
        Assert.Equal(before, fixture.HashInstalledFiles());
        Assert.Equal(dataBefore, fixture.HashDataFiles());
        Assert.Equal("preserve-me", await File.ReadAllTextAsync(Path.Combine(fixture.DataRoot, "history.db")));
        Assert.False(Directory.Exists(fixture.Workspace));
    }

    [Fact]
    public async Task Failed_rollback_preserves_recovery_workspace_and_a_later_recovery_finishes_it()
    {
        using var fixture = new TransactionFixture();
        fixture.CreateInstallAndStagedVersions();
        Dictionary<string, string> before = fixture.HashInstalledFiles();
        string dataBefore = fixture.HashDataFiles();
        var failingFileSystem = new FaultInjectingFileSystem(failReplaceCalls: [2, 3]);
        var firstAttempt = fixture.CreateTransaction(failingFileSystem);

        await Assert.ThrowsAsync<AggregateException>(() => firstAttempt.ApplyAsync(CancellationToken.None));

        Assert.Equal(UpdateTransactionState.RollbackPending, firstAttempt.State);
        Assert.True(Directory.Exists(fixture.Workspace));
        Assert.True(File.Exists(Path.Combine(fixture.Workspace, UpdateTransaction.JournalFileName)));

        var recovery = fixture.CreateTransaction();
        UpdateTransactionResult recovered = await recovery.RecoverAsync(CancellationToken.None);

        Assert.Equal(UpdateTransactionState.RolledBack, recovered.State);
        Assert.Equal(before, fixture.HashInstalledFiles());
        Assert.Equal(dataBefore, fixture.HashDataFiles());
        Assert.False(Directory.Exists(fixture.Workspace));
        Assert.Equal("preserve-me", await File.ReadAllTextAsync(Path.Combine(fixture.DataRoot, "history.db")));
    }

    [Fact]
    public async Task Refuses_to_replace_a_managed_file_modified_after_its_installed_manifest_was_written()
    {
        using var fixture = new TransactionFixture();
        fixture.CreateInstallAndStagedVersions();
        await File.WriteAllTextAsync(Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe"), "user-modified");
        Dictionary<string, string> before = fixture.HashInstalledFiles();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateTransaction().ApplyAsync(CancellationToken.None));

        Assert.Equal(before, fixture.HashInstalledFiles());
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, UpdateTransaction.JournalFileName)));
    }

    [Fact]
    public async Task Deletes_only_manifested_obsolete_files_and_keeps_user_data_unchanged()
    {
        using var fixture = new TransactionFixture(includeObsoleteFile: true);
        fixture.CreateInstallAndStagedVersions();
        var transaction = fixture.CreateTransaction();

        UpdateTransactionResult result = await transaction.ApplyAsync(CancellationToken.None);

        Assert.Equal(UpdateTransactionState.HealthPending, result.State);
        Assert.False(File.Exists(Path.Combine(fixture.InstallRoot, "Obsolete.dll")));
        Assert.Equal("new-app", await File.ReadAllTextAsync(Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe")));
        Assert.Equal("preserve-me", await File.ReadAllTextAsync(Path.Combine(fixture.DataRoot, "history.db")));
        await transaction.RollbackAsync(CancellationToken.None);
        Assert.Equal("obsolete", await File.ReadAllTextAsync(Path.Combine(fixture.InstallRoot, "Obsolete.dll")));
        Assert.Equal("preserve-me", await File.ReadAllTextAsync(Path.Combine(fixture.DataRoot, "history.db")));
    }

    [Fact]
    public async Task Rejects_a_forged_journal_operation_not_derived_from_either_manifest()
    {
        using var fixture = new TransactionFixture();
        fixture.CreateInstallAndStagedVersions();
        string journalPath = Path.Combine(fixture.Workspace, UpdateTransaction.JournalFileName);
        var forged = new UpdateJournal(1, Guid.NewGuid(), UpdateTransactionState.Applying,
        [
            new UpdateOperation("Unmanaged.dll", UpdateOperationKind.Delete, true, new string('a', 64), 10, null, 0),
            new UpdateOperation(UpdateTransaction.ManifestFileName, UpdateOperationKind.Replace, true, new string('b', 64), 10, new string('c', 64), 10)
        ], DateTimeOffset.UtcNow);
        File.WriteAllBytes(journalPath, JsonSerializer.SerializeToUtf8Bytes(forged, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        }));
        Dictionary<string, string> before = fixture.HashInstalledFiles();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateTransaction().RecoverAsync(CancellationToken.None));

        Assert.Equal(before, fixture.HashInstalledFiles());
        Assert.True(Directory.Exists(fixture.Workspace));
    }

    [Fact]
    public async Task A_new_unmanaged_file_created_before_apply_is_never_deleted_during_rollback()
    {
        using var fixture = new TransactionFixture();
        fixture.CreateInstallAndStagedVersions();
        var fileSystem = new RaceFileSystem(fixture.InstallRoot);
        var transaction = fixture.CreateTransaction(fileSystem);

        await Assert.ThrowsAsync<AggregateException>(() => transaction.ApplyAsync(CancellationToken.None));

        Assert.Equal("user-created", await File.ReadAllTextAsync(Path.Combine(fixture.InstallRoot, "ClipboardManager.App.exe")));
        Assert.True(File.Exists(Path.Combine(fixture.Workspace, UpdateTransaction.JournalFileName)) || Directory.Exists(fixture.Workspace));
    }
    private sealed class TransactionFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cm-update-transaction-{Guid.NewGuid():N}");
        private readonly bool _includeObsoleteFile;
        private readonly string _oldApp = "old-app";
        private readonly string _oldLibrary = "old-library";
        private readonly string _newApp = "new-app";
        private readonly string _newLibrary = "new-library";

        public TransactionFixture(bool includeObsoleteFile = false)
        {
            _includeObsoleteFile = includeObsoleteFile;
            Directory.CreateDirectory(_root);
            InstallRoot = Path.Combine(_root, "app");
            DataRoot = Path.Combine(_root, "data");
            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(DataRoot);
            Workspace = Path.Combine(InstallRoot, UpdateTransaction.WorkspaceDirectoryName, Guid.NewGuid().ToString("N"));
            NewDirectory = Path.Combine(Workspace, "new");
            Directory.CreateDirectory(NewDirectory);
        }

        public string InstallRoot { get; }
        public string DataRoot { get; }
        public string Workspace { get; }
        public string NewDirectory { get; }

        public void CreateInstallAndStagedVersions()
        {
            File.WriteAllText(Path.Combine(InstallRoot, "ClipboardManager.App.exe"), _oldApp);
            File.WriteAllText(Path.Combine(InstallRoot, "ClipboardManager.App.dll"), _oldLibrary);
            var oldFiles = new List<UpdateManifestFile>
            {
                Entry("ClipboardManager.App.exe", _oldApp),
                Entry("ClipboardManager.App.dll", _oldLibrary)
            };
            if (_includeObsoleteFile)
            {
                const string obsolete = "obsolete";
                File.WriteAllText(Path.Combine(InstallRoot, "Obsolete.dll"), obsolete);
                oldFiles.Add(Entry("Obsolete.dll", obsolete));
            }
            File.WriteAllText(Path.Combine(DataRoot, "history.db"), "preserve-me");

            File.WriteAllText(Path.Combine(NewDirectory, "ClipboardManager.App.exe"), _newApp);
            File.WriteAllText(Path.Combine(NewDirectory, "ClipboardManager.App.dll"), _newLibrary);
            var oldManifest = MakeManifest("1.0.0", oldFiles);
            var newManifest = MakeManifest("1.1.0", [Entry("ClipboardManager.App.exe", _newApp), Entry("ClipboardManager.App.dll", _newLibrary)]);
            File.WriteAllBytes(Path.Combine(InstallRoot, UpdateTransaction.ManifestFileName), Serialize(oldManifest));
            File.WriteAllBytes(Path.Combine(NewDirectory, UpdateTransaction.ManifestFileName), Serialize(newManifest));
        }


        public UpdateTransaction CreateTransaction(IUpdateFileSystem? fileSystem = null)
            => new(InstallRoot, Workspace, DataRoot, fileSystem);

        public Dictionary<string, string> HashInstalledFiles()
            => Directory.EnumerateFiles(InstallRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.StartsWith(Workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(path => Path.GetRelativePath(InstallRoot, path), path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);

        public string HashDataFiles()
            => string.Join(Environment.NewLine, Directory.EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.GetRelativePath(DataRoot, path) + ":" +
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

        private static UpdateManifest MakeManifest(string version, IReadOnlyList<UpdateManifestFile> files)
            => new(1, "ClipboardManager", version, "win-x64", UpdateManifest.SupportedPackageKind, files);

        private static UpdateManifestFile Entry(string path, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            return new UpdateManifestFile(path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, bytes.Length);
        }

        private static byte[] Serialize(UpdateManifest manifest)
            => JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class RaceFileSystem(string installRoot) : PhysicalUpdateFileSystem
    {
        private int _injected;
        public override void CopyFile(string source, string destination, bool overwrite = false)
        {
            if (Interlocked.Exchange(ref _injected, 1) == 0)
                File.WriteAllText(Path.Combine(installRoot, "ClipboardManager.App.exe"), "user-created");
            base.CopyFile(source, destination, overwrite);
        }
    }
    private sealed class FaultInjectingFileSystem(params int[] failReplaceCalls) : PhysicalUpdateFileSystem
    {
        private int _replaceCalls;
        private readonly HashSet<int> _failReplaceCalls = failReplaceCalls.ToHashSet();
        public override void ReplaceFile(string source, string destination)
        {
            if (_failReplaceCalls.Contains(Interlocked.Increment(ref _replaceCalls))) throw new IOException("injected replace failure");
            base.ReplaceFile(source, destination);
        }
    }
}









