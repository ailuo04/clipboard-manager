using System.IO;
using System.Reflection;
using ClipboardManager.Services;
using ClipboardManager.Updates;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task PrepareFailureClearsPreparedStateBeforeReturning()
    {
        using var service = new UpdateService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        FieldInfo prepared = typeof(UpdateService).GetField("_prepared", BindingFlags.Instance | BindingFlags.NonPublic)!;
        prepared.SetValue(service, true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var update = new AvailableUpdate(new UpdateManifest(), Array.Empty<byte>(), new UpdatePlan(),
            new Dictionary<string, UpdateAsset>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(update, null, cancellation.Token));

        Assert.False((bool)prepared.GetValue(service)!);
    }
}
