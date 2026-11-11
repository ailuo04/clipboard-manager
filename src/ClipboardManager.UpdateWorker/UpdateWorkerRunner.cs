using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ClipboardManager.Core.Updates;

namespace ClipboardManager.UpdateWorker;

public interface IUpdateWorkerRuntime
{
    IDisposable AcquireMutex(string name, TimeSpan timeout);
    void WaitForPreviousApplication(int processId, long startTimeUtcTicks, TimeSpan timeout);
    UpdateProcessIdentity StartProcess(string executablePath, string workingDirectory, IReadOnlyList<string> arguments);
    bool WaitForHealthReady(string markerPath, UpdateHealthMarker expected, UpdateProcessIdentity expectedProcess, TimeSpan timeout);
    void StopProcessIfMatches(UpdateProcessIdentity process, TimeSpan timeout);
}

public sealed class SystemUpdateWorkerRuntime : IUpdateWorkerRuntime
{
    public IDisposable AcquireMutex(string name, TimeSpan timeout)
    {
        var mutex = new Mutex(false, name);
        bool entered;
        try { entered = mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered)
        {
            mutex.Dispose();
            throw new TimeoutException("The update worker lock could not be acquired.");
        }
        return new MutexLease(mutex);
    }

    public void WaitForPreviousApplication(int processId, long startTimeUtcTicks, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        if (processId > 0 && startTimeUtcTicks > 0)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.StartTime.ToUniversalTime().Ticks == startTimeUtcTicks && !process.WaitForExit(ToMilliseconds(timeout)))
                    throw new TimeoutException("The previous application did not exit in time.");
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        TimeSpan remaining = timeout - timer.Elapsed;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("The previous application did not exit in time.");
        using var mutex = new Mutex(false, UpdateProtocol.SingleInstanceMutexName);
        bool entered;
        try { entered = mutex.WaitOne(remaining); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered) throw new TimeoutException("The application single-instance lock remained occupied.");
        mutex.ReleaseMutex();
    }

    public UpdateProcessIdentity StartProcess(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException("The application process could not be started.");
        return new UpdateProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
    }

    public bool WaitForHealthReady(string markerPath, UpdateHealthMarker expected, UpdateProcessIdentity expectedProcess, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (File.Exists(markerPath))
            {
                try
                {
                    UpdateHealthMarker? ready = JsonSerializer.Deserialize<UpdateHealthMarker>(File.ReadAllBytes(markerPath), JsonOptions);
                    if (ready is not null && ready.TransactionId == expected.TransactionId &&
                        string.Equals(ready.Token, expected.Token, StringComparison.Ordinal)) return true;
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
            if (!IsProcessRunning(expectedProcess)) return false;
            Thread.Sleep(100);
        }
        return false;
    }

    private static bool IsProcessRunning(UpdateProcessIdentity expectedProcess)
    {
        try
        {
            using Process process = Process.GetProcessById(expectedProcess.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == expectedProcess.StartTimeUtcTicks && !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void StopProcessIfMatches(UpdateProcessIdentity process, TimeSpan timeout)
    {
        try
        {
            using Process candidate = Process.GetProcessById(process.ProcessId);
            if (candidate.StartTime.ToUniversalTime().Ticks != process.StartTimeUtcTicks || candidate.HasExited) return;
            candidate.Kill(entireProcessTree: false);
            if (!candidate.WaitForExit(ToMilliseconds(timeout)))
                throw new TimeoutException("The unconfirmed application process could not be stopped.");
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static int ToMilliseconds(TimeSpan value) => (int)Math.Clamp(value.TotalMilliseconds, 1, int.MaxValue);

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;
        public void Dispose()
        {
            Mutex? owned = Interlocked.Exchange(ref _mutex, null);
            if (owned is null) return;
            try { owned.ReleaseMutex(); }
            finally { owned.Dispose(); }
        }
    }
}

public sealed class UpdateWorkerRunner
{
    public static readonly TimeSpan PreviousApplicationTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan StartupHealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IUpdateWorkerRuntime _runtime;
    private readonly IUpdateFileSystem _fileSystem;
    private readonly string? _expectedUserDataRoot;
    private readonly string? _workerExecutablePath;

    public UpdateWorkerRunner(IUpdateWorkerRuntime? runtime = null, IUpdateFileSystem? fileSystem = null,
        string? expectedUserDataRoot = null, string? workerExecutablePath = null)
    {
        _runtime = runtime ?? new SystemUpdateWorkerRuntime();
        _fileSystem = fileSystem ?? new PhysicalUpdateFileSystem();
        _expectedUserDataRoot = expectedUserDataRoot is null ? null : Path.GetFullPath(expectedUserDataRoot);
        _workerExecutablePath = workerExecutablePath is null ? null : Path.GetFullPath(workerExecutablePath);
    }

    public int Run(UpdateWorkerCommand command)
    {
        Guid correlationId = Guid.NewGuid();
        string phase = "validate";
        UpdateTransaction? transaction = null;
        UpdateProcessIdentity? newProcess = null;
        bool previousApplicationStopped = false;
        UpdateWorkerCommand validated = command;
        bool commandValidated = false;
        try
        {
            validated = ValidateCommand(command);
            commandValidated = true;
            string lockName = CreateUpdateMutexName(validated.InstallRoot);
            using IDisposable updateLock = _runtime.AcquireMutex(lockName, TimeSpan.Zero);
            phase = "wait-for-application-exit";
            _runtime.WaitForPreviousApplication(validated.PreviousProcessId, validated.PreviousProcessStartTicks,
                PreviousApplicationTimeout);
            previousApplicationStopped = true;
            transaction = new UpdateTransaction(validated.InstallRoot, validated.Workspace, validated.UserDataDirectory, _fileSystem);
            phase = "apply";
            UpdateTransactionResult applied = transaction.ApplyAsync(CancellationToken.None).GetAwaiter().GetResult();
            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var health = new UpdateHealthMarker(applied.TransactionId, token);
            string requestPath = Path.Combine(validated.Workspace, UpdateProtocol.HealthRequestFileName);
            string readyPath = Path.Combine(validated.Workspace, UpdateProtocol.HealthReadyFileName);
            _fileSystem.WriteAtomic(requestPath, JsonSerializer.SerializeToUtf8Bytes(health, JsonOptions));
            phase = "launch-updated-application";
            newProcess = _runtime.StartProcess(validated.ApplicationExecutablePath, validated.InstallRoot,
                [UpdateProtocol.HealthArgument, health.TransactionId.ToString("N"), health.Token]);
            phase = "startup-health-check";
            if (_runtime.WaitForHealthReady(readyPath, health, newProcess, StartupHealthTimeout))
            {
                transaction.MarkHealthyAsync(health.TransactionId, CancellationToken.None).GetAwaiter().GetResult();
                return 0;
            }

            phase = "startup-health-timeout";
            _runtime.StopProcessIfMatches(newProcess, TimeSpan.FromSeconds(10));
            _runtime.WaitForPreviousApplication(newProcess.ProcessId, newProcess.StartTimeUtcTicks, PreviousApplicationTimeout);
            transaction.RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();
            WriteDiagnostic(validated, applied.TransactionId, phase, typeof(TimeoutException), correlationId);
            _runtime.StartProcess(validated.ApplicationExecutablePath, validated.InstallRoot, []);
            return 1;
        }
        catch (Exception exception)
        {
            Guid transactionId = transaction?.TransactionId ?? Guid.Empty;
            if (previousApplicationStopped && transaction is not null && commandValidated)
            {
                try
                {
                    if (newProcess is not null)
                    {
                        _runtime.StopProcessIfMatches(newProcess, TimeSpan.FromSeconds(10));
                        _runtime.WaitForPreviousApplication(newProcess.ProcessId, newProcess.StartTimeUtcTicks, PreviousApplicationTimeout);
                    }
                    if (transaction.State is UpdateTransactionState.HealthPending or UpdateTransactionState.RollbackPending ||
                        _fileSystem.FileExists(Path.Combine(validated.Workspace, UpdateTransaction.JournalFileName)))
                        transaction.RecoverAsync(CancellationToken.None).GetAwaiter().GetResult();
                    WriteDiagnostic(validated, transactionId, phase, exception.GetType(), correlationId);
                    _runtime.StartProcess(validated.ApplicationExecutablePath, validated.InstallRoot, []);
                }
                catch (Exception recoveryException)
                {
                    WriteDiagnostic(validated, transactionId, "rollback", recoveryException.GetType(), correlationId);
                    return 1;
                }
            }
            else if (previousApplicationStopped && transaction is null && commandValidated)
            {
                WriteDiagnostic(validated, transactionId, phase, exception.GetType(), correlationId);
                try { _runtime.StartProcess(validated.ApplicationExecutablePath, validated.InstallRoot, []); }
                catch (Exception restartException) { WriteDiagnostic(validated, transactionId, "restart-restored-application", restartException.GetType(), correlationId); }
            }
            else if (commandValidated)
            {
                WriteDiagnostic(validated, transactionId, phase, exception.GetType(), correlationId);
            }
            else
            {
                WindowsApplicationEventLog.TryWrite("Clipboard Manager update failed during " + phase + ".", correlationId);
            }
            return 1;
        }
    }

    private UpdateWorkerCommand ValidateCommand(UpdateWorkerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.InstallRoot));
        string workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.Workspace));
        string expectedWorkspaceParent = Path.Combine(install, UpdateTransaction.WorkspaceDirectoryName);
        string? workspaceParent = Path.GetDirectoryName(workspace);
        string workspaceName = Path.GetFileName(workspace);
        if (workspaceParent is null || !Path.TrimEndingDirectorySeparator(workspaceParent).Equals(expectedWorkspaceParent, PathComparison) ||
            workspaceName.Length != 32 || !Guid.TryParseExact(workspaceName, "N", out _) ||
            !string.Equals(Path.GetPathRoot(install), Path.GetPathRoot(workspace), PathComparison))
            throw new InvalidDataException("The update workspace is outside the approved installation directory.");
        string dataRoot = Path.GetFullPath(command.UserDataDirectory);
        string expectedData = _expectedUserDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardManager");
        if (!dataRoot.Equals(Path.GetFullPath(expectedData), PathComparison) || IsWithin(install, dataRoot) || IsWithin(workspace, dataRoot) || IsWithin(dataRoot, workspace))
            throw new InvalidDataException("The update worker cannot target or overlap user data.");
        string app = Path.GetFullPath(command.ApplicationExecutablePath);
        if (!IsWithin(app, install) || !File.Exists(app) || _fileSystem.IsReparsePoint(app))
            throw new InvalidDataException("The application restart path is outside the installation or linked.");
        string? workerPathValue = _workerExecutablePath ?? Environment.ProcessPath;
        string workerPath = workerPathValue is null ? string.Empty : Path.GetFullPath(workerPathValue);
        if (workerPath.Length > 0 && !IsWithin(workerPath, workspace))
            throw new InvalidDataException("The update worker executable is outside the transaction workspace.");
        return command with { InstallRoot = install, Workspace = workspace, UserDataDirectory = dataRoot, ApplicationExecutablePath = app };
    }

    private void WriteDiagnostic(UpdateWorkerCommand command, Guid transactionId, string phase, Type exceptionType, Guid correlationId)
    {
        try
        {
            if (IsSafeDiagnosticWorkspace(command))
            {
                Directory.CreateDirectory(command.Workspace);
                if (!_fileSystem.IsReparsePoint(command.Workspace))
                {
                    var diagnostic = new UpdateWorkerDiagnostic(DateTimeOffset.UtcNow, transactionId, phase,
                        exceptionType.FullName ?? exceptionType.Name, correlationId);
                    _fileSystem.WriteAtomic(Path.Combine(command.Workspace, UpdateProtocol.WorkerErrorFileName),
                        JsonSerializer.SerializeToUtf8Bytes(diagnostic, JsonOptions));
                }
            }
        }
        catch { }
        WindowsApplicationEventLog.TryWrite("Clipboard Manager update failed during " + phase + ".", correlationId);
    }

    private bool IsSafeDiagnosticWorkspace(UpdateWorkerCommand command)
    {
        string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.InstallRoot));
        string workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.Workspace));
        string expectedParent = Path.Combine(install, UpdateTransaction.WorkspaceDirectoryName);
        if (!Path.GetDirectoryName(workspace)!.Equals(expectedParent, PathComparison) ||
            Path.GetFileName(workspace).Length != 32 || !Guid.TryParseExact(Path.GetFileName(workspace), "N", out _) ||
            !string.Equals(Path.GetPathRoot(install), Path.GetPathRoot(workspace), PathComparison) ||
            IsWithin(install, command.UserDataDirectory) || IsWithin(command.UserDataDirectory, install)) return false;

        string current = Path.GetPathRoot(install)!;
        if (_fileSystem.IsReparsePoint(current)) return false;
        foreach (string segment in Path.GetRelativePath(current, install).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (_fileSystem.IsReparsePoint(current)) return false;
        }
        string parent = Path.Combine(install, UpdateTransaction.WorkspaceDirectoryName);
        if (!Directory.Exists(parent) || _fileSystem.IsReparsePoint(parent) || _fileSystem.IsReparsePoint(workspace)) return false;
        if (File.Exists(workspace)) return false;
        string diagnostic = Path.Combine(workspace, UpdateProtocol.WorkerErrorFileName);
        if (_fileSystem.IsReparsePoint(diagnostic)) return false;
        return true;
    }
    private static string CreateUpdateMutexName(string installRoot)
        => "Local\\ClipboardManager.Update." + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(installRoot)))[..16];
    private static bool IsWithin(string path, string root)
    {
        StringComparison comparison = PathComparison;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, comparison) || path.StartsWith(prefix, comparison);
    }
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}






