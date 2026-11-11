using System.Text.Json;
using System.Text.Json.Serialization;
using ClipboardManager.Core.Updates;

namespace ClipboardManager.Core.Updates;

/// <summary>Applies a manifest-derived file transaction and retains its backup until startup health is confirmed.</summary>
public sealed class UpdateTransaction
{
    public const string WorkspaceDirectoryName = ".clipboard-update";
    public const string ManifestFileName = "update-manifest.json";
    public const string JournalFileName = "journal.json";
    private const string OldManifestSnapshotName = "old-manifest.json";
    private const string NewManifestSnapshotName = "new-manifest.json";
    private const string BackupDirectoryName = "backup";
    private const string NewDirectoryName = "new";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _installRoot;
    private readonly string _workspace;
    private readonly string _dataRoot;
    private readonly IUpdateFileSystem _fileSystem;
    private readonly string _journalPath;
    private UpdateJournal? _journal;
    private int _state;

    public UpdateTransaction(string installRoot, string workspace, string userDataDirectory, IUpdateFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        _installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        _workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDataDirectory));
        _fileSystem = fileSystem ?? new PhysicalUpdateFileSystem();
        _journalPath = Path.Combine(_workspace, JournalFileName);
        ValidateWorkspaceLocation();
    }

    public Guid TransactionId => _journal?.TransactionId ?? Guid.Empty;
    public UpdateTransactionState State => _journal?.State ?? (UpdateTransactionState)Volatile.Read(ref _state);

    public async Task<UpdateTransactionResult> ApplyAsync(CancellationToken cancellationToken)
    {
        if (_fileSystem.FileExists(_journalPath))
            throw new InvalidOperationException("An update transaction already exists in this workspace.");
        EnsureSafeWorkspaceChain();
        string currentManifestPath = Path.Combine(_installRoot, ManifestFileName);
        byte[] currentManifestBytes = _fileSystem.ReadAllBytes(currentManifestPath);
        UpdateManifest current = ParseManifest(currentManifestBytes, _installRoot);
        string stagedManifestPath = Path.Combine(_workspace, NewDirectoryName, ManifestFileName);
        byte[] nextManifestBytes = _fileSystem.ReadAllBytes(stagedManifestPath);
        UpdateManifest next = ParseManifest(nextManifestBytes, _installRoot);
        if (Version.Parse(next.Version) <= Version.Parse(current.Version))
            throw new InvalidDataException("An update must advance the installed version.");
        string oldManifestSnapshot = Path.Combine(_workspace, OldManifestSnapshotName);
        if (_fileSystem.FileExists(oldManifestSnapshot))
            throw new InvalidOperationException("The update workspace already contains an old manifest snapshot.");
        _fileSystem.WriteAtomic(oldManifestSnapshot, currentManifestBytes);
        _fileSystem.WriteAtomic(Path.Combine(_workspace, NewManifestSnapshotName), nextManifestBytes);

        IReadOnlyList<UpdateOperation> operations = BuildOperations(current, next, currentManifestBytes, nextManifestBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _journal = new UpdateJournal(1, Guid.NewGuid(), UpdateTransactionState.Prepared, operations, DateTimeOffset.UtcNow);
        PersistJournal();
        try
        {
            SetState(UpdateTransactionState.Applying);
            foreach (int index in Enumerable.Range(0, _journal.Operations.Count))
            {
                UpdateOperation operation = _journal.Operations[index];
                cancellationToken.ThrowIfCancellationRequested();
                ApplyOperation(index, operation);
            }
            SetState(UpdateTransactionState.HealthPending);
            return new UpdateTransactionResult(_journal.TransactionId, _journal.State);
        }
        catch (Exception applyFailure)
        {
            try
            {
                await RollbackCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                SetState(UpdateTransactionState.RollbackPending);
                throw new AggregateException("Update failed and rollback remains recoverable; the workspace was preserved.",
                    applyFailure, rollbackFailure);
            }
            CleanupWorkspace();
            throw;
        }
    }

    public async Task<UpdateTransactionResult> RecoverAsync(CancellationToken cancellationToken)
    {
        EnsureSafeWorkspaceChain();
        if (!_fileSystem.FileExists(_journalPath))
            throw new FileNotFoundException("The update recovery journal is missing.", _journalPath);
        _journal = DeserializeJournal(_fileSystem.ReadAllBytes(_journalPath));
        if (_journal.State == UpdateTransactionState.Complete)
        {
            CleanupWorkspace();
            return new UpdateTransactionResult(_journal.TransactionId, UpdateTransactionState.Complete);
        }
        if (_journal.State == UpdateTransactionState.RolledBack)
        {
            CleanupWorkspace();
            return new UpdateTransactionResult(_journal.TransactionId, UpdateTransactionState.RolledBack);
        }
        ValidateJournalAgainstManifests(_journal);
        cancellationToken.ThrowIfCancellationRequested();
        await RollbackCoreAsync(CancellationToken.None).ConfigureAwait(false);
        CleanupWorkspace();
        return new UpdateTransactionResult(_journal.TransactionId, UpdateTransactionState.RolledBack);
    }

    public Task MarkHealthyAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeWorkspaceChain();
        if (_journal is null && _fileSystem.FileExists(_journalPath)) _journal = DeserializeJournal(_fileSystem.ReadAllBytes(_journalPath));
        if (_journal is not null) ValidateJournalAgainstManifests(_journal);
        if (_journal is null || _journal.State != UpdateTransactionState.HealthPending || _journal.TransactionId != transactionId)
            throw new InvalidOperationException("The startup health marker does not match a pending update transaction.");
        SetState(UpdateTransactionState.Complete);
        CleanupWorkspace();
        return Task.CompletedTask;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeWorkspaceChain();
        if (_journal is null && _fileSystem.FileExists(_journalPath)) _journal = DeserializeJournal(_fileSystem.ReadAllBytes(_journalPath));
        if (_journal is null) throw new FileNotFoundException("The update recovery journal is missing.", _journalPath);
        await RollbackCoreAsync(CancellationToken.None).ConfigureAwait(false);
        CleanupWorkspace();
    }

    private IReadOnlyList<UpdateOperation> BuildOperations(UpdateManifest current, UpdateManifest next, byte[] currentManifestBytes, byte[] nextManifestBytes, CancellationToken cancellationToken)
    {
        var currentFiles = current.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (UpdateManifestFile installedFile in current.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyFile(Resolve(_installRoot, installedFile.Path), installedFile.Size, installedFile.Sha256,
                "An installed file is missing or was modified outside the update process.");
        }
        var nextFiles = next.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var operations = new List<UpdateOperation>();
        foreach (UpdateManifestFile file in next.Files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string staged = Resolve(_workspace, $"{NewDirectoryName}/{file.Path}");
            VerifyFile(staged, file.Size, file.Sha256, "A staged update file failed verification.");
            string target = Resolve(_installRoot, file.Path);
            bool exists = _fileSystem.FileExists(target);
            string? oldHash = null;
            long oldSize = 0;
            if (exists)
            {
                if (!currentFiles.TryGetValue(file.Path, out UpdateManifestFile? oldFile))
                    throw new IOException($"Refusing to overwrite a file not owned by the installed manifest: {file.Path}");
                VerifyFile(target, oldFile.Size, oldFile.Sha256, "An installed file was modified outside the update process.");
                oldHash = oldFile.Sha256;
                oldSize = oldFile.Size;
                if (string.Equals(oldHash, file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            }
            else if (currentFiles.TryGetValue(file.Path, out UpdateManifestFile? oldMissing) &&
                     string.Equals(oldMissing.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                operations.Add(new UpdateOperation(file.Path, UpdateOperationKind.Replace, false, null, 0,
                    file.Sha256, file.Size));
                continue;
            }
            operations.Add(new UpdateOperation(file.Path, UpdateOperationKind.Replace, exists, oldHash, oldSize,
                file.Sha256, file.Size));
        }

        foreach (UpdateManifestFile oldFile in current.Files.Where(file => !nextFiles.ContainsKey(file.Path))
                     .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = Resolve(_installRoot, oldFile.Path);
            if (!_fileSystem.FileExists(target)) continue;
            VerifyFile(target, oldFile.Size, oldFile.Sha256, "An obsolete installed file was modified; it will not be deleted.");
            operations.Add(new UpdateOperation(oldFile.Path, UpdateOperationKind.Delete, true, oldFile.Sha256,
                oldFile.Size, null, 0));
        }

        string currentManifest = Path.Combine(_installRoot, ManifestFileName);
        string stagedManifest = Path.Combine(_workspace, NewDirectoryName, ManifestFileName);
        VerifyFile(currentManifest, currentManifestBytes.LongLength, HashBytes(currentManifestBytes), "The installed manifest changed during validation.");
        VerifyFile(stagedManifest, nextManifestBytes.LongLength, HashBytes(nextManifestBytes), "The staged manifest changed during validation.");
        operations.Add(new UpdateOperation(ManifestFileName, UpdateOperationKind.Replace, true, HashBytes(currentManifestBytes),
            _fileSystem.GetLength(currentManifest), HashBytes(nextManifestBytes), nextManifestBytes.LongLength));
        return operations.AsReadOnly();
    }

    private void ApplyOperation(int index, UpdateOperation operation)
    {
        string target = Resolve(_installRoot, operation.Path);
        string backup = Resolve(_workspace, $"{BackupDirectoryName}/{operation.Path}");
        string staged = Resolve(_workspace, $"{NewDirectoryName}/{operation.Path}");
        if (operation.HadOriginal)
        {
            VerifyFile(target, operation.OriginalSize, operation.OriginalSha256!, "An installed file changed before replacement.");
            EnsureSafeDirectories(_workspace, Path.GetDirectoryName(backup)!);
            _fileSystem.CopyFile(target, backup);
            VerifyFile(backup, operation.OriginalSize, operation.OriginalSha256!, "An update backup failed verification.");
            UpdateOperation backedUp = operation with { BackupSaved = true };
            ReplaceJournalOperation(index, backedUp);
        }
        else if (_fileSystem.FileExists(target))
        {
            throw new IOException($"Refusing to replace an unmanaged file: {operation.Path}");
        }

        if (operation.Kind == UpdateOperationKind.Delete)
        {
            if (operation.HadOriginal) _fileSystem.DeleteFile(target);
        }
        else
        {
            VerifyFile(staged, operation.NewSize, operation.NewSha256!, "A staged update file changed before replacement.");
            EnsureSafeDirectories(_installRoot, Path.GetDirectoryName(target)!);
            if (operation.HadOriginal) _fileSystem.ReplaceFile(staged, target);
            else _fileSystem.MoveFile(staged, target);
            VerifyFile(target, operation.NewSize, operation.NewSha256!, "An installed update file failed verification.");
        }
        ReplaceJournalOperation(index, _journal!.Operations[index] with { Applied = true });
    }

    private Task RollbackCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetState(UpdateTransactionState.RollingBack);
        for (int index = _journal!.Operations.Count - 1; index >= 0; index--)
        {
            UpdateOperation operation = _journal.Operations[index];
            string target = Resolve(_installRoot, operation.Path);
            string backup = Resolve(_workspace, $"{BackupDirectoryName}/{operation.Path}");
            bool backupExists = _fileSystem.FileExists(backup);
            if (backupExists)
            {
                VerifyFile(backup, operation.OriginalSize, operation.OriginalSha256!, "An update backup failed verification during rollback.");
                EnsureSafeDirectories(_installRoot, Path.GetDirectoryName(target)!);
                string temporary = target + ".rollback-" + _journal.TransactionId.ToString("N");
                if (_fileSystem.FileExists(temporary)) _fileSystem.DeleteFile(temporary);
                _fileSystem.CopyFile(backup, temporary);
                VerifyFile(temporary, operation.OriginalSize, operation.OriginalSha256!, "A rollback copy failed verification.");
                if (_fileSystem.FileExists(target)) _fileSystem.ReplaceFile(temporary, target);
                else _fileSystem.MoveFile(temporary, target);
                VerifyFile(target, operation.OriginalSize, operation.OriginalSha256!, "An installed file could not be restored.");
            }
            else if (operation.HadOriginal)
            {
                if (!_fileSystem.FileExists(target) || !Matches(target, operation.OriginalSize, operation.OriginalSha256!))
                    throw new IOException($"The original file and its backup are both unavailable: {operation.Path}");
            }
            else if (_fileSystem.FileExists(target))
            {
                if (operation.Kind == UpdateOperationKind.Delete || !Matches(target, operation.NewSize, operation.NewSha256!))
                    throw new IOException($"An unexpected file blocks update rollback: {operation.Path}");
                _fileSystem.DeleteFile(target);
            }
            ReplaceJournalOperation(index, operation with { Applied = false, BackupSaved = false });
        }
        SetState(UpdateTransactionState.RolledBack);
        return Task.CompletedTask;
    }

    private void ValidateJournalAgainstManifests(UpdateJournal journal)
    {
        string oldSnapshotPath = Path.Combine(_workspace, OldManifestSnapshotName);
        string nextManifestPath = Path.Combine(_workspace, NewManifestSnapshotName);
        if (!_fileSystem.FileExists(oldSnapshotPath) || !_fileSystem.FileExists(nextManifestPath))
            throw new InvalidDataException("The update journal is missing its manifest snapshots.");
        UpdateManifest oldManifest = ParseManifest(_fileSystem.ReadAllBytes(oldSnapshotPath), _installRoot);
        UpdateManifest nextManifest = LoadManifest(nextManifestPath, _installRoot);
        if (Version.Parse(nextManifest.Version) <= Version.Parse(oldManifest.Version))
            throw new InvalidDataException("The journal manifests do not describe a forward update.");

        var oldFiles = oldManifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var nextFiles = nextManifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UpdateOperation operation in journal.Operations)
        {
            if (!seen.Add(operation.Path)) throw new InvalidDataException("The update journal contains duplicate operations.");
            if (operation.Path == ManifestFileName)
            {
                string oldHash = HashBytes(_fileSystem.ReadAllBytes(oldSnapshotPath));
                string nextHash = HashFile(nextManifestPath);
                if (operation.Kind != UpdateOperationKind.Replace || !operation.HadOriginal ||
                    operation.OriginalSize != _fileSystem.GetLength(oldSnapshotPath) ||
                    !string.Equals(operation.OriginalSha256, oldHash, StringComparison.OrdinalIgnoreCase) ||
                    operation.NewSize != _fileSystem.GetLength(nextManifestPath) ||
                    !string.Equals(operation.NewSha256, nextHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The journal manifest operation does not match its snapshots.");
                continue;
            }

            oldFiles.TryGetValue(operation.Path, out UpdateManifestFile? oldFile);
            nextFiles.TryGetValue(operation.Path, out UpdateManifestFile? nextFile);
            if (operation.Kind == UpdateOperationKind.Replace)
            {
                if (nextFile is null || operation.NewSize != nextFile.Size ||
                    !string.Equals(operation.NewSha256, nextFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A journal replacement is not declared by the new manifest.");
                if (oldFile is null)
                {
                    if (operation.HadOriginal) throw new InvalidDataException("The journal claims ownership of a new path.");
                }
                else if (operation.HadOriginal && (operation.OriginalSize != oldFile.Size ||
                         !string.Equals(operation.OriginalSha256, oldFile.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("A journal replacement does not match the old manifest.");
                else if (!operation.HadOriginal && string.Equals(oldFile.Sha256, nextFile.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    string installed = Resolve(_installRoot, operation.Path);
                    string staged = Resolve(_workspace, $"{NewDirectoryName}/{operation.Path}");
                    if (_fileSystem.FileExists(installed) && _fileSystem.FileExists(staged))
                        throw new InvalidDataException("The journal claims a missing original for an unchanged installed file.");
                }
            }
            else
            {
                if (oldFile is null || nextFile is not null || !operation.HadOriginal ||
                    operation.OriginalSize != oldFile.Size ||
                    !string.Equals(operation.OriginalSha256, oldFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A journal deletion is not declared by the old and new manifests.");
            }
        }

        if (!seen.Contains(ManifestFileName)) throw new InvalidDataException("The update journal omits its manifest commit operation.");
        foreach (UpdateManifestFile file in nextManifest.Files)
        {
            oldFiles.TryGetValue(file.Path, out UpdateManifestFile? oldFile);
            bool requiresOperation = oldFile is null || !string.Equals(oldFile.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase);
            if (requiresOperation && !seen.Contains(file.Path))
                throw new InvalidDataException("The update journal omits a file declared by the new manifest.");
        }
    }

    private UpdateManifest ParseManifest(byte[] bytes, string root)
    {
        if (bytes.Length is 0 or > UpdateManifest.MaximumMetadataBytes)
            throw new InvalidDataException("An update manifest is empty or oversized.");
        using JsonDocument document = JsonDocument.Parse(bytes);
        string version = document.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidDataException("The update manifest version is missing.");
        return UpdateManifest.ParseAndValidate(bytes, version, "win-x64", root, _dataRoot, _fileSystem.IsReparsePoint);
    }
    private UpdateManifest LoadManifest(string path, string installRoot)
    {
        if (!_fileSystem.FileExists(path) || _fileSystem.IsReparsePoint(path))
            throw new InvalidDataException("The installed or staged update manifest is missing or linked.");
        byte[] bytes = _fileSystem.ReadAllBytes(path);
        if (bytes.Length > UpdateManifest.MaximumMetadataBytes)
            throw new InvalidDataException("The update manifest exceeds the metadata limit.");
        using JsonDocument document = JsonDocument.Parse(bytes);
        string version = document.RootElement.GetProperty("version").GetString() ?? throw new InvalidDataException("The manifest version is missing.");
        return UpdateManifest.ParseAndValidate(bytes, version, "win-x64", installRoot, _dataRoot, _fileSystem.IsReparsePoint);
    }

    private void VerifyFile(string path, long size, string sha256, string message)
    {
        if (!_fileSystem.FileExists(path) || _fileSystem.IsReparsePoint(path) || _fileSystem.GetLength(path) != size ||
            !string.Equals(HashFile(path), sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(message);
    }

    private bool Matches(string path, long size, string sha256)
        => _fileSystem.FileExists(path) && !_fileSystem.IsReparsePoint(path) && _fileSystem.GetLength(path) == size &&
           string.Equals(HashFile(path), sha256, StringComparison.OrdinalIgnoreCase);

    private string HashFile(string path) => _fileSystem.ComputeSha256(path);
    private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private void ReplaceJournalOperation(int index, UpdateOperation operation)
    {
        var operations = _journal!.Operations.ToArray();
        operations[index] = operation;
        _journal = _journal with { Operations = operations, UpdatedAtUtc = DateTimeOffset.UtcNow };
        PersistJournal();
    }

    private void SetState(UpdateTransactionState state)
    {
        _journal = _journal! with { State = state, UpdatedAtUtc = DateTimeOffset.UtcNow };
        Volatile.Write(ref _state, (int)state);
        PersistJournal();
    }

    private void PersistJournal()
    {
        EnsureSafeWorkspaceChain();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_journal, JsonOptions);
        _fileSystem.WriteAtomic(_journalPath, bytes);
    }

    private UpdateJournal DeserializeJournal(byte[] bytes)
    {
        UpdateJournal journal = JsonSerializer.Deserialize<UpdateJournal>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The update recovery journal is invalid.");
        if (journal.Schema != 1 || journal.TransactionId == Guid.Empty || journal.Operations.Count == 0 ||
            journal.Operations.Count > 10_001 || journal.Operations[^1].Path != ManifestFileName)
            throw new InvalidDataException("The update recovery journal failed validation.");
        foreach (UpdateOperation operation in journal.Operations)
        {
            UpdateManifest.ValidatePath(operation.Path);
            Resolve(_installRoot, operation.Path);
            if (operation.HadOriginal && (operation.OriginalSha256 is null || operation.OriginalSize < 0) ||
                operation.Kind == UpdateOperationKind.Replace && (operation.NewSha256 is null || operation.NewSize < 0))
                throw new InvalidDataException("The update recovery journal contains an invalid operation.");
        }
        return journal;
    }

    private string Resolve(string root, string relative)
    {
        UpdateManifest.ValidatePath(relative);
        string destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, comparison) || IsWithin(destination, _dataRoot))
            throw new InvalidDataException("An update transaction path escaped its approved directory or overlaps user data.");
        RejectLinks(destination, root);
        return destination;
    }

    private void EnsureSafeWorkspaceChain()
    {
        RejectLinks(_workspace, _installRoot);
        if (_fileSystem.IsReparsePoint(_workspace)) throw new IOException("The update transaction workspace is a linked directory.");
    }

    private void EnsureSafeDirectories(string root, string directory)
    {
        string relative = Path.GetRelativePath(root, directory);
        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!_fileSystem.DirectoryExists(current)) _fileSystem.CreateDirectory(current);
            if (_fileSystem.IsReparsePoint(current)) throw new IOException("An update path traverses a linked directory.");
        }
    }

    private void RejectLinks(string destination, string root)
    {
        string current = root;
        if (_fileSystem.IsReparsePoint(current)) throw new IOException("Update paths cannot traverse a linked root directory.");
        string relative = Path.GetRelativePath(root, destination);
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (_fileSystem.IsReparsePoint(current)) throw new IOException("Update paths cannot traverse a linked directory or file.");
        }
    }

    private void ValidateWorkspaceLocation()
    {
        string parent = Path.Combine(_installRoot, WorkspaceDirectoryName);
        string name = Path.GetFileName(_workspace);
        if (!IsWithin(_workspace, parent) || name.Length != 32 || !Guid.TryParseExact(name, "N", out _))
            throw new ArgumentException("The transaction workspace must be a GUID folder under the installation update directory.", nameof(_workspace));
        if (IsWithin(_workspace, _dataRoot) || IsWithin(_dataRoot, _workspace))
            throw new ArgumentException("The transaction workspace cannot overlap user data.", nameof(_workspace));
        if (!Path.GetPathRoot(_installRoot)!.Equals(Path.GetPathRoot(_workspace), PathComparison))
            throw new ArgumentException("The update backup workspace must be on the installation volume.", nameof(_workspace));
    }

    private void CleanupWorkspace()
    {
        if (!_fileSystem.DirectoryExists(_workspace)) return;
        EnsureSafeWorkspaceChain();
        EnsureNoLinksRecursively(_workspace);
        _fileSystem.DeleteDirectory(_workspace, recursive: true);
    }

    private void EnsureNoLinksRecursively(string directory)
    {
        if (_fileSystem.IsReparsePoint(directory)) throw new IOException("Refusing to clean a linked update workspace.");
        foreach (string entry in _fileSystem.EnumerateEntries(directory))
        {
            if (_fileSystem.IsReparsePoint(entry)) throw new IOException("Refusing to clean a workspace containing links.");
            if (_fileSystem.DirectoryExists(entry)) EnsureNoLinksRecursively(entry);
        }
    }

    private static bool IsWithin(string path, string root)
    {
        StringComparison comparison = PathComparison;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, comparison) || path.StartsWith(prefix, comparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}












