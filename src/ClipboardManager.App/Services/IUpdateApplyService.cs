using ClipboardManager.Core.Updates;
namespace ClipboardManager.App.Services;

public interface IUpdateWorkerLauncher
{
    void Start(string executablePath, string workingDirectory, IReadOnlyList<string> arguments);
}

public sealed class ProcessUpdateWorkerLauncher : IUpdateWorkerLauncher
{
    public void Start(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new System.Diagnostics.ProcessStartInfo(executablePath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        _ = System.Diagnostics.Process.Start(start) ?? throw new IOException("The update worker could not be started.");
    }
}

public interface IUpdateApplyService
{
    Task StartAsync(UpdateManifest manifest, string verifiedStagingDirectory,
        CancellationToken cancellationToken);
}

