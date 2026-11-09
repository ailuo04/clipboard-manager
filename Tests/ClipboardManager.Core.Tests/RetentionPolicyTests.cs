using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using Xunit;

namespace ClipboardManager.Core.Tests;

public sealed class RetentionPolicyTests
{
    private static readonly DateTime BaseTime = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Trim_keeps_records_within_both_capacity_limits()
    {
        var records = new[]
        {
            Record(1, BaseTime, bytes: 20),
            Record(2, BaseTime.AddMinutes(1), bytes: 30)
        };

        var result = RetentionPolicy.Trim(records, Settings(maxRecords: 50, maxStorageMb: 64));

        Assert.Empty(result.DeleteIds);
        Assert.Null(result.CapacityWarning);
    }

    [Fact]
    public void Trim_deletes_oldest_non_favorite_when_record_count_exceeds_limit()
    {
        var records = Enumerable.Range(1, 51)
            .Select(id => Record(id, BaseTime.AddTicks(id)))
            .ToArray();

        var result = RetentionPolicy.Trim(records, Settings(maxRecords: 50));

        Assert.Equal(new[] { records[0].Id }, result.DeleteIds);
        Assert.Null(result.CapacityWarning);
    }

    [Fact]
    public void Trim_deletes_oldest_non_favorites_until_total_bytes_fit()
    {
        var oldest = Record(1, BaseTime, bytes: 10 * 1024 * 1024);
        var nextOldest = Record(2, BaseTime.AddMinutes(1), bytes: 40 * 1024 * 1024);
        var newest = Record(3, BaseTime.AddMinutes(2), bytes: 30 * 1024 * 1024);

        var result = RetentionPolicy.Trim(new[] { newest, nextOldest, oldest }, Settings(maxStorageMb: 64));

        Assert.Equal(new[] { oldest.Id, nextOldest.Id }, result.DeleteIds);
        Assert.Null(result.CapacityWarning);
    }

    [Fact]
    public void Trim_never_deletes_favorites_while_removing_non_favorites()
    {
        var favorite = Record(1, BaseTime, bytes: 64 * 1024 * 1024, isFavorite: true);
        var nonFavorite = Record(2, BaseTime.AddMinutes(1), bytes: 1);

        var result = RetentionPolicy.Trim(new[] { favorite, nonFavorite }, Settings(maxStorageMb: 64));

        Assert.Equal(new[] { nonFavorite.Id }, result.DeleteIds);
        Assert.Null(result.CapacityWarning);
    }

    [Fact]
    public void Trim_preserves_over_capacity_favorites_and_returns_warning()
    {
        var records = new[]
        {
            Record(1, BaseTime, bytes: 64 * 1024 * 1024, isFavorite: true),
            Record(2, BaseTime.AddMinutes(1), bytes: 1, isFavorite: true)
        };

        var result = RetentionPolicy.Trim(records, Settings(maxStorageMb: 64));

        Assert.Empty(result.DeleteIds);
        Assert.False(string.IsNullOrWhiteSpace(result.CapacityWarning));
    }

    [Fact]
    public void Trim_satisfies_count_and_byte_limits_in_one_pass()
    {
        var oldest = Record(1, BaseTime, bytes: 10 * 1024 * 1024);
        var nextOldest = Record(2, BaseTime.AddMinutes(1), bytes: 40 * 1024 * 1024);
        var newerLargeRecord = Record(3, BaseTime.AddMinutes(2), bytes: 20 * 1024 * 1024);
        var zeroByteRecords = Enumerable.Range(4, 49)
            .Select(id => Record(id, BaseTime.AddMinutes(3), bytes: 0))
            .ToArray();
        var records = zeroByteRecords.Concat(new[] { newerLargeRecord, nextOldest, oldest }).ToArray();

        var result = RetentionPolicy.Trim(records, Settings(maxRecords: 50, maxStorageMb: 64));

        Assert.Equal(new[] { oldest.Id, nextOldest.Id }, result.DeleteIds);
        Assert.True(records.Length - result.DeleteIds.Count <= 50);
        var deletedIds = result.DeleteIds.ToHashSet();
        var retainedBytes = records.Where(record => !deletedIds.Contains(record.Id))
            .Sum(record => (decimal)record.ByteLength);
        Assert.True(retainedBytes <= 64m * 1024 * 1024);
        Assert.Null(result.CapacityWarning);
    }

