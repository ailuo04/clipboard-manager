using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ClipboardManager.App.Utilities;
using ClipboardManager.Core.Updates;

namespace ClipboardManager.App.Services;

/// <summary>Recovers interrupted program-file transactions before storage starts and acknowledges healthy handoffs after startup.</summary>
public sealed class UpdateRecoveryService : IUpdateRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly IUpdateFileSystem _fileSystem;
    private readonly string _installRoot;
    private string[] _arguments;
    private UpdateHealthMarker? _pendingHealth;
    private string? _pendingWorkspace;

    public UpdateRecoveryService(AppPaths paths, AppLogger logger, IUpdateFileSystem? fileSystem = null,
        string? installRoot = null, IReadOnlyList<string>? arguments = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileSystem = fileSystem ?? new PhysicalUpdateFileSystem();
        _installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot ?? AppContext.BaseDirectory));
        _arguments = arguments?.ToArray() ?? Environment.GetCommandLineArgs().Skip(1).ToArray();
    }

    public void SetLaunchArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return;
        string[] activationArguments = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (activationArguments.Length > 0 && activationArguments[0] == UpdateProtocol.HealthArgument)
            _arguments = activationArguments;
    }

    public async Task RecoverIfNeededAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ParsePendingHealthRequest();
        string transactionRoot = Path.Combine(_installRoot, UpdateProtocol.WorkspaceDirectoryName);
        if (!Directory.Exists(transactionRoot)) return;
        EnsureNoLinks(transactionRoot);
        foreach (string workspace in Directory.EnumerateDirectories(transactionRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(workspace).Length != 32 || !Guid.TryParseExact(Path.GetFileName(workspace), "N", out _))
                throw new InvalidDataException("An unknown update workspace was found; it was preserved.");
            if (IsPendingHealthWorkspace(workspace)) continue;
            if (IsLegacyWpfJournal(workspace))
            {
                EnsureNoLinksRecursively(workspace);
                _logger.Log(AppLogLevel.Warning, AppLogOperation.UpdateRecovery, AppLogErrorCategory.Security);
                continue;
            }
            var transaction = new UpdateTransaction(_installRoot, workspace, _paths.RootDirectory, _fileSystem);
            try
            {
                await transaction.RecoverAsync(cancellationToken).ConfigureAwait(false);
                _logger.Log(AppLogLevel.Information, AppLogOperation.UpdateRecovery);
            }
            catch (FileNotFoundException)
            {
                // Before the transaction journal is committed, no installation file has changed.
                EnsureNoLinksRecursively(workspace);
                string diagnosticPath = Path.Combine(workspace, UpdateProtocol.WorkerErrorFileName);
                if (IsRecognizedWorkerDiagnostic(diagnosticPath))
                {
                    _logger.Log(AppLogLevel.Warning, AppLogOperation.UpdateRecovery, AppLogErrorCategory.Security);
                    continue;
                }
                Directory.Delete(workspace, recursive: true);
            }
            catch (Exception exception)
            {
                try { _logger.LogException(AppLogOperation.UpdateRecovery, AppLogErrorCategory.Security, exception); } catch { }
                throw;
            }
        }
    }

    public Task MarkStartupHealthyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pendingHealth is null || _pendingWorkspace is null) return Task.CompletedTask;
        string path = Path.Combine(_pendingWorkspace, UpdateProtocol.HealthReadyFileName);
        EnsureNoLinks(_pendingWorkspace);
        _fileSystem.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(_pendingHealth, JsonOptions));
        _pendingHealth = null;
        _pendingWorkspace = null;
        return Task.CompletedTask;
    }

    private void ParsePendingHealthRequest()
    {
        if (_arguments.Length == 0) return;
        int index = Array.IndexOf(_arguments, UpdateProtocol.HealthArgument);
        if (index < 0) return;
        if (index != 0 || _arguments.Length != 3 || !Guid.TryParseExact(_arguments[1], "N", out Guid id) ||
            _arguments[2].Length != 64 || !_arguments[2].All(Uri.IsHexDigit))
            throw new InvalidDataException("The update startup health arguments are invalid.");
        string workspace = Path.Combine(_installRoot, UpdateProtocol.WorkspaceDirectoryName, id.ToString("N"));
        EnsureNoLinks(workspace);
        string requestPath = Path.Combine(workspace, UpdateProtocol.HealthRequestFileName);
        if (!_fileSystem.FileExists(requestPath) || _fileSystem.IsReparsePoint(requestPath))
            throw new InvalidDataException("The matching update health request is missing.");
        UpdateHealthMarker? request = JsonSerializer.Deserialize<UpdateHealthMarker>(_fileSystem.ReadAllBytes(requestPath), JsonOptions);
        if (request is null || request.TransactionId != id ||
            !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(request.Token), System.Text.Encoding.ASCII.GetBytes(_arguments[2])))
            throw new InvalidDataException("The update startup health request did not match its launch token.");
        string journalPath = Path.Combine(workspace, UpdateTransaction.JournalFileName);
        if (!_fileSystem.FileExists(journalPath)) throw new InvalidDataException("The update transaction journal is missing.");
        _pendingHealth = request;
        _pendingWorkspace = workspace;
    }

    private bool IsLegacyWpfJournal(string workspace)
    {
        try
        {
            string path = Path.Combine(workspace, UpdateTransaction.JournalFileName);
            if (!_fileSystem.FileExists(path) || _fileSystem.IsReparsePoint(path) || _fileSystem.GetLength(path) is <= 0 or > UpdateManifest.MaximumMetadataBytes)
                return false;
            using JsonDocument document = JsonDocument.Parse(_fileSystem.ReadAllBytes(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is 0 or > 10_001) return false;
            JsonElement last = default;
            foreach (JsonElement operation in root.EnumerateArray())
            {
                if (operation.ValueKind != JsonValueKind.Object ||
                    !operation.TryGetProperty("path", out JsonElement pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                    !operation.TryGetProperty("hadOriginal", out JsonElement originalValue) || originalValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !operation.TryGetProperty("deleteOnly", out JsonElement deleteValue) || deleteValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !operation.TryGetProperty("sha256", out JsonElement hashValue) || hashValue.ValueKind != JsonValueKind.String ||
                    hashValue.GetString() is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)) return false;
                UpdateManifest.ValidatePath(pathValue.GetString()!);
                last = operation;
            }
            return last.TryGetProperty("path", out JsonElement lastPath) &&
                   string.Equals(lastPath.GetString(), UpdateTransaction.ManifestFileName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private bool IsRecognizedWorkerDiagnostic(string path)
    {
        try
        {
            if (!_fileSystem.FileExists(path) || _fileSystem.IsReparsePoint(path) || _fileSystem.GetLength(path) is <= 0 or > 4096)
                return false;
            UpdateWorkerDiagnostic? diagnostic = JsonSerializer.Deserialize<UpdateWorkerDiagnostic>(_fileSystem.ReadAllBytes(path), JsonOptions);
            return diagnostic is not null && diagnostic.TimestampUtc != default && diagnostic.CorrelationId != Guid.Empty &&
                   diagnostic.ExceptionType is { Length: > 0 and <= 256 } &&
                   diagnostic.Phase is "wait-for-application-exit" or "apply" or "launch-updated-application" or
                       "startup-health-check" or "startup-health-timeout" or "rollback" or "restart-restored-application";
        }
        catch { return false; }
    }

    private void EnsureNoLinksRecursively(string directory)
    {
        EnsureNoLinks(directory);
        foreach (string entry in _fileSystem.EnumerateEntries(directory))
        {
            EnsureNoLinks(entry);
            if (_fileSystem.DirectoryExists(entry)) EnsureNoLinksRecursively(entry);
        }
    }
    private bool IsPendingHealthWorkspace(string workspace)
        => _pendingHealth is not null && string.Equals(workspace, _pendingWorkspace,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void EnsureNoLinks(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        Check(current);
        foreach (string component in Path.GetRelativePath(current, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            Check(current);
        }
    }

    private void Check(string path)
    {
        if (_fileSystem.IsReparsePoint(path)) throw new IOException("Update recovery cannot traverse a linked path.");
    }
}



