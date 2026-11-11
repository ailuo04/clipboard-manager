using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipboardManager.Core.Updates;

public sealed record UpdateManifest(
    int Schema,
    string Application,
    string Version,
    string Platform,
    string PackageKind,
    IReadOnlyList<UpdateManifestFile> Files)
{
    public const int MaximumMetadataBytes = 4 * 1024 * 1024;
    public const string SupportedPackageKind = "winui3";
    public const long MaximumFileBytes = 512L * 1024 * 1024;
    public const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumTotalCompressedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly Regex VersionPattern = new(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "images", "history.db", "history.json", "theme.json", "app.log", "settings.json",
        ".clipboard-update", ".blobs", ".migration-backups", ".migration-journal.json", ".migration-complete.json"
    };

    public static UpdateManifest ParseAndValidate(ReadOnlySpan<byte> json, string expectedVersion, string expectedPlatform,
        string? installDirectory = null, string? userDataDirectory = null, Func<string, bool>? isReparsePoint = null)
    {
        if (json.Length is 0 or > MaximumMetadataBytes)
            throw new InvalidDataException("Update manifest metadata is empty or exceeds the 4 MiB limit.");
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPlatform);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Update manifest must be a JSON object.");
            EnsureNoDuplicateProperties(root);

            int schema = RequiredInt32(root, "schema");
            string application = RequiredString(root, "application");
            string version = RequiredString(root, "version");
            string platform = RequiredString(root, "platform");
            string packageKind = RequiredString(root, "packageKind");
            if (schema != 1 || !string.Equals(application, "ClipboardManager", StringComparison.Ordinal) ||
                !IsVersion(version) || !string.Equals(version, expectedVersion, StringComparison.Ordinal) ||
                !string.Equals(platform, expectedPlatform, StringComparison.Ordinal) ||
                !string.Equals(packageKind, SupportedPackageKind, StringComparison.Ordinal))
                throw new InvalidDataException("Update manifest identity does not match the requested release.");

            if (!root.TryGetProperty("files", out JsonElement fileArray) || fileArray.ValueKind != JsonValueKind.Array ||
                fileArray.GetArrayLength() is 0 or > 10_000)
                throw new InvalidDataException("Update manifest has an invalid file list.");

            var files = new List<UpdateManifestFile>(fileArray.GetArrayLength());
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var digests = new Dictionary<string, (long Size, long CompressedSize)>(StringComparer.OrdinalIgnoreCase);
            long totalSize = 0;
            foreach (JsonElement item in fileArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Update manifest contains an invalid file entry.");
                EnsureNoDuplicateProperties(item);
                string path = RequiredString(item, "path");
                string sha256 = RequiredString(item, "sha256");
                long size = RequiredInt64(item, "size");
                long compressedSize = RequiredInt64(item, "compressedSize");
                ValidatePath(path);
                if (!Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant) ||
                    size < 0 || size > MaximumFileBytes || compressedSize is <= 0 or > MaximumFileBytes ||
                    !paths.Add(path))
                    throw new InvalidDataException("Update manifest contains an invalid or duplicate file entry.");
                totalSize = checked(totalSize + size);
                if (totalSize > MaximumTotalBytes)
                    throw new InvalidDataException("Update package exceeds the 2 GiB expanded size limit.");
                string digest = sha256.ToLowerInvariant();
                if (digests.TryGetValue(digest, out var existing) && existing != (size, compressedSize))
                    throw new InvalidDataException("Files with the same hash have inconsistent size metadata.");
                digests[digest] = (size, compressedSize);
                files.Add(new UpdateManifestFile(path, digest, size, compressedSize));
            }

            long totalCompressed = 0;
            foreach (var blob in digests.Values) totalCompressed = checked(totalCompressed + blob.CompressedSize);
            if (totalCompressed > MaximumTotalCompressedBytes)
                throw new InvalidDataException("Update package exceeds the 2 GiB compressed download limit.");

            if (installDirectory is not null && userDataDirectory is not null)
                ValidateSeparation(files, installDirectory, userDataDirectory, isReparsePoint ?? HasReparsePoint);

            return new UpdateManifest(schema, application, version, platform, packageKind, files.AsReadOnly());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Update manifest JSON is invalid.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Update manifest sizes exceed supported limits.", exception);
        }
    }

    public static bool IsVersion(string value) => VersionPattern.IsMatch(value) && System.Version.TryParse(value, out _);

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith("/", StringComparison.Ordinal) ||
            path.Contains(':') || path.Any(char.IsControl) || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            throw new InvalidDataException("Update manifest contains a non-canonical file path.");

        string[] parts = path.Split('/');
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(".", StringComparison.Ordinal) ||
                part.EndsWith(" ", StringComparison.Ordinal))
                throw new InvalidDataException("Update manifest contains a traversal or non-canonical path segment.");
            string deviceName = part.Split('.')[0].TrimEnd(' ', '.');
            if (ProtectedNames.Contains(part) || IsDeviceName(deviceName))
                throw new InvalidDataException("Update manifest targets a reserved or user-data path.");
        }
    }

    private static bool IsDeviceName(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            return name[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3';
        return false;
    }

    private static void ValidateSeparation(IReadOnlyList<UpdateManifestFile> files, string installDirectory, string userDataDirectory, Func<string, bool> isReparsePoint)
    {
        string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        string data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDataDirectory));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        RejectReparsePointChain(install, Path.GetPathRoot(install)!, isReparsePoint);
        RejectReparsePointChain(data, Path.GetPathRoot(data)!, isReparsePoint);
        foreach (UpdateManifestFile file in files)
        {
            string destination = Path.GetFullPath(Path.Combine(install, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(destination, install, comparison) || IsWithin(destination, data, comparison) || IsWithin(data, destination, comparison))
                throw new InvalidDataException("Update manifest overlaps the user-data directory.");
            RejectReparsePointChain(destination, install, isReparsePoint);
        }
    }

    private static void RejectReparsePointChain(string target, string root, Func<string, bool> isReparsePoint)
    {
        string current = root;
        if (isReparsePoint(current)) throw new InvalidDataException("Update paths cannot use reparse points.");
        string relative = Path.GetRelativePath(root, target);
        foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (isReparsePoint(current)) throw new InvalidDataException("Update paths cannot use reparse points.");
        }
    }

    private static bool HasReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsWithin(string candidate, string root, StringComparison comparison)
    {
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.Equals(root, comparison) || candidate.StartsWith(prefix, comparison);
    }

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Update manifest contains duplicate JSON properties.");
    }

    private static string RequiredString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidDataException($"Update manifest is missing {name}.");

    private static int RequiredInt32(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : throw new InvalidDataException($"Update manifest is missing {name}.");

    private static long RequiredInt64(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : throw new InvalidDataException($"Update manifest is missing {name}.");
}

public sealed record UpdateManifestFile(string Path, string Sha256, long Size, long CompressedSize);