    [Fact]
    public void Trim_warns_when_favorite_count_alone_exceeds_limit_below_byte_budget()
    {
        var records = Enumerable.Range(1, 51)
            .Select(id => Record(id, BaseTime.AddTicks(id), bytes: 1, isFavorite: true))
            .ToArray();

        var result = RetentionPolicy.Trim(records, Settings(maxRecords: 50, maxStorageMb: 64));

        Assert.Empty(result.DeleteIds);
        Assert.False(string.IsNullOrWhiteSpace(result.CapacityWarning));
        Assert.True(records.Length > 50);
        Assert.True(records.Sum(record => record.ByteLength) < 64L * 1024 * 1024);
    }

    [Fact]
    public void Trim_treats_exact_count_and_byte_capacity_as_within_limits()
    {
        var records = Enumerable.Range(1, 50)
            .Select(id => Record(id, BaseTime.AddTicks(id), bytes: id == 50 ? 15 * 1024 * 1024 : 1024 * 1024))
            .ToArray();

        var result = RetentionPolicy.Trim(records, Settings(maxRecords: 50, maxStorageMb: 64));

        Assert.Empty(result.DeleteIds);
        Assert.Null(result.CapacityWarning);
    }

    [Fact]
    public void Trim_handles_zero_byte_records_using_count_limit()
    {
        var records = Enumerable.Range(1, 51)
            .Select(id => Record(id, BaseTime.AddTicks(id), bytes: 0))
            .ToArray();

        var result = RetentionPolicy.Trim(records, Settings(maxRecords: 50));

        Assert.Equal(new[] { records[0].Id }, result.DeleteIds);
        Assert.Null(result.CapacityWarning);
    }

    [Fact]
    public void Trim_orders_equal_timestamps_deterministically_by_record_id()
    {
        var higherId = Record(2, BaseTime);
        var lowerId = Record(1, BaseTime);
        var records = new[] { higherId, lowerId }
            .Concat(Enumerable.Range(10, 49).Select(id => Record(id, BaseTime.AddMinutes(1))))
            .ToArray();

        var result = RetentionPolicy.Trim(records, Settings(maxRecords: 50));

        Assert.Equal(new[] { lowerId.Id }, result.DeleteIds);
    }

    [Fact]
    public void Trim_uses_full_utc_ticks_when_records_share_a_millisecond()
    {
        var laterWithinMillisecond = Record(1, BaseTime.AddTicks(9_000));
        var earlierWithinMillisecond = Record(2, BaseTime.AddTicks(1_000));
        var records = new[] { laterWithinMillisecond, earlierWithinMillisecond }
            .Concat(Enumerable.Range(10, 49).Select(id => Record(id, BaseTime.AddMilliseconds(1))))
            .ToArray();

        var result = RetentionPolicy.Trim(records, Settings(maxRecords: 50));

        Assert.Equal(new[] { earlierWithinMillisecond.Id }, result.DeleteIds);
    }

    [Theory]
    [InlineData(49, 50, 50)]
    [InlineData(5001, 5000, 5001)]
    public void Trim_normalizes_record_limit_to_supported_range(int configuredLimit, int expectedLimit, int recordCount)
    {
        var records = Enumerable.Range(1, recordCount)
            .Select(id => Record(id, BaseTime.AddTicks(id)))
            .ToArray();

        var result = RetentionPolicy.Trim(records, Settings(maxRecords: configuredLimit));

        Assert.Equal(recordCount - expectedLimit, result.DeleteIds.Count);
    }

