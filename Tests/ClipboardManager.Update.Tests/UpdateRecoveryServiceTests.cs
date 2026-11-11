using System.Text.Json;
using ClipboardManager.App.Services;
using ClipboardManager.App.Utilities;
using ClipboardManager.Core.Updates;
using Xunit;

namespace ClipboardManager.Update.Tests;

public sealed class UpdateRecoveryServiceTests
{
    [Fact]
    public async Task Preserves_a_legacy_wpf_array_journal_without_parsing_it_as_a_winui_transaction()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-legacy-journal-{Guid.NewGuid():N}");
        string install = Path.Combine(root, "install");
        string data = Path.Combine(root, "data");
        string workspace = Path.Combine(install, UpdateProtocol.WorkspaceDirectoryName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        string journal = Path.Combine(workspace, UpdateTransaction.JournalFileName);
        string journalJson = JsonSerializer.Serialize(new[] { new { path = UpdateTransaction.ManifestFileName, hadOriginal = true, deleteOnly = false, sha256 = new string('a', 64) } });
        await File.WriteAllTextAsync(journal, journalJson);
        try
        {
            var service = new UpdateRecoveryService(new AppPaths(data), new AppLogger(Path.Combine(data, "app.log")),
                installRoot: install, arguments: []);

            await service.RecoverIfNeededAsync(CancellationToken.None);

            Assert.Equal(journalJson, await File.ReadAllTextAsync(journal));
            Assert.True(Directory.Exists(workspace));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task Unrelated_activation_arguments_do_not_erase_process_health_arguments()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-health-activation-{Guid.NewGuid():N}");
        string install = Path.Combine(root, "install");
        string data = Path.Combine(root, "data");
        Guid transactionId = Guid.NewGuid();
        string workspace = Path.Combine(install, UpdateProtocol.WorkspaceDirectoryName, transactionId.ToString("N"));
        string token = new string('a', 64);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        await File.WriteAllBytesAsync(Path.Combine(workspace, UpdateProtocol.HealthRequestFileName),
            JsonSerializer.SerializeToUtf8Bytes(new UpdateHealthMarker(transactionId, token),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await File.WriteAllTextAsync(Path.Combine(workspace, UpdateTransaction.JournalFileName), "process-managed-journal");
        try
        {
            var service = new UpdateRecoveryService(new AppPaths(data), new AppLogger(Path.Combine(data, "app.log")),
                installRoot: install, arguments: [UpdateProtocol.HealthArgument, transactionId.ToString("N"), token]);
            service.SetLaunchArguments(string.Empty);
            service.SetLaunchArguments("--unrelated activation");

            await service.RecoverIfNeededAsync(CancellationToken.None);
            await service.MarkStartupHealthyAsync(CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(workspace, UpdateProtocol.HealthReadyFileName)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task Keeps_a_valid_worker_diagnostic_after_recovering_a_rolled_back_workspace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-recovery-service-{Guid.NewGuid():N}");
        string install = Path.Combine(root, "install");
        string data = Path.Combine(root, "data");
        string workspace = Path.Combine(install, UpdateProtocol.WorkspaceDirectoryName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        string diagnosticPath = Path.Combine(workspace, UpdateProtocol.WorkerErrorFileName);
        var diagnostic = new UpdateWorkerDiagnostic(DateTimeOffset.UtcNow, Guid.NewGuid(), "startup-health-timeout",
            "System.TimeoutException", Guid.NewGuid());
        await File.WriteAllBytesAsync(diagnosticPath, JsonSerializer.SerializeToUtf8Bytes(diagnostic,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        try
        {
            var service = new UpdateRecoveryService(new AppPaths(data), new AppLogger(Path.Combine(data, "app.log")),
                installRoot: install, arguments: []);

            await service.RecoverIfNeededAsync(CancellationToken.None);

            Assert.True(File.Exists(diagnosticPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
