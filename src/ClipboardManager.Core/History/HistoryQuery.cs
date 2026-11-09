namespace ClipboardManager.Core.History;

public enum HistoryKindFilter
{
    All,
    Text,
    Image
}

public sealed record HistoryQuery(
    string SearchText,
    HistoryKindFilter Kind,
    bool FavoritesOnly,
    int Offset,
    int Limit);

public sealed record HistoryPage(
    IReadOnlyList<ClipboardRecord> Records,
    int TotalCount,
    int Offset,
    int Limit);
