using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Storage.Sqlite;
using Microsoft.Data.Sqlite;

const int recordCount = 5_000;
const int repeatCount = 20;
string? outputPath = null;
for (int index = 0; index < args.Length; index++)
{
    if (args[index] == "--output" && index + 1 < args.Length)
        outputPath = args[++index];
    else
        throw new ArgumentException("Usage: --output <json-path>");
}
if (string.IsNullOrWhiteSpace(outputPath))
    throw new ArgumentException("An output path is required.");

string root = Path.Combine(Path.GetTempPath(), $"cm-history-benchmark-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    string databasePath = Path.Combine(root, "history.db");
    var repository = new SqliteHistoryRepository(databasePath);
    var records = Enumerable.Range(0, recordCount)
        .Select(CreateRecord)
        .ToArray();

    var seedTimer = Stopwatch.StartNew();
    await repository.ImportLegacyAsync(
        "synthetic-performance-fixture-v1",
        records,
        new AppSettings(5_000, 512, false, true, false, true, "Light"),
        [],
        CancellationToken.None);
    seedTimer.Stop();

    var connectionFactory = new SqliteConnectionFactory(databasePath);
    string sqliteVersion;
    await using (SqliteConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None))
    await using (SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText = "SELECT sqlite_version();";
        sqliteVersion = Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    HistoryQuery longQuery = new("needle-token", HistoryKindFilter.All, false, 0, 100);
    var firstSearchTimer = Stopwatch.StartNew();
    HistoryPage firstSearch = await repository.SearchAsync(longQuery, CancellationToken.None);
    firstSearchTimer.Stop();
    if (firstSearch.TotalCount != 50)
        throw new InvalidOperationException($"Long-query fixture match count mismatch: {firstSearch.TotalCount}.");

    var allRecordsTimer = Stopwatch.StartNew();
    HistoryPage allRecords = await repository.SearchAsync(
        new HistoryQuery(string.Empty, HistoryKindFilter.All, false, 0, recordCount),
        CancellationToken.None);
    allRecordsTimer.Stop();
    if (allRecords.TotalCount != recordCount || allRecords.Records.Count != recordCount)
        throw new InvalidOperationException($"Synthetic fixture count mismatch: {allRecords.TotalCount}.");

    var repeatSamples = new List<double>(repeatCount);
    for (int index = 0; index < repeatCount; index++)
    {
        var timer = Stopwatch.StartNew();
        HistoryPage result = await repository.SearchAsync(longQuery, CancellationToken.None);
        timer.Stop();
        if (result.TotalCount != firstSearch.TotalCount)
            throw new InvalidOperationException("Repeated search results changed between runs.");
        repeatSamples.Add(timer.Elapsed.TotalMilliseconds);
    }

    var shortQueryTimer = Stopwatch.StartNew();
    HistoryPage shortQuery = await repository.SearchAsync(
        new HistoryQuery("a", HistoryKindFilter.All, false, 0, 100),
        CancellationToken.None);
    shortQueryTimer.Stop();

    long managedBytesBefore = GC.GetTotalMemory(forceFullCollection: true);
    long workingSetBefore = Environment.WorkingSet;
    for (int index = 0; index < repeatCount; index++)
        _ = await repository.SearchAsync(longQuery, CancellationToken.None);
    long managedBytesAfter = GC.GetTotalMemory(forceFullCollection: true);
    long workingSetAfter = Environment.WorkingSet;

    var report = new
    {
        schema = 1,
        fixture = new
        {
            records = recordCount,
            textRecords = recordCount / 2,
            imageRecords = recordCount / 2,
            textLengthCharacters = 256,
            matchingTextRecords = 50,
            matchingPercent = 2,
            recordSource = "synthetic-only; no real clipboard or user data"
        },
        runtime = new
        {
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            sqliteVersion,
            sqliteProviderVersion = typeof(SqliteConnection).Assembly.GetName().Version?.ToString()
        },
        measurements = new
        {
            databaseBytes = Directory.EnumerateFiles(root).Sum(path => new FileInfo(path).Length),
            seedMilliseconds = seedTimer.Elapsed.TotalMilliseconds,
            firstAllRecordsSearchMilliseconds = allRecordsTimer.Elapsed.TotalMilliseconds,
            firstLongSearchMilliseconds = firstSearchTimer.Elapsed.TotalMilliseconds,
            firstLongSearchMatchCount = firstSearch.TotalCount,
            repeatLongSearchMilliseconds = Summarize(repeatSamples),
            shortSearchMilliseconds = shortQueryTimer.Elapsed.TotalMilliseconds,
            shortSearchMatchCount = shortQuery.TotalCount,
            repeatedSearchCountForMemorySample = repeatCount,
            managedHeapBytesBeforeRepeatedSample = managedBytesBefore,
            managedHeapBytesAfterRepeatedSample = managedBytesAfter,
            managedHeapDeltaBytes = managedBytesAfter - managedBytesBefore,
            processWorkingSetBytesBeforeRepeatedSample = workingSetBefore,
            processWorkingSetBytesAfterRepeatedSample = workingSetAfter,
            processWorkingSetDeltaBytes = workingSetAfter - workingSetBefore
        }
    };

    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    });
    string fullOutputPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
    await File.WriteAllTextAsync(fullOutputPath, json + Environment.NewLine, new UTF8Encoding(false));
    Console.WriteLine($"Synthetic history benchmark wrote: {fullOutputPath}");
}
finally
{
    string resolvedRoot = Path.GetFullPath(root);
    string expectedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!resolvedRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(resolvedRoot) is not string directoryName ||
        !directoryName.StartsWith("cm-history-benchmark-", StringComparison.Ordinal) ||
        !Guid.TryParseExact(directoryName["cm-history-benchmark-".Length..], "N", out _))
        throw new InvalidOperationException($"Refusing to remove an unexpected benchmark directory: {resolvedRoot}");
    if (Directory.Exists(resolvedRoot))
        Directory.Delete(resolvedRoot, recursive: true);
}

