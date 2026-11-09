using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.Settings;

namespace ClipboardManager.Core.History;

public interface IHistoryService
{
    Task<HistoryPage> SearchAsync(HistoryQuery query, CancellationToken cancellationToken);
    Task<HistoryChangeResult> AddAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken);
    Task<HistoryChangeResult> DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<HistoryChangeResult> ClearAsync(CancellationToken cancellationToken);
    Task<HistoryChangeResult> SetFavoriteAsync(Guid id, bool isFavorite, CancellationToken cancellationToken);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task<HistoryChangeResult> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken);
}
