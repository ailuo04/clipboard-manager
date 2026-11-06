using System.IO;
using System.Reflection;
using ClipboardManager.Services;
using ClipboardManager.Updates;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task PrepareFailureAfterGateAcquisitionClearsPreparedState()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new UpdateService(root);
            FieldInfo prepared = typeof(UpdateService).GetField("_prepared", BindingFlags.Instance | BindingFlags.NonPublic)!;
            prepared.SetValue(service, true);

            var update = new AvailableUpdate(new UpdateManifest(), Array.Empty<byte>(), new UpdatePlan(),
                new Dictionary<string, UpdateAsset>());
            await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(update, null));

            string workspace = Path.Combine(root, UpdateFiles.WorkspaceName);
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace, "prepared"), "");
            Assert.Throws<InvalidOperationException>(service.LaunchPrepared);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CancelPreparedClearsStateWhenWorkspaceCleanupFails()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, UpdateFiles.WorkspaceName);
        Directory.CreateDirectory(workspace);
        string marker = Path.Combine(workspace, "prepared");
        File.WriteAllText(marker, "");
        using var service = new UpdateService(root);
        FieldInfo prepared = typeof(UpdateService).GetField("_prepared", BindingFlags.Instance | BindingFlags.NonPublic)!;
        prepared.SetValue(service, true);

        try
        {
            using (var lockedMarker = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.ThrowsAny<IOException>(service.CancelPrepared);

            Assert.Throws<InvalidOperationException>(service.LaunchPrepared);
        }
        finally
        {
            service.CancelPrepared();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CancelPreparedCleansMarkerEvenWhenPreparedFlagIsFalse()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, UpdateFiles.WorkspaceName);
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "prepared"), "");
            using var service = new UpdateService(root);

            service.CancelPrepared();

            Assert.False(Directory.Exists(workspace));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
