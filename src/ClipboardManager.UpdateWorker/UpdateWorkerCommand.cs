using System.Diagnostics;

namespace ClipboardManager.UpdateWorker;

public sealed record UpdateWorkerCommand(
    string Workspace,
    string InstallRoot,
    int PreviousProcessId,
    long PreviousProcessStartTicks,
    string UserDataDirectory,
    string ApplicationExecutablePath)
{
    public static bool TryParse(string[] args, out UpdateWorkerCommand? command)
    {
        command = null;
        if (args is null || args.Length != 7 || !string.Equals(args[0], "--apply", StringComparison.Ordinal)) return false;
        if (!int.TryParse(args[3], out int processId) || processId < 0 ||
            !long.TryParse(args[4], out long startTicks) || startTicks < 0) return false;
        try
        {
            command = new UpdateWorkerCommand(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), processId,
                startTicks, Path.GetFullPath(args[5]), Path.GetFullPath(args[6]));
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }
}

public sealed record UpdateProcessIdentity(int ProcessId, long StartTimeUtcTicks);
