using ClipboardManager.App.ViewModels;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class HistoryViewModelTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LoadInitial_and_LoadNextPage_request_bounded_pages_without_duplicate_items()
    {
        var service = new FakeHistoryService();
        service.Records.AddRange([Record("first"), Record("second"), Record("third")]);
        var vm = CreateViewModel(service, pageSize: 2);

        await vm.LoadInitialAsync();
        Assert.Equal(["first", "second"], vm.Items.Select(item => item.Record.Text));
        Assert.True(vm.HasMore);
        await vm.LoadNextPageAsync();

        Assert.Equal(["first", "second", "third"], vm.Items.Select(item => item.Record.Text));
        Assert.False(vm.HasMore);
        Assert.Equal([0, 2], service.Queries.Select(query => query.Offset));
        Assert.All(service.Queries, query => Assert.Equal(2, query.Limit));
    }

    [Fact]
    public async Task SearchText_waits_for_the_debounce_window_then_uses_latest_query_only()
    {
        var service = new FakeHistoryService();
        service.Records.Add(Record("new query"));
        var vm = CreateViewModel(service, pageSize: 10, debounce: TimeSpan.FromMilliseconds(35));

        vm.SearchText = "old";
        vm.SearchText = "new";
        Assert.Empty(service.Queries);
        await vm.WaitForSearchAsync();

        Assert.Single(service.Queries);
        Assert.Equal("new", service.Queries[0].SearchText);
        Assert.Equal("new query", Assert.Single(vm.Items).Record.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(160), HistoryViewModel.SearchDebounce);
    }

    [Fact]
    public async Task Replacing_a_slow_query_cancels_it_and_ignores_its_late_result()
    {
        var service = new FakeHistoryService();
        service.BlockSearchText = "slow";
        var vm = CreateViewModel(service, pageSize: 10, debounce: TimeSpan.FromMilliseconds(5));

        vm.SearchText = "slow";
        await service.BlockedQueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.SearchText = "fast";
        await vm.WaitForSearchAsync();

        Assert.Contains("slow", service.CanceledQueries);
        Assert.Equal("fast", service.Queries[^1].SearchText);
        Assert.All(vm.Items, item => Assert.DoesNotContain("slow", item.Record.Text ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Kind_filter_and_favorites_page_are_applied_to_repository_queries()
    {
        var service = new FakeHistoryService();
        var vm = CreateViewModel(service, pageSize: 10);

        await vm.SetKindFilterAsync(HistoryKindFilter.Image);
        await vm.SetFavoritesOnlyAsync(true);

        Assert.Equal(ClipboardManager.Core.History.HistoryKindFilter.Image, service.Queries[^2].Kind);
        Assert.True(service.Queries[^1].FavoritesOnly);
        Assert.Equal(ClipboardManager.Core.History.HistoryKindFilter.Image, service.Queries[^1].Kind);
    }

    [Fact]
    public async Task Deleting_selected_record_selects_the_next_available_record()
    {
        var service = new FakeHistoryService();
        service.Records.AddRange([Record("first"), Record("middle"), Record("last")]);
        var vm = CreateViewModel(service, pageSize: 10);
        await vm.LoadInitialAsync();
        vm.SelectedItem = vm.Items[1];

        await vm.DeleteSelectedAsync();

        Assert.Equal(["first", "last"], vm.Items.Select(item => item.Record.Text));
        Assert.Equal("last", vm.SelectedItem?.Record.Text);
        Assert.DoesNotContain(service.Records[1].Id, service.DeletedIds);
    }

    [Fact]
    public async Task Deleting_last_item_on_page_falls_back_to_previous_record()
    {
        var service = new FakeHistoryService();
        service.Records.AddRange([Record("first"), Record("middle")]);
        var vm = CreateViewModel(service, pageSize: 10);
        await vm.LoadInitialAsync();
        vm.SelectedItem = vm.Items[1];

        await vm.DeleteSelectedAsync();

        Assert.Equal("first", vm.SelectedItem?.Record.Text);
    }

    [Fact]
    public async Task Search_failure_sets_error_state_and_retry_recovers()
    {
        var service = new FakeHistoryService { FailSearchesRemaining = 1 };
        service.Records.Add(Record("recovered"));
        var vm = CreateViewModel(service, pageSize: 10);

        await vm.LoadInitialAsync();
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
        Assert.False(vm.IsLoading);

        await vm.RetryAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.Equal("recovered", Assert.Single(vm.Items).Record.Text);
    }

    [Fact]
    public async Task Empty_result_is_a_successful_state_and_not_an_error()
    {
        var vm = CreateViewModel(new FakeHistoryService(), pageSize: 10);

        await vm.LoadInitialAsync();

        Assert.Empty(vm.Items);
        Assert.False(vm.HasMore);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Toggling_favorite_updates_the_row_and_removes_unfavorited_item_from_favorites()
    {
        var favorite = Record("saved", favorite: true);
        var service = new FakeHistoryService();
        service.Records.Add(favorite);
        var vm = CreateViewModel(service, pageSize: 10);
        await vm.SetFavoritesOnlyAsync(true);
        RecordItemViewModel item = Assert.Single(vm.Items);
        vm.SelectedItem = item;

        await vm.ToggleFavoriteAsync(item);

        Assert.Empty(vm.Items);
        Assert.Null(vm.SelectedItem);
        Assert.False(item.IsFavorite);
    }

    [Fact]
    public async Task CopySelected_writes_text_or_safe_image_path_according_to_record_kind()
    {
        var service = new FakeHistoryService();
        Guid imageId = Guid.NewGuid();
        var image = new ClipboardRecord(imageId, ClipboardContentKind.Image, null, $"{imageId:N}.png", 12, 3, 4,
            new string('0', 64), Now, false);
        service.Records.AddRange([Record("copy me"), image]);
        var store = new FakeImageStore();
        var writer = new FakeClipboardWriter();
        var vm = new HistoryViewModel(service, store, writer, pageSize: 10);
        int copyCompleted = 0;
        vm.CopyCompleted += (_, _) => copyCompleted++;
        await vm.LoadInitialAsync();

        vm.SelectedItem = vm.Items[0];
        await vm.CopySelectedAsync();
        vm.SelectedItem = vm.Items[1];
        await vm.CopySelectedAsync();

        Assert.Equal("copy me", writer.WrittenText);
        Assert.Equal(Path.Combine(Path.GetTempPath(), $"{image.Id:N}.png"), writer.WrittenImagePath);
        Assert.Equal(2, copyCompleted);
    }
    [Fact]
    public async Task Clear_summary_counts_records_favorites_and_images_before_confirmed_clear()
    {
        var service = new FakeHistoryService();
        service.Records.AddRange([
            Record("text favorite", favorite: true),
            Record("text"),
            new ClipboardRecord(Guid.NewGuid(), ClipboardContentKind.Image, null, null, 20, 2, 2, new string('0', 64), Now, true)
        ]);
        var vm = CreateViewModel(service, pageSize: 2);

        HistoryClearSummary summary = await vm.GetClearSummaryAsync();

        Assert.Equal(new HistoryClearSummary(3, 2, 1), summary);
        await vm.ClearAllAsync();
        Assert.Empty(vm.Items);
        Assert.Empty(service.Records);
        Assert.Null(vm.SelectedItem);
    }
    [Fact]
    public async Task Refresh_reloads_the_current_query_after_background_capture_changes_history()
    {
        var service = new FakeHistoryService();
        service.Records.Add(Record("before"));
        var vm = CreateViewModel(service, pageSize: 10, debounce: TimeSpan.Zero);
        await vm.LoadInitialAsync();
        vm.SearchText = "after";
        await vm.WaitForSearchAsync();
        service.Records.Add(Record("after capture"));

        await vm.RefreshAsync();

        Assert.Equal("after", service.Queries[^1].SearchText);
        Assert.Single(vm.Items);
        Assert.Equal("after capture", vm.Items[0].Record.Text);
    }
    private static HistoryViewModel CreateViewModel(FakeHistoryService service, int pageSize, TimeSpan? debounce = null)
        => new(service, new FakeImageStore(), new FakeClipboardWriter(), pageSize, debounce ?? TimeSpan.Zero);

    private static ClipboardRecord Record(string text, bool favorite = false)
        => new(Guid.NewGuid(), ClipboardContentKind.Text, text, null, text.Length * 2L, 0, 0,
            Guid.NewGuid().ToString("N").PadRight(64, '0')[..64], Now, favorite);

    private sealed class FakeHistoryService : IHistoryService
    {
        public List<ClipboardRecord> Records { get; } = [];
        public List<HistoryQuery> Queries { get; } = [];
        public List<Guid> DeletedIds { get; } = [];
        public List<string> CanceledQueries { get; } = [];
        public TaskCompletionSource BlockedQueryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? BlockSearchText { get; set; }
        public int FailSearchesRemaining { get; set; }

        public async Task<HistoryPage> SearchAsync(HistoryQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            if (query.SearchText == BlockSearchText)
            {
                BlockedQueryStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { CanceledQueries.Add(query.SearchText); CancellationObserved.TrySetResult(); throw; }
            }
            if (FailSearchesRemaining > 0) { FailSearchesRemaining--; throw new IOException("synthetic query failure"); }
            IEnumerable<ClipboardRecord> records = Records;
            if (query.FavoritesOnly) records = records.Where(record => record.IsFavorite);
            if (query.Kind == HistoryKindFilter.Text) records = records.Where(record => record.Kind == ClipboardContentKind.Text);
            if (query.Kind == HistoryKindFilter.Image) records = records.Where(record => record.Kind == ClipboardContentKind.Image);
            if (!string.IsNullOrEmpty(query.SearchText)) records = records.Where(record => record.Text?.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase) == true);
            ClipboardRecord[] matching = records.ToArray();
            return new HistoryPage(matching.Skip(query.Offset).Take(query.Limit).ToArray(), matching.Length, query.Offset, query.Limit);
        }

        public Task<HistoryChangeResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            DeletedIds.Add(id);
            ClipboardRecord? record = Records.FirstOrDefault(item => item.Id == id);
            if (record is not null) Records.Remove(record);
            return Task.FromResult(new HistoryChangeResult([], [id], null));
        }

        public Task<HistoryChangeResult> SetFavoriteAsync(Guid id, bool isFavorite, CancellationToken cancellationToken)
        {
            int index = Records.FindIndex(item => item.Id == id);
            if (index < 0) return Task.FromResult(new HistoryChangeResult([], [], null));
            Records[index] = Records[index] with { IsFavorite = isFavorite };
            return Task.FromResult(new HistoryChangeResult([Records[index]], [], null));
        }

        public Task<HistoryChangeResult> AddAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HistoryChangeResult> ClearAsync(CancellationToken cancellationToken)
        {
            Guid[] removed = Records.Select(record => record.Id).ToArray();
            Records.Clear();
            return Task.FromResult(new HistoryChangeResult([], removed, null));
        }
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HistoryChangeResult> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeImageStore : IImageStore
    {
        public Task SavePngAsync(Guid id, ReadOnlyMemory<byte> png, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public string GetPath(string fileName) => Path.Combine(Path.GetTempPath(), fileName);
    }

    private sealed class FakeClipboardWriter : IClipboardWriter
    {
        public string? WrittenText { get; private set; }
        public string? WrittenImagePath { get; private set; }
        public Task WriteTextAsync(string text, CancellationToken cancellationToken) { WrittenText = text; return Task.CompletedTask; }
        public Task WriteImageAsync(string imagePath, CancellationToken cancellationToken) { WrittenImagePath = imagePath; return Task.CompletedTask; }
    }
}
