using ClipboardManager.Core.History;
using ClipboardManager.Storage.Sqlite;
using Xunit;

namespace ClipboardManager.Storage.Tests;

public sealed class SearchCompatibilityTests
{
    public static TheoryData<string> Queries => new()
    {
        "CLIPBOARD",
        "中",
        "中文",
        "中文测",
        "🙂",
        "👩‍💻",
        "'quoted'",
        "100%_match",
        "line one\r\nline TWO",
        " leading and trailing ",
        "needle-needle",
        "ı",
        "İ",
        "' OR 1=1 --",
        "missing"
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Search_results_match_string_ordinal_ignore_case_contains(string query)
    {
        await using var database = new SqliteTestDatabase();
        var repository = new SqliteHistoryRepository(database.ConnectionFactory);
        await repository.InitializeAsync(CancellationToken.None);

        string longText = "prefix-" + new string('x', 12_000) + "-needle-needle-suffix";
        var records = new[]
        {
            SqliteHistoryRepositoryTests.TextRecord("Clipboard clipboard CLIPBOARD"),
            SqliteHistoryRepositoryTests.TextRecord("大小写测试 Chinese中文测试"),
            SqliteHistoryRepositoryTests.TextRecord("emoji 🙂 and 👩‍💻 end"),
            SqliteHistoryRepositoryTests.TextRecord("'quoted' plus \"double\" quotes"),
            SqliteHistoryRepositoryTests.TextRecord("literal 100%_match"),
            SqliteHistoryRepositoryTests.TextRecord("line one\r\nline TWO"),
            SqliteHistoryRepositoryTests.TextRecord(" leading and trailing "),
            SqliteHistoryRepositoryTests.TextRecord(longText),
            SqliteHistoryRepositoryTests.TextRecord("Turkish Iıİi"),
            SqliteHistoryRepositoryTests.TextRecord("literal ' OR 1=1 -- only"),
            SqliteHistoryRepositoryTests.TextRecord("not a match")
        };

        foreach (var record in records)
            await repository.ApplyAsync(new HistoryMutation(record, [], [], [], null), CancellationToken.None);

        var expected = records
            .Where(record => record.Text!.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.CopiedAtUtc.Ticks)
            .ThenBy(record => record.Id)
            .Select(record => record.Id)
            .ToArray();
        var actual = await repository.SearchAsync(
            new HistoryQuery(query, HistoryKindFilter.All, false, 0, records.Length + 1),
            CancellationToken.None);

        Assert.Equal(expected, actual.Records.Select(record => record.Id));
        Assert.Equal(expected.Length, actual.TotalCount);
    }

    [Fact]
    public async Task Search_paginates_the_ordinal_ignore_case_result_set_before_returning_page()
    {
        await using var database = new SqliteTestDatabase();
        var repository = new SqliteHistoryRepository(database.ConnectionFactory);
        await repository.InitializeAsync(CancellationToken.None);
        var records = Enumerable.Range(0, 5)
            .Select(index => SqliteHistoryRepositoryTests.TextRecord(
                $"needle {index}",
                DateTime.UnixEpoch.AddTicks(index * 10L).ToUniversalTime(),
                Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}")))
            .ToArray();
        foreach (var record in records)
            await repository.ApplyAsync(new HistoryMutation(record, [], [], [], null), CancellationToken.None);

        var page = await repository.SearchAsync(
            new HistoryQuery("NEEDLE", HistoryKindFilter.All, false, 2, 2),
            CancellationToken.None);

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Offset);
        Assert.Equal(2, page.Limit);
        Assert.Equal(records.OrderByDescending(record => record.CopiedAtUtc.Ticks).ThenBy(record => record.Id)
            .Skip(2).Take(2).Select(record => record.Id), page.Records.Select(record => record.Id));
    }
}
