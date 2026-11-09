using ClipboardManager.Core.Settings;

namespace ClipboardManager.Core.History;

public sealed record HistoryMutation(
    ClipboardRecord? Upsert,
    IReadOnlyList<Guid> DeleteIds,
    IReadOnlyList<string> PendingImageDeletes,
    IReadOnlyList<string> CompletedImageDeletes,
    AppSettings? Settings);