static ClipboardRecord CreateRecord(int index)
{
    bool isText = index % 2 == 0;
    string? text = null;
    if (isText)
    {
        string prefix = $"Synthetic clipboard record {index:D4}; sample payload with searchable material";
        string marker = index % 100 == 0 ? " needle-token" : string.Empty;
        text = prefix + marker + new string((char)('a' + index % 26), 256 - prefix.Length - marker.Length);
    }
    byte[] hashInput = Encoding.UTF8.GetBytes(text ?? $"synthetic-image-{index:D4}");
    string hash = Convert.ToHexStringLower(SHA256.HashData(hashInput));
    return new ClipboardRecord(
        GuidFromIndex(index),
        isText ? ClipboardContentKind.Text : ClipboardContentKind.Image,
        text,
        isText ? null : $"synthetic-{index:D4}.png",
        isText ? Encoding.UTF8.GetByteCount(text!) : 16_384,
        isText ? 0 : 64,
        isText ? 0 : 64,
        hash,
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(-index),
        index % 17 == 0);
}

static Guid GuidFromIndex(int index)
{
    Span<byte> bytes = stackalloc byte[16];
    BitConverter.TryWriteBytes(bytes, index);
    bytes[15] = 0x5A;
    return new Guid(bytes);
}

static object Summarize(IReadOnlyCollection<double> values)
{
    double[] sorted = values.Order().ToArray();
    return new
    {
        samples = sorted.Length,
        medianMilliseconds = Percentile(sorted, 0.50),
        p95Milliseconds = Percentile(sorted, 0.95),
        minimumMilliseconds = sorted[0],
        maximumMilliseconds = sorted[^1]
    };
}

static double Percentile(double[] sorted, double percentile)
    => sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];






