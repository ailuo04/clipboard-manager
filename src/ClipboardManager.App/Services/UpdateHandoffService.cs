using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ClipboardManager.App.Utilities;
using ClipboardManager.Core.Updates;

namespace ClipboardManager.App.Services;

/// <summary>Promotes already verified files into an install-volume workspace and hands off to the isolated worker.</summary>
public sealed class UpdateHandoffService : IUpdateApplyService
{
    private const string ApplicationExecutableName = "ClipboardManager.App.exe";
    private const string WorkerExecutableName = "ClipboardManager.UpdateWorker.exe";
    private const string NewDirectoryName = "new";
    private const string ManifestFileName = "update-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly IAppWindowService _window;
    private readonly IUpdateWorkerLauncher _launcher;
    private readonly string _installRoot;
    private readonly string? _applicationExecutablePath;

    public UpdateHandoffService(AppPaths paths, IAppWindowService window, IUpdateWorkerLauncher launcher,
        string? installRoot = null, string? applicationExecutablePath = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot ?? AppContext.BaseDirectory));
        _applicationExecutablePath = applicationExecutablePath is null ? null : Path.GetFullPath(applicationExecutablePath);
        if (IsWithin(_paths.RootDirectory, _installRoot) || IsWithin(_installRoot, _paths.RootDirectory))
            throw new ArgumentException("The program installation and clipboard user-data directory must not overlap.", nameof(paths));
    }

    public Task StartAsync(UpdateManifest manifest, string verifiedStagingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        string staging = ValidateStagingDirectory(verifiedStagingDirectory);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        UpdateManifest validated = UpdateManifest.ParseAndValidate(manifestBytes, manifest.Version, "win-x64",
            _installRoot, _paths.RootDirectory, IsReparsePoint);

        string currentManifestPath = Path.Combine(_installRoot, ManifestFileName);
        UpdateManifest current = LoadInstalledManifest(currentManifestPath);
        UpdateManifestFile workerEntry = current.Files.SingleOrDefault(file => file.Path.Equals(WorkerExecutableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new UpdateOperationException(UpdateErrorKind.InstallDirectoryNotWritable, "The installed update worker is not listed in the current manifest.");
        string workerSource = SafeCombine(_installRoot, WorkerExecutableName);
        VerifyFile(workerSource, workerEntry.Size, workerEntry.Sha256);
        string appExecutable = ResolveCurrentApplicationPath();

        string transactionRoot = Path.Combine(_installRoot, UpdateProtocol.WorkspaceDirectoryName);
        EnsureNoLinks(transactionRoot);
        Directory.CreateDirectory(transactionRoot);
        string workspace = Path.Combine(transactionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        bool workerStarted = false;
        try
        {
            EnsureNoLinks(workspace);
            string newRoot = Path.Combine(workspace, NewDirectoryName);
            Directory.CreateDirectory(newRoot);
            foreach (UpdateManifestFile entry in validated.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = SafeCombine(staging, entry.Path);
                string destination = SafeCombine(newRoot, entry.Path);
                VerifyFile(source, entry.Size, entry.Sha256);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.SequentialScan))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.WriteThrough))
                {
                    input.CopyTo(output, 128 * 1024);
                    output.Flush(flushToDisk: true);
                }
                VerifyFile(destination, entry.Size, entry.Sha256);
            }
            File.WriteAllBytes(Path.Combine(newRoot, ManifestFileName), manifestBytes);
            string helperPath = Path.Combine(workspace, WorkerExecutableName);
            File.Copy(workerSource, helperPath, overwrite: false);
            VerifyFile(helperPath, workerEntry.Size, workerEntry.Sha256);

            using Process currentProcess = Process.GetCurrentProcess();
            string[] arguments =
            [
                "--apply", workspace, _installRoot, currentProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                currentProcess.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _paths.RootDirectory, appExecutable
            ];
            cancellationToken.ThrowIfCancellationRequested();
            _launcher.Start(helperPath, _installRoot, arguments);
            workerStarted = true;
            _window.RequestExit();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            if (!workerStarted)
            {
                try
                {
                    EnsureNoLinks(workspace);
                    Directory.Delete(workspace, recursive: true);
                }
                catch { }
            }
            if (exception is UpdateOperationException) throw;
            UpdateErrorKind kind = exception switch
            {
                UnauthorizedAccessException => UpdateErrorKind.InstallDirectoryNotWritable,
                OperationCanceledException => UpdateErrorKind.Cancelled,
                _ => UpdateErrorKind.ApplyFailed
            };
            throw new UpdateOperationException(kind, "The verified update could not be handed to the update worker.", exception);
        }
    }

    private string ValidateStagingDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(_paths.UpdateStagingRoot);
        if (!IsWithin(full, root) || Path.GetDirectoryName(full) is null ||
            !Path.GetFullPath(Path.GetDirectoryName(full)!).Equals(root, PathComparison) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _) || !Directory.Exists(full))
            throw new UpdateOperationException(UpdateErrorKind.VerificationFailed, "The verified update staging directory is outside its approved temporary workspace.");
        EnsureNoLinks(full);
        return full;
    }

    private UpdateManifest LoadInstalledManifest(string path)
    {
        VerifyNotLinked(path);
        if (!File.Exists(path)) throw new UpdateOperationException(UpdateErrorKind.InstallDirectoryNotWritable, "The installed update manifest is missing.");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length > UpdateManifest.MaximumMetadataBytes)
            throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "The installed update manifest is oversized.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            string version = document.RootElement.GetProperty("version").GetString()
                ?? throw new InvalidDataException("Installed version is missing.");
            return UpdateManifest.ParseAndValidate(bytes, version, "win-x64", _installRoot, _paths.RootDirectory, IsReparsePoint);
        }
        catch (InvalidDataException exception)
        {
            throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "The installed update manifest failed validation.", exception);
        }
    }

    private string ResolveCurrentApplicationPath()
    {
        string? processPath = _applicationExecutablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)) throw new UpdateOperationException(UpdateErrorKind.InstallDirectoryNotWritable, "The current application path could not be determined.");
        string full = Path.GetFullPath(processPath);
        if (!IsWithin(full, _installRoot) || !File.Exists(full))
            throw new UpdateOperationException(UpdateErrorKind.InstallDirectoryNotWritable, "The current application executable is outside the installation directory.");
        VerifyNotLinked(full);
        return full;
    }

    private string SafeCombine(string root, string relative)
    {
        UpdateManifest.ValidatePath(relative.Replace(Path.DirectorySeparatorChar, '/'));
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(full, Path.GetFullPath(root)))
            throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "An update path escaped the approved workspace.");
        RejectLinks(full, Path.GetFullPath(root));
        return full;
    }

    private static void VerifyFile(string path, long size, string sha256)
    {
        VerifyNotLinked(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        if (stream.Length != size || !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateOperationException(UpdateErrorKind.VerificationFailed, "An update workspace file no longer matches its validated manifest.");
    }

    private static void EnsureNoLinks(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path))!;
        RejectLinks(Path.GetFullPath(path), root);
        if (Directory.Exists(path) && IsReparsePoint(path)) throw new IOException("Update paths cannot use a directory link.");
    }

    private static void RejectLinks(string target, string root)
    {
        string current = root;
        if (IsReparsePoint(current)) throw new IOException("Update paths cannot traverse a linked root.");
        string relative = Path.GetRelativePath(root, target);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current)) throw new IOException("Update paths cannot traverse a link.");
        }
    }

    private static void VerifyNotLinked(string path)
    {
        string full = Path.GetFullPath(path);
        RejectLinks(full, Path.GetPathRoot(full)!);
    }

    private static bool IsReparsePoint(string path)
        => (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool IsWithin(string path, string root)
    {
        StringComparison comparison = PathComparison;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, comparison) || path.StartsWith(prefix, comparison);
    }
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}




