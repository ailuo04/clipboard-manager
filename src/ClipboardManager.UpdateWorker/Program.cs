namespace ClipboardManager.UpdateWorker;

public static class Program
{
    public const int InvalidArgumentsExitCode = 2;

    public static int Main(string[] args)
    {
        if (!UpdateWorkerCommand.TryParse(args, out UpdateWorkerCommand? command) || command is null)
            return InvalidArgumentsExitCode;
        return new UpdateWorkerRunner().Run(command);
    }
}