    [Theory]
    [InlineData(63, 64)]
    [InlineData(4097, 4096)]
    public void Trim_normalizes_storage_limit_to_supported_range(int configuredLimitMb, int normalizedLimitMb)
    {
        var records = new[]
        {
            Record(1, BaseTime, bytes: (long)normalizedLimitMb * 1024 * 1024)
        };

        var result = RetentionPolicy.Trim(records, Settings(maxStorageMb: configuredLimitMb));

        Assert.Empty(result.DeleteIds);
        Assert.Null(result.CapacityWarning);
    }

    [Theory]
    [InlineData(49, 50)]
    [InlineData(5001, 5000)]
    public void AppSettings_normalizes_record_limit_to_supported_bounds(int configured, int expected)
    {
        Assert.Equal(expected, Settings(maxRecords: configured).Normalize().MaxRecords);
    }

    [Theory]
    [InlineData(63, 64)]
    [InlineData(4097, 4096)]
    public void AppSettings_normalizes_storage_limit_to_supported_bounds(int configured, int expected)
    {
        Assert.Equal(expected, Settings(maxStorageMb: configured).Normalize().MaxStorageMb);
    }

    [Fact]
    public void Trim_uses_the_normalized_upper_storage_limit()
    {
        var record = Record(1, BaseTime, bytes: 4097L * 1024 * 1024);

        var result = RetentionPolicy.Trim(new[] { record }, Settings(maxStorageMb: 4097));

        Assert.Equal(new[] { record.Id }, result.DeleteIds);
        Assert.Null(result.CapacityWarning);
    }

    [Fact]
    public void ClipboardRecord_preserves_its_positional_contract()
    {
        var record = Record(1, BaseTime);

        var (id, kind, _, _, _, _, _, _, copiedAtUtc, isFavorite) = record;

        Assert.Equal(record.Id, id);
        Assert.Equal(ClipboardContentKind.Text, kind);
        Assert.Equal(BaseTime, copiedAtUtc);
        Assert.False(isFavorite);
    }

    [Fact]
    public void ClipboardSnapshot_preserves_its_positional_contract()
    {
        var snapshot = new ClipboardSnapshot(
            ClipboardContentKind.Text, "text", ReadOnlyMemory<byte>.Empty, BaseTime);

        var (kind, text, pngBytes, capturedAtUtc) = snapshot;

        Assert.Equal(ClipboardContentKind.Text, kind);
        Assert.Equal("text", text);
        Assert.Empty(pngBytes.ToArray());
        Assert.Equal(BaseTime, capturedAtUtc);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ClipboardRecord_rejects_non_utc_timestamps(DateTimeKind kind)
    {
        var invalidTime = DateTime.SpecifyKind(BaseTime, kind);

        Assert.Throws<ArgumentException>(() => Record(1, invalidTime));
    }

    [Fact]
    public void ClipboardSnapshot_rejects_non_utc_timestamps()
    {
        var invalidTime = DateTime.SpecifyKind(BaseTime, DateTimeKind.Local);

        Assert.Throws<ArgumentException>(() => new ClipboardSnapshot(
            ClipboardContentKind.Text, Text: "text", PngBytes: ReadOnlyMemory<byte>.Empty, CapturedAtUtc: invalidTime));
    }

    private static AppSettings Settings(int maxRecords = 500, int maxStorageMb = 512)
        => new(maxRecords, maxStorageMb, AlwaysOnTop: false, AutoCheckUpdates: true,
            MinimizeToTray: false, HideAfterCopy: true, ThemePreference: "System");

    private static ClipboardRecord Record(
        int id,
        DateTime copiedAtUtc,
        long bytes = 1,
        bool isFavorite = false)
        => new(Guid.Parse($"00000000-0000-0000-0000-{id:D12}"), ClipboardContentKind.Text,
            Text: "record", ImageFileName: null, ByteLength: bytes, ImageWidth: 0, ImageHeight: 0,
            ContentHash: new string('a', 64), CopiedAtUtc: copiedAtUtc, IsFavorite: isFavorite);
}
