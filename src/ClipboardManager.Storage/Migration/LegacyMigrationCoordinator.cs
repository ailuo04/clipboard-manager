using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers.Binary;
using System.IO.Compression;
using ClipboardManager.Core.History;

namespace ClipboardManager.Storage.Migration;

public enum MigrationStage { BackupCompleted, SchemaCreated, BeforeImport, AfterImport, AfterValidation, BeforeCompletionMarker }

public sealed class LegacyMigrationCoordinator
{
    private static readonly uint[] PngCrcTable = BuildPngCrcTable();
    private readonly string _historyPath;
    private readonly string _themePath;
    private readonly string _imagesPath;
    private readonly string _backupRoot;
    private readonly string _completionPath;
    private readonly IHistoryRepository _repository;
    private readonly Action<MigrationStage>? _faultPoint;

    public LegacyMigrationCoordinator(string historyPath, string themePath, string imagesPath, string backupRoot,
        string completionPath, IHistoryRepository repository, Action<MigrationStage>? faultPoint = null)
    {
        _historyPath = Path.GetFullPath(historyPath);
        _themePath = Path.GetFullPath(themePath);
        _imagesPath = Path.GetFullPath(imagesPath);
        _backupRoot = Path.GetFullPath(backupRoot);
        _completionPath = Path.GetFullPath(completionPath);
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _faultPoint = faultPoint;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_completionPath))
        {
            await ValidateCompletionAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!File.Exists(_historyPath))
        {
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] historyBytes = await File.ReadAllBytesAsync(_historyPath, cancellationToken).ConfigureAwait(false);
        LegacyHistoryDocument document;
        try
        {
            document = JsonSerializer.Deserialize<LegacyHistoryDocument>(historyBytes, LegacyHistoryDocument.JsonOptions)
                ?? throw new InvalidDataException("Legacy history document is empty.");
        }
        catch (JsonException exception) { throw new InvalidDataException("Legacy history JSON is invalid; source files were left unchanged.", exception); }

        IReadOnlyList<ClipboardRecord> records = document.ToRecords();
        byte[]? themeBytes = File.Exists(_themePath)
            ? await File.ReadAllBytesAsync(_themePath, cancellationToken).ConfigureAwait(false)
            : null;
        string theme = ParseTheme(themeBytes);
        string[] images = records.Where(record => record.Kind == ClipboardContentKind.Image)
            .Select(record => ValidateImageName(record.ImageFileName)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var activeImageNames = images.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> pending = ValidateNames(document.PendingImageDeletes ?? [])
            .Where(name => !activeImageNames.Contains(name)).ToArray();
        var sources = new List<MigrationSource>
        {
            new("history.json", _historyPath, historyBytes, Convert.ToHexString(SHA256.HashData(historyBytes)))
        };
        if (themeBytes is not null)
            sources.Add(new MigrationSource("theme.json", _themePath, themeBytes, Convert.ToHexString(SHA256.HashData(themeBytes))));
        foreach (string name in images)
        {
            string path = Path.Combine(_imagesPath, name);
            if (!File.Exists(path)) throw new InvalidDataException($"Referenced legacy image is missing: {name}.");
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            ValidatePng(bytes, name);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            sources.Add(new MigrationSource(Path.Combine("images", name), path, null, hash));
            Array.Clear(bytes);
        }

        string migrationId = ComputeMigrationId(sources.Select(item => new KeyValuePair<string, string>(item.Relative.Replace('\\', '/'), item.Sha256)));
        await BackupAsync(migrationId, sources, cancellationToken).ConfigureAwait(false);
        await WriteJournalAsync(migrationId, "backup-complete", cancellationToken).ConfigureAwait(false);
        _faultPoint?.Invoke(MigrationStage.BackupCompleted);
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _faultPoint?.Invoke(MigrationStage.SchemaCreated);
        await WriteJournalAsync(migrationId, "schema-created", cancellationToken).ConfigureAwait(false);

        if (!await _repository.HasImportedMigrationAsync(migrationId, cancellationToken).ConfigureAwait(false))
        {
            await WriteJournalAsync(migrationId, "import-started", cancellationToken).ConfigureAwait(false);
            _faultPoint?.Invoke(MigrationStage.BeforeImport);
            await _repository.ImportLegacyAsync(migrationId, records, document.ToSettings(theme), pending, cancellationToken).ConfigureAwait(false);
            await WriteJournalAsync(migrationId, "database-committed", cancellationToken).ConfigureAwait(false);
            _faultPoint?.Invoke(MigrationStage.AfterImport);
        }
        await VerifyImportedStateAsync(records, document.ToSettings(theme), pending, cancellationToken).ConfigureAwait(false);
        _faultPoint?.Invoke(MigrationStage.AfterValidation);
        await WriteJournalAsync(migrationId, "validated", cancellationToken).ConfigureAwait(false);
        _faultPoint?.Invoke(MigrationStage.BeforeCompletionMarker);
        await WriteCompletionAsync(migrationId, records, cancellationToken).ConfigureAwait(false);
        await WriteJournalAsync(migrationId, "complete", cancellationToken).ConfigureAwait(false);
    }

    public static string ComputeMigrationId(IEnumerable<KeyValuePair<string, string>> pathHashes)
    {
        string canonical = string.Join("\n", pathHashes.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key.Replace('\\', '/')}:{item.Value.ToUpperInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string ParseTheme(byte[]? bytes)
    {
        if (bytes is null) return "Light";
        string json = Encoding.UTF8.GetString(bytes);
        ThemeDocument theme;
        try
        {
            theme = JsonSerializer.Deserialize<ThemeDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            }) ?? throw new InvalidDataException("Legacy theme document is empty.");
        }
        catch (JsonException) { return "Light"; }
        return Enum.IsDefined(theme.Theme) ? theme.Theme.ToString() : "Light";
    }

    private static IReadOnlyList<string> ValidateNames(IEnumerable<string> names)
        => names.Select(ValidateImageName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string ValidateImageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length != 36 || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(name[..32], "N", out _))
            throw new InvalidDataException("Legacy image reference is not a valid {guid}.png file name.");
        return name;
    }

    private static void ValidatePng(byte[] bytes, string name)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        try
        {
            if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(signature)) throw new InvalidDataException();
            int offset = 8;
            int width = 0, height = 0, bitsPerPixel = 0;
            byte interlace = 0, imageColorType = 0, imageBitDepth = 0;
            int paletteEntries = 0;
            bool sawHeader = false, sawPalette = false, sawPaletteDependentChunk = false, sawData = false, dataEnded = false, sawEnd = false;
            var seenUniqueChunks = new HashSet<string>(StringComparer.Ordinal);
            using var compressed = new MemoryStream();
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 12) throw new InvalidDataException();
                uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                if (chunkLength > int.MaxValue || chunkLength > bytes.Length - offset - 12) throw new InvalidDataException();
                int length = (int)chunkLength;
                ReadOnlySpan<byte> type = bytes.AsSpan(offset + 4, 4);
                ReadOnlySpan<byte> data = bytes.AsSpan(offset + 8, length);
                for (int typeIndex = 0; typeIndex < 4; typeIndex++)
                    if (!(type[typeIndex] is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
                        throw new InvalidDataException();
                if ((type[2] & 0x20) != 0) throw new InvalidDataException();
                string typeName = Encoding.ASCII.GetString(type);
                if (IsUniquePngChunk(typeName) && !seenUniqueChunks.Add(typeName)) throw new InvalidDataException();
                if (IsBeforeIdatChunk(typeName) && sawData) throw new InvalidDataException();
                if (IsBeforePlteChunk(typeName) && sawPalette) throw new InvalidDataException();
                uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + length, 4));
                if (PngCrc32(bytes.AsSpan(offset + 4, 4 + length)) != storedCrc) throw new InvalidDataException();

                if (!sawHeader)
                {
                    if (!type.SequenceEqual("IHDR"u8) || length != 13) throw new InvalidDataException();
                    uint pngWidth = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                    uint pngHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                    byte bitDepth = data[8], colorType = data[9];
                    interlace = data[12];
                    imageBitDepth = bitDepth;
                    imageColorType = colorType;
                    int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                    bool validDepth = colorType switch
                    {
                        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
                        2 or 4 or 6 => bitDepth is 8 or 16,
                        3 => bitDepth is 1 or 2 or 4 or 8,
                        _ => false
                    };
                    if (pngWidth == 0 || pngHeight == 0 || pngWidth > int.MaxValue || pngHeight > int.MaxValue
                        || channels == 0 || !validDepth || data[10] != 0 || data[11] != 0 || interlace > 1
                        || (colorType == 3 && data[8] > 8))
                        throw new InvalidDataException();
                    width = (int)pngWidth;
                    height = (int)pngHeight;
                    bitsPerPixel = channels * bitDepth;
                    sawHeader = true;
                }
                else if (type.SequenceEqual("IHDR"u8)) throw new InvalidDataException();

                if (type.SequenceEqual("PLTE"u8))
                {
                    if (sawData || sawPalette || length is < 3 or > 768 || length % 3 != 0
                        || sawPaletteDependentChunk || imageColorType is 0 or 4
                        || (imageColorType == 3 && length / 3 > (1 << imageBitDepth)))
                        throw new InvalidDataException();
                    paletteEntries = length / 3;
                    sawPalette = true;
                }
                ValidateKnownPngChunk(typeName, data, imageColorType, imageBitDepth, paletteEntries, sawPalette);
                if (typeName is "bKGD" or "hIST" or "tRNS") sawPaletteDependentChunk = true;
                else if (type.SequenceEqual("IDAT"u8))
                {
                    if (sawEnd || dataEnded || (imageColorType == 3 && !sawPalette)) throw new InvalidDataException();
                    compressed.Write(data);
                    sawData = true;
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    if (length != 0 || !sawData || offset + 12 != bytes.Length) throw new InvalidDataException();
                    sawEnd = true;
                    offset += 12;
                    break;
                }
                else
                {
                    if (sawData) dataEnded = true;
                    if (!IsAncillary(type) && !type.SequenceEqual("IHDR"u8)) throw new InvalidDataException();
                }

                offset += checked(length + 12);
            }
            if (!sawHeader || !sawData || !sawEnd || offset != bytes.Length) throw new InvalidDataException();

            long expectedDecodedLength = GetDecodedLength(width, height, bitsPerPixel, interlace);
            if (expectedDecodedLength > 512L * 1024 * 1024) throw new InvalidDataException();
            compressed.Position = 0;
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            ValidateDecodedScanlines(zlib, width, height, bitsPerPixel, interlace, expectedDecodedLength);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException($"Referenced legacy image is not a valid PNG: {name}.", exception);
        }
    }

    private static uint PngCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes) crc = PngCrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildPngCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }

    private static bool IsAncillary(ReadOnlySpan<byte> type) => (type[0] & 0x20) != 0;

    private static bool IsUniquePngChunk(string type) => type is
        "cHRM" or "cICP" or "gAMA" or "iCCP" or "mDCV" or "cLLI" or "sBIT" or "sRGB" or
        "bKGD" or "hIST" or "tRNS" or "pHYs" or "eXIf" or "tIME";

    private static bool IsBeforeIdatChunk(string type) => type is
        "cHRM" or "cICP" or "gAMA" or "iCCP" or "mDCV" or "cLLI" or "sBIT" or "sRGB" or
        "bKGD" or "hIST" or "tRNS" or "pHYs" or "sPLT" or "eXIf";

    private static bool IsBeforePlteChunk(string type) => type is
        "cHRM" or "cICP" or "gAMA" or "iCCP" or "mDCV" or "cLLI" or "sBIT" or "sRGB";

    private static void ValidateKnownPngChunk(string type, ReadOnlySpan<byte> data, byte colorType, byte bitDepth,
        int paletteEntries, bool sawPalette)
    {
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 3, 4 => 2, 6 => 4, _ => 0 };
        switch (type)
        {
            case "cHRM" when data.Length != 32: throw new InvalidDataException();
            case "cICP" when data.Length != 4: throw new InvalidDataException();
            case "gAMA" when data.Length != 4 || BinaryPrimitives.ReadUInt32BigEndian(data) == 0: throw new InvalidDataException();
            case "sRGB" when data.Length != 1 || data[0] > 3: throw new InvalidDataException();
            case "pHYs" when data.Length != 9 || data[8] > 1: throw new InvalidDataException();
            case "tIME":
                if (data.Length != 7 || BinaryPrimitives.ReadUInt16BigEndian(data) == 0 || data[2] is 0 or > 12
                    || data[3] is 0 or > 31 || data[4] > 23 || data[5] > 59 || data[6] > 60)
                    throw new InvalidDataException();
                break;
            case "bKGD":
                int backgroundLength = colorType switch { 0 or 4 => 2, 2 or 6 => 6, 3 => 1, _ => 0 };
                if (backgroundLength == 0 || data.Length != backgroundLength || (colorType == 3 && !sawPalette)
                    || (colorType == 3 && data[0] >= paletteEntries)) throw new InvalidDataException();
                break;
            case "hIST":
                if (colorType != 3 || !sawPalette || data.Length != paletteEntries * 2) throw new InvalidDataException();
                break;
            case "tRNS":
                bool validTransparency = colorType switch
                {
                    0 => data.Length == 2,
                    2 => data.Length == 6,
                    3 => sawPalette && data.Length > 0 && data.Length <= paletteEntries,
                    _ => false
                };
                if (!validTransparency) throw new InvalidDataException();
                break;
            case "sBIT":
                int significantBitsLength = colorType == 3 ? 3 : channels;
                byte maxSignificantBits = colorType == 3 ? (byte)8 : bitDepth;
                if (significantBitsLength == 0 || data.Length != significantBitsLength) throw new InvalidDataException();
                foreach (byte value in data)
                    if (value == 0 || value > maxSignificantBits) throw new InvalidDataException();
                break;
            case "sPLT":
                int keywordEnd = data.IndexOf((byte)0);
                if (keywordEnd is < 1 or > 79 || keywordEnd + 2 >= data.Length || data[keywordEnd + 1] is not (8 or 16))
                    throw new InvalidDataException();
                int entrySize = data[keywordEnd + 1] == 8 ? 6 : 10;
                if (data.Length - keywordEnd - 2 == 0 || (data.Length - keywordEnd - 2) % entrySize != 0)
                    throw new InvalidDataException();
                break;
        }
    }

    private static long GetDecodedLength(int width, int height, int bitsPerPixel, byte interlace)
    {
        if (interlace == 0) return checked((long)height * (1 + ((long)width * bitsPerPixel + 7) / 8));
        ReadOnlySpan<(int X, int Y, int Dx, int Dy)> passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
        long total = 0;
        foreach (var pass in passes)
        {
            int passWidth = width <= pass.X ? 0 : (width - pass.X + pass.Dx - 1) / pass.Dx;
            int passHeight = height <= pass.Y ? 0 : (height - pass.Y + pass.Dy - 1) / pass.Dy;
            if (passWidth > 0 && passHeight > 0) total = checked(total + (long)passHeight * (1 + ((long)passWidth * bitsPerPixel + 7) / 8));
        }
        return total;
    }

    private static void ValidateDecodedScanlines(Stream zlib, int width, int height, int bitsPerPixel, byte interlace, long expectedLength)
    {
        long total = 0;
        if (interlace == 0) ValidatePass(zlib, width, height, bitsPerPixel, ref total);
        else
        {
            ReadOnlySpan<(int X, int Y, int Dx, int Dy)> passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
            foreach (var pass in passes)
            {
                int passWidth = width <= pass.X ? 0 : (width - pass.X + pass.Dx - 1) / pass.Dx;
                int passHeight = height <= pass.Y ? 0 : (height - pass.Y + pass.Dy - 1) / pass.Dy;
                if (passWidth > 0 && passHeight > 0) ValidatePass(zlib, passWidth, passHeight, bitsPerPixel, ref total);
            }
        }
        if (total != expectedLength || zlib.ReadByte() != -1) throw new InvalidDataException("PNG image data length is invalid.");
    }

    private static void ValidatePass(Stream zlib, int width, int height, int bitsPerPixel, ref long total)
    {
        int rowBytes = checked((int)(((long)width * bitsPerPixel + 7) / 8));
        byte[] row = new byte[rowBytes + 1];
        for (int y = 0; y < height; y++)
        {
            int read = 0;
            while (read < row.Length)
            {
                int count = zlib.Read(row, read, row.Length - read);
                if (count == 0) throw new InvalidDataException("PNG image data is truncated.");
                read += count;
            }
            if (row[0] > 4) throw new InvalidDataException("PNG scanline filter is invalid.");
            total = checked(total + row.Length);
        }
    }

    private async Task BackupAsync(string migrationId, IReadOnlyList<MigrationSource> sources, CancellationToken cancellationToken)
    {
        string destination = Path.Combine(_backupRoot, migrationId);
        foreach (var source in sources)
        {
            string target = Path.GetFullPath(Path.Combine(destination, source.Relative));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Migration backup path escaped its destination.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                string savedHash = Convert.ToHexString(await HashFileAsync(target, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(savedHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Existing migration backup does not match source data.");
                continue;
            }
            string temporary = target + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    byte[] actualHash;
                    if (source.Content is not null)
                    {
                        await stream.WriteAsync(source.Content, cancellationToken).ConfigureAwait(false);
                        actualHash = SHA256.HashData(source.Content);
                    }
                    else
                    {
                        actualHash = await CopyAndHashAsync(source.SourcePath, stream, cancellationToken).ConfigureAwait(false);
                    }
                    if (!string.Equals(Convert.ToHexString(actualHash), source.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A legacy source file changed while its migration backup was being written.");
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> CopyAndHashAsync(string sourcePath, Stream destination, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return hash.GetHashAndReset();
    }

    private async Task VerifyImportedStateAsync(IReadOnlyList<ClipboardRecord> records,
        ClipboardManager.Core.Settings.AppSettings settings, IReadOnlyList<string> pending, CancellationToken cancellationToken)
    {
        var page = await _repository.SearchAsync(new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, int.MaxValue), cancellationToken).ConfigureAwait(false);
        if (page.TotalCount < records.Count || records.Any(expected => !page.Records.Contains(expected)))
            throw new InvalidDataException("Imported legacy records failed verification.");
        if (await _repository.LoadSettingsAsync(cancellationToken).ConfigureAwait(false) != settings)
            throw new InvalidDataException("Imported settings failed verification.");
        var queued = await _repository.LoadPendingImageDeletesAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Any(name => !queued.Contains(name, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Imported pending image deletes failed verification.");
    }

    private async Task ValidateCompletionAsync(CancellationToken cancellationToken)
    {
        MigrationCompletion marker;
        try
        {
            marker = JsonSerializer.Deserialize<MigrationCompletion>(await File.ReadAllTextAsync(_completionPath, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidDataException("Migration completion marker is empty.");
        }
        catch (JsonException exception) { throw new InvalidDataException("Migration completion marker is invalid.", exception); }
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(marker.MigrationId)
            || !await _repository.HasImportedMigrationAsync(marker.MigrationId, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Completion marker does not match committed database migration state.");
        if (marker.RecordCount < 0 || marker.RecordIds is null || marker.RecordIds.Count != marker.RecordCount)
            throw new InvalidDataException("Completed migration records failed verification.");
        var markerIds = new HashSet<Guid>();
        foreach (string id in marker.RecordIds)
        {
            if (!Guid.TryParse(id, out Guid parsed) || parsed == Guid.Empty || !markerIds.Add(parsed))
                throw new InvalidDataException("Completed migration records failed verification.");
        }
        if (!string.Equals(marker.ManifestHash, ComputeCompletionManifestHash(marker.MigrationId, marker.RecordCount, marker.RecordIds), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Migration completion marker integrity check failed.");
    }

    private Task WriteJournalAsync(string migrationId, string phase, CancellationToken cancellationToken)
        => MigrationJournalStore.WriteAsync(Path.Combine(_backupRoot, migrationId, "migration.json"),
            new MigrationJournal(migrationId, phase, DateTime.UtcNow), cancellationToken);

    private async Task WriteCompletionAsync(string migrationId, IReadOnlyList<ClipboardRecord> records, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_completionPath)!);
        string temporary = _completionPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                string[] ids = records.Select(record => record.Id.ToString("D")).ToArray();
                await JsonSerializer.SerializeAsync(stream, new MigrationCompletion(migrationId, ids.Length, ids,
                    ComputeCompletionManifestHash(migrationId, ids.Length, ids)), cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _completionPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class ThemeDocument { public LegacyTheme Theme { get; init; } = LegacyTheme.Light; }
    private enum LegacyTheme { System, Light, Dark }
    private sealed record MigrationSource(string Relative, string SourcePath, byte[]? Content, string Sha256);
    private sealed record MigrationCompletion(string MigrationId, int RecordCount, IReadOnlyList<string>? RecordIds, string ManifestHash);

    private static string ComputeCompletionManifestHash(string migrationId, int recordCount, IEnumerable<string> recordIds)
    {
        string canonical = $"{migrationId}\n{recordCount}\n{string.Join("\n", recordIds.OrderBy(id => id, StringComparer.Ordinal))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
