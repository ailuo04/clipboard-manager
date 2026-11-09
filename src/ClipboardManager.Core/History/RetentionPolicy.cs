namespace ClipboardManager.Core.History;

public static class RetentionPolicy
{
    private const int BytesPerMegabyte = 1024 * 1024;
    private const string OverCapacityWarning = "收藏记录已超过容量限制；它们不会被自动清理。";

    public static RetentionResult Trim(
        IReadOnlyCollection<ClipboardRecord> records,
        ClipboardManager.Core.Settings.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(settings);

        foreach (var record in records)
            ArgumentNullException.ThrowIfNull(record);

        var normalizedSettings = settings.Normalize();
        var maxRecords = normalizedSettings.MaxRecords;
        var maxBytes = (decimal)normalizedSettings.MaxStorageMb * BytesPerMegabyte;
        decimal totalBytes = records.Sum(record => (decimal)record.ByteLength);
        int remainingCount = records.Count;
        var deleteIds = new List<Guid>();

        foreach (var candidate in records
                     .Where(record => !record.IsFavorite)
                     .OrderBy(record => record.CopiedAtUtc.Ticks)
                     .ThenBy(record => record.Id))
        {
            if (remainingCount <= maxRecords && totalBytes <= maxBytes)
                break;

            deleteIds.Add(candidate.Id);
            remainingCount--;
            totalBytes -= candidate.ByteLength;
        }

        string? warning = remainingCount > maxRecords || totalBytes > maxBytes
            ? OverCapacityWarning
            : null;

        return new RetentionResult(Array.AsReadOnly(deleteIds.ToArray()), warning);
    }
}
