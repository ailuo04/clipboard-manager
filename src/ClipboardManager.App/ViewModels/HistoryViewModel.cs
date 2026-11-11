using System.Collections.ObjectModel;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClipboardManager.App.ViewModels;

/// <summary>Provides cancellable, bounded history paging and selection behavior for history/favorites views.</summary>
public sealed record HistoryClearSummary(int TotalRecords, int FavoriteRecords, int ImageRecords);

public sealed class HistoryViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(160);
    private readonly IHistoryService _history;
    private readonly IImageStore _imageStore;
    private readonly IClipboardWriter _clipboardWriter;
    private readonly int _pageSize;
    private readonly TimeSpan _debounce;
    private CancellationTokenSource? _queryLifetime;
    private Task _pendingSearch = Task.CompletedTask;
    private int _queryGeneration;
    private bool _isDisposed;
    private bool _hasMore;
    private bool _isLoading;
    private bool _isEmpty;
    private string? _errorMessage;
    private string _searchText = string.Empty;
    private HistoryKindFilter _kindFilter;
    private bool _favoritesOnly;
    private RecordItemViewModel? _selectedItem;
    private int _totalCount;

    public HistoryViewModel(IHistoryService history, IImageStore imageStore, IClipboardWriter clipboardWriter,
        int pageSize = 50, TimeSpan? searchDebounce = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _imageStore = imageStore ?? throw new ArgumentNullException(nameof(imageStore));
        _clipboardWriter = clipboardWriter ?? throw new ArgumentNullException(nameof(clipboardWriter));
        if (pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _pageSize = pageSize;
        _debounce = searchDebounce ?? SearchDebounce;
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(searchDebounce));
    }

    public event EventHandler? CopyCompleted;
    public ObservableCollection<RecordItemViewModel> Items { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            QueueQuery(debounce: true);
        }
    }

    public HistoryKindFilter KindFilter
    {
        get => _kindFilter;
        private set => SetProperty(ref _kindFilter, value);
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        private set => SetProperty(ref _favoritesOnly, value);
    }

    public RecordItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }
    public int TotalCount { get => _totalCount; private set => SetProperty(ref _totalCount, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    public Task WaitForSearchAsync() => _pendingSearch;

    public Task LoadInitialAsync(CancellationToken cancellationToken = default)
        => StartQueryAsync(debounce: false, cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => StartQueryAsync(debounce: false, cancellationToken);

    public async Task LoadNextPageAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!HasMore || IsLoading) return;
        CancellationTokenSource? lifetime = _queryLifetime;
        if (lifetime is null)
        {
            await LoadInitialAsync(cancellationToken);
            return;
        }
        await FetchPageAsync(_queryGeneration, offset: Items.Count, lifetime.Token, reset: false, cancellationToken);
    }

    public async Task SetKindFilterAsync(HistoryKindFilter filter, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(filter)) throw new ArgumentOutOfRangeException(nameof(filter));
        KindFilter = filter;
        await StartQueryAsync(debounce: false, cancellationToken);
    }

    public async Task SetFavoritesOnlyAsync(bool favoritesOnly, CancellationToken cancellationToken = default)
    {
        FavoritesOnly = favoritesOnly;
        await StartQueryAsync(debounce: false, cancellationToken);
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
        => await StartQueryAsync(debounce: false, cancellationToken);

    public async Task<HistoryClearSummary> GetClearSummaryAsync(CancellationToken cancellationToken = default)
    {
        int total = 0;
        int favorites = 0;
        int images = 0;
        int offset = 0;
        const int summaryPageSize = 500;
        while (true)
        {
            HistoryPage page = await _history.SearchAsync(
                new HistoryQuery(string.Empty, HistoryKindFilter.All, false, offset, summaryPageSize), cancellationToken);
            total += page.Records.Count;
            favorites += page.Records.Count(record => record.IsFavorite);
            images += page.Records.Count(record => record.Kind == ClipboardContentKind.Image);
            if (offset + page.Records.Count >= page.TotalCount || page.Records.Count == 0) break;
            offset += page.Records.Count;
        }
        return new HistoryClearSummary(total, favorites, images);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        IsLoading = true;
        try
        {
            HistoryChangeResult result = await _history.ClearAsync(cancellationToken);
            Items.Clear();
            SelectedItem = null;
            TotalCount = 0;
            HasMore = false;
            IsEmpty = true;
            ErrorMessage = result.Warning;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { ErrorMessage = ToUserError(exception); }
        finally { IsLoading = false; }
    }
    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        RecordItemViewModel? selected = SelectedItem;
        if (selected is null) return;
        int index = Items.IndexOf(selected);
        if (index < 0) return;
        try
        {
            ErrorMessage = null;
            HistoryChangeResult result = await _history.DeleteAsync(selected.Record.Id, cancellationToken);
            if (result.DeletedIds.Contains(selected.Record.Id))
            {
                Items.Remove(selected);
                SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
                TotalCount = Math.Max(0, TotalCount - 1);
                IsEmpty = Items.Count == 0;
            }
            ErrorMessage = result.Warning;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { ErrorMessage = ToUserError(exception); }
    }

    public async Task ToggleFavoriteAsync(RecordItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item)) return;
        try
        {
            ErrorMessage = null;
            HistoryChangeResult result = await _history.SetFavoriteAsync(item.Record.Id, !item.IsFavorite, cancellationToken);
            ClipboardRecord? updated = result.Upserted.FirstOrDefault(record => record.Id == item.Record.Id);
            if (updated is not null) item.Update(updated);
            if (FavoritesOnly && updated is { IsFavorite: false })
            {
                int index = Items.IndexOf(item);
                Items.Remove(item);
                if (ReferenceEquals(SelectedItem, item)) SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
                TotalCount = Math.Max(0, TotalCount - 1);
                IsEmpty = Items.Count == 0;
            }
            ErrorMessage = result.Warning;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { ErrorMessage = ToUserError(exception); }
    }

    public async Task CopySelectedAsync(CancellationToken cancellationToken = default)
    {
        RecordItemViewModel? item = SelectedItem;
        if (item is null) return;
        try
        {
            ErrorMessage = null;
            bool copied = false;
            if (item.Record.Kind == ClipboardContentKind.Text && item.Record.Text is not null)
            {
                await _clipboardWriter.WriteTextAsync(item.Record.Text, cancellationToken);
                copied = true;
            }
            else if (item.Record.Kind == ClipboardContentKind.Image && item.Record.ImageFileName is not null)
            {
                await _clipboardWriter.WriteImageAsync(_imageStore.GetPath(item.Record.ImageFileName), cancellationToken);
                copied = true;
            }
            if (copied) CopyCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { ErrorMessage = ToUserError(exception); }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        CancellationTokenSource? lifetime = Interlocked.Exchange(ref _queryLifetime, null);
        if (lifetime is not null)
        {
            lifetime.Cancel();
            _ = _pendingSearch.ContinueWith(_ => lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private Task StartQueryAsync(bool debounce, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        CancellationTokenSource lifetime = ReplaceQueryLifetime(cancellationToken, out int generation);
        _pendingSearch = debounce
            ? DebounceAndFetchAsync(generation, lifetime)
            : FetchPageAsync(generation, 0, lifetime.Token, reset: true, CancellationToken.None);
        return _pendingSearch;
    }

    private void QueueQuery(bool debounce)
    {
        if (_isDisposed) return;
        try { _pendingSearch = StartQueryAsync(debounce, CancellationToken.None); }
        catch (ObjectDisposedException) { }
    }

    private async Task DebounceAndFetchAsync(int generation, CancellationTokenSource lifetime)
    {
        CancellationToken token = lifetime.Token;
        try
        {
            await Task.Delay(_debounce, token);
            await FetchPageAsync(generation, 0, token, reset: true, CancellationToken.None);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private CancellationTokenSource ReplaceQueryLifetime(CancellationToken externalToken, out int generation)
    {
        CancellationTokenSource next = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _queryLifetime, next);
        Task previousOperation = _pendingSearch;
        if (previous is not null)
        {
            previous.Cancel();
            _ = previousOperation.ContinueWith(_ => previous.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        generation = Interlocked.Increment(ref _queryGeneration);
        return next;
    }

    private async Task FetchPageAsync(int generation, int offset, CancellationToken queryToken, bool reset,
        CancellationToken externalToken)
    {
        if (reset)
        {
            Items.Clear();
            SelectedItem = null;
            HasMore = true;
            TotalCount = 0;
            IsEmpty = false;
        }
        IsLoading = true;
        ErrorMessage = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(queryToken, externalToken);
        try
        {
            var query = new HistoryQuery(SearchText, KindFilter, FavoritesOnly, offset, _pageSize);
            HistoryPage page = await _history.SearchAsync(query, linked.Token);
            if (generation != Volatile.Read(ref _queryGeneration) || linked.IsCancellationRequested) return;
            foreach (ClipboardRecord record in page.Records)
                if (Items.All(item => item.Record.Id != record.Id)) Items.Add(CreateItem(record));
            TotalCount = page.TotalCount;
            HasMore = offset + page.Records.Count < page.TotalCount;
            IsEmpty = Items.Count == 0;
            if (SelectedItem is null && Items.Count > 0) SelectedItem = Items[0];
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _queryGeneration)) ErrorMessage = ToUserError(exception);
        }
        finally
        {
            if (generation == Volatile.Read(ref _queryGeneration)) IsLoading = false;
        }
    }

    private RecordItemViewModel CreateItem(ClipboardRecord record)
    {
        string? imagePath = null;
        if (record.Kind == ClipboardContentKind.Image && record.ImageFileName is not null)
        {
            try { imagePath = _imageStore.GetPath(record.ImageFileName); }
            catch (Exception) { }
        }
        return new RecordItemViewModel(record, imagePath);
    }

    private static string ToUserError(Exception exception)
        => exception switch
        {
            IOException => "读取历史记录失败，请重试。",
            UnauthorizedAccessException => "无法访问本地历史数据，请检查应用数据目录权限。",
            _ => "操作没有完成，请重试。"
        };
}
