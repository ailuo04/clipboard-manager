using System.IO.Compression;
using System.Security.Cryptography;
using ClipboardManager.Core.Updates;
using ClipboardManager.App.Utilities;

namespace ClipboardManager.App.Services;

/// <summary>Downloads unique content-addressed blobs and verifies expanded files in an isolated temporary workspace.</summary>
public sealed class UpdatePackageService : IUpdatePackageService
{
    private readonly IUpdateReleaseService _releases;
    private readonly string _stagingRoot;
    private readonly Func<string, bool> _isReparsePoint;

    public UpdatePackageService(IUpdateReleaseService releases, AppPaths paths, Func<string, bool>? isReparsePoint = null)
    {
        _releases = releases ?? throw new ArgumentNullException(nameof(releases));
        _isReparsePoint = isReparsePoint ?? HasReparsePoint;
        ArgumentNullException.ThrowIfNull(paths);
        _stagingRoot = Path.GetFullPath(paths.UpdateStagingRoot);
        string dataRoot = Path.GetFullPath(paths.RootDirectory);
        if (IsWithin(_stagingRoot, dataRoot) || IsWithin(dataRoot, _stagingRoot))
            throw new ArgumentException("The update workspace must be outside the application data directory.", nameof(paths));
    }

    public string CreateStagingDirectory(string stagingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        string root = Path.GetFullPath(stagingRoot);
        if (!IsRoot(root))
            throw new UpdateOperationException(UpdateErrorKind.DownloadFailed, "The update workspace root is not approved.");
        RejectReparsePointChain(root, Path.GetPathRoot(root)!);
        Directory.CreateDirectory(root);
        if (IsReparsePoint(root)) throw new UpdateOperationException(UpdateErrorKind.DownloadFailed, "The update workspace is a linked directory.");
        string workspace = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    public async Task DownloadAsync(UpdateCheckResult update, string stagingDirectory, IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!update.IsUpdateAvailable || update.Manifest is null)
            throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "There is no validated update package to download.");
        string workspace = ValidateWorkspace(stagingDirectory);
        long required = checked(update.DownloadSize + update.Manifest.Files.Sum(file => file.Size));
        try
        {
            if (new DriveInfo(Path.GetPathRoot(workspace)!).AvailableFreeSpace < required)
                throw new UpdateOperationException(UpdateErrorKind.InsufficientDiskSpace, "There is not enough free disk space for the update.");
        }
        catch (UpdateOperationException) { throw; }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            throw new UpdateOperationException(UpdateErrorKind.DownloadFailed, "The update workspace cannot be inspected.", exception);
        }

        string blobDirectory = Path.Combine(workspace, ".blobs");
        EnsureSafeDirectories(workspace, blobDirectory);
        long transferred = 0;
        foreach (UpdateManifestFile file in update.Manifest.Files.DistinctBy(file => file.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!update.Assets.TryGetValue(file.Sha256, out UpdateAsset? asset) || asset.Size != file.CompressedSize)
                throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "An update attachment is missing or has an unexpected size.");
            string destination = Path.Combine(blobDirectory, $"update-{file.Sha256}.gz");
            var assetProgress = new InlineProgress<UpdateProgress>(value => progress?.Report(new UpdateProgress(transferred + value.BytesTransferred, update.DownloadSize, value.CurrentFile)));
            await _releases.DownloadAssetAsync(asset, destination, file.CompressedSize, assetProgress, cancellationToken).ConfigureAwait(false);
            transferred = checked(transferred + file.CompressedSize);
        }
    }

    public async Task VerifyAndExtractAsync(UpdateCheckResult update, string stagingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Manifest is null) throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "The update manifest is missing.");
        string workspace = ValidateWorkspace(stagingDirectory);
        string blobDirectory = Path.Combine(workspace, ".blobs");
        EnsureSafeDirectories(workspace, blobDirectory);
        long expandedTotal = 0;
        long expectedTotal = update.Manifest.Files.Sum(item => item.Size);
        foreach (UpdateManifestFile file in update.Manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { UpdateManifest.ValidatePath(file.Path); }
            catch (InvalidDataException exception) { throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "An update path failed validation.", exception); }
            string blob = Path.Combine(blobDirectory, $"update-{file.Sha256}.gz");
            if (!File.Exists(blob) || IsReparsePoint(blob) || new FileInfo(blob).Length != file.CompressedSize)
                throw new UpdateOperationException(UpdateErrorKind.VerificationFailed, "A downloaded update attachment is missing or truncated.");
            string destination = Path.GetFullPath(Path.Combine(workspace, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(destination, workspace) || destination.StartsWith(blobDirectory + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "An update path escaped its isolated workspace.");
            string directory = Path.GetDirectoryName(destination)!;
            EnsureSafeDirectories(workspace, directory);
            string partial = destination + ".partial";
            bool createdPartial = false;
            try
            {
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long fileBytes = 0;
                await using (var inputFile = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var gzip = new GZipStream(inputFile, CompressionMode.Decompress, leaveOpen: false))
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    createdPartial = true;
                    byte[] buffer = new byte[64 * 1024];
                    while (true)
                    {
                        int read = await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        fileBytes = checked(fileBytes + read);
                        expandedTotal = checked(expandedTotal + read);
                        if (fileBytes > file.Size || fileBytes > UpdateManifest.MaximumFileBytes ||
                            expandedTotal > UpdateManifest.MaximumTotalBytes || expandedTotal > expectedTotal)
                            throw new UpdateOperationException(UpdateErrorKind.VerificationFailed, "Expanded update data exceeds the validated manifest limits.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (fileBytes != file.Size || !actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateOperationException(UpdateErrorKind.VerificationFailed, "An expanded update file does not match the manifest hash or size.");
                File.Move(partial, destination);
            }
            catch (OperationCanceledException) { if (createdPartial) TryDelete(partial); throw; }
            catch (Exception exception)
            {
                if (createdPartial) TryDelete(partial);
                if (exception is UpdateOperationException) throw;
                throw new UpdateOperationException(UpdateErrorKind.VerificationFailed, "An update attachment could not be verified.", exception);
            }
        }
        EnsureNoReparsePoints(blobDirectory);
        Directory.Delete(blobDirectory, recursive: true);
    }

    public void Cleanup(string stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory)) return;
        string fullPath = Path.GetFullPath(stagingDirectory);
        string? parent = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileName(fullPath);
        if (parent is null || name.Length != 32 || !Guid.TryParseExact(name, "N", out _) || !IsRoot(Path.GetFullPath(parent))) return;
        if (!Directory.Exists(fullPath)) return;
        RejectReparsePointChain(fullPath, Path.GetPathRoot(fullPath)!);
        EnsureNoReparsePoints(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }

    private string ValidateWorkspace(string path)
    {
        string full = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(full);
        string name = Path.GetFileName(full);
        if (parent is null || !IsRoot(Path.GetFullPath(parent)) || name.Length != 32 || !Guid.TryParseExact(name, "N", out _) ||
            !Directory.Exists(full) || IsReparsePoint(full))
            throw new UpdateOperationException(UpdateErrorKind.DownloadFailed, "The update workspace is unavailable, linked, or outside its approved root.");
        RejectReparsePointChain(full, Path.GetPathRoot(full)!);
        return full;
    }

    private bool IsRoot(string path)
        => path.Equals(_stagingRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void RejectReparsePointChain(string target, string root)
    {
        string current = root;
        string relative = Path.GetRelativePath(root, target);
        if (IsReparsePoint(current)) throw new UpdateOperationException(UpdateErrorKind.DownloadFailed, "The update workspace has a linked parent directory.");
        foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (IsReparsePoint(current)) throw new UpdateOperationException(UpdateErrorKind.DownloadFailed, "The update workspace has a linked parent directory.");
        }
    }

    private void EnsureSafeDirectories(string root, string targetDirectory)
    {
        string current = root;
        string relative = Path.GetRelativePath(root, targetDirectory);
        foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            if (IsReparsePoint(current))
                throw new UpdateOperationException(UpdateErrorKind.InvalidManifest, "An update staging path contains a directory link.");
        }
    }

    private void EnsureNoReparsePoints(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out string? current))
        {
            if (IsReparsePoint(current))
                throw new IOException("Refusing to recursively remove a workspace containing a directory link.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePoint(entry))
                    throw new IOException("Refusing to recursively remove a workspace containing a directory link.");
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
    }

    private static bool IsWithin(string path, string root)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, comparison) || path.StartsWith(prefix, comparison);
    }

    private bool IsReparsePoint(string path) => _isReparsePoint(path);

    private static bool HasReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}


















