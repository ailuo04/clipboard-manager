using ClipboardManager.App.Services;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class CaptureCoordinatorTests
{
    [Fact]
    public void Production_debounce_is_exactly_100_milliseconds()
        => Assert.Equal(TimeSpan.FromMilliseconds(100), ClipboardCaptureCoordinator.DebounceInterval);

    [Fact]
    public async Task Repeated_system_notifications_for_the_same_sequence_are_only_added_once()
    {
        using var fixture = new Fixture(debounce: TimeSpan.FromMilliseconds(1));
        fixture.Reader.Enqueue(new ClipboardReadResult(41, TextSnapshot("synthetic one"), null));
        fixture.Reader.Enqueue(new ClipboardReadResult(41, TextSnapshot("synthetic one"), null));
        await fixture.Coordinator.StartAsync();

        fixture.Monitor.RaiseChanged();
        await fixture.History.WaitForAddCountAsync(1);
        fixture.Monitor.RaiseChanged();
        await fixture.Reader.WaitForReadCountAsync(2);

        Assert.Equal(2, fixture.Reader.ReadCount);
        Assert.Equal(1, fixture.History.AddCount);
    }

    [Fact]
    public async Task Pause_clears_pending_capture_and_resume_does_not_read_the_old_clipboard_state()
    {
        using var fixture = new Fixture(debounce: TimeSpan.FromMilliseconds(100));
        fixture.Reader.Enqueue(new ClipboardReadResult(7, TextSnapshot("must not be captured"), null));
        await fixture.Coordinator.StartAsync();
        fixture.Monitor.RaiseChanged();

        await fixture.Coordinator.PauseAsync();
        await fixture.Coordinator.ResumeAsync();
        await Task.Delay(160);

        Assert.True(fixture.Coordinator.IsPaused is false);
        Assert.Equal(0, fixture.Reader.ReadCount);
        Assert.Equal(0, fixture.History.AddCount);
        Assert.Equal(2, fixture.Monitor.StartCount);
        Assert.Equal(1, fixture.Monitor.StopCount);
    }

    [Fact]
    public async Task Read_and_history_add_are_serialized_and_keep_the_required_order()
    {
        using var fixture = new Fixture(debounce: TimeSpan.FromMilliseconds(1));
        fixture.Reader.Enqueue(new ClipboardReadResult(1, TextSnapshot("first"), null));
        fixture.Reader.Enqueue(new ClipboardReadResult(2, TextSnapshot("second"), null));
        await fixture.Coordinator.StartAsync();

        fixture.Monitor.RaiseChanged();
        await fixture.History.WaitForAddCountAsync(1);
        fixture.Monitor.RaiseChanged();
        await fixture.History.WaitForAddCountAsync(2);

        Assert.Equal(["monitor-start", "read:1", "add:1", "read:2", "add:2"], fixture.Events);
        Assert.Equal(1, fixture.History.MaximumConcurrentAdds);
    }

    [Fact]
    public async Task Stop_cancels_debounce_and_waits_for_in_flight_reader_before_returning()
    {
        using var fixture = new Fixture(debounce: TimeSpan.FromMilliseconds(1));
        fixture.Reader.BlockReads = true;
        fixture.Reader.Enqueue(new ClipboardReadResult(9, TextSnapshot("synthetic"), null));
        await fixture.Coordinator.StartAsync();
        fixture.Monitor.RaiseChanged();
        await fixture.Reader.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.Coordinator.StopAsync();

        Assert.True(fixture.Reader.ReadCancellationObserved);
        Assert.Equal(0, fixture.History.AddCount);
        Assert.False(fixture.Coordinator.IsRunning);
    }

    [Fact]
    public async Task Reader_exception_diagnostics_do_not_contain_exception_message_or_clipboard_text()
    {
        using var fixture = new Fixture(debounce: TimeSpan.FromMilliseconds(1));
        fixture.Reader.Failure = new InvalidOperationException("synthetic-private-clipboard-payload");
        await fixture.Coordinator.StartAsync();
        fixture.Monitor.RaiseChanged();
        await fixture.Reader.WaitForReadCountAsync(1);
        await WaitForFileAsync(fixture.LogPath);
        await fixture.Coordinator.StopAsync();

        string log = await File.ReadAllTextAsync(fixture.LogPath);
        Assert.DoesNotContain("synthetic-private-clipboard-payload", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synchronous_disposal_stops_the_listener_and_waits_for_in_flight_capture()
    {
        using var fixture = new Fixture(debounce: TimeSpan.FromMilliseconds(1));
        fixture.Reader.BlockReads = true;
        fixture.Reader.Enqueue(new ClipboardReadResult(11, TextSnapshot("synthetic"), null));
        await fixture.Coordinator.StartAsync();
        fixture.Monitor.RaiseChanged();
        await fixture.Reader.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsAssignableFrom<IDisposable>(fixture.Coordinator).Dispose();

        Assert.True(fixture.Reader.ReadCancellationObserved);
        Assert.Equal(1, fixture.Monitor.StopCount);
        Assert.False(fixture.Coordinator.IsRunning);
    }
    [Fact]
    public async Task History_failure_is_observed_and_diagnostics_omit_record_content()
    {
        using var fixture = new Fixture(debounce: TimeSpan.FromMilliseconds(1));
        const string privatePayload = "synthetic-private-record-content";
        fixture.Reader.Enqueue(new ClipboardReadResult(31, TextSnapshot(privatePayload), null));
        fixture.History.Failure = new IOException(privatePayload);
        await fixture.Coordinator.StartAsync();
        fixture.Monitor.RaiseChanged();
        await fixture.History.WaitForAddCountAsync(1);
        await WaitForFileAsync(fixture.LogPath);
        await fixture.Coordinator.StopAsync();

        string log = await File.ReadAllTextAsync(fixture.LogPath);
        Assert.DoesNotContain(privatePayload, log, StringComparison.Ordinal);
        Assert.Equal(1, fixture.History.MaximumConcurrentAdds);
    }
    [Fact]
    public async Task Successful_capture_notifies_the_shell_only_after_history_persistence()
    {
        using var fixture = new Fixture(debounce: TimeSpan.FromMilliseconds(1));
        fixture.History.ReturnMutation = true;
        fixture.Reader.Enqueue(new ClipboardReadResult(77, TextSnapshot("synthetic visible record"), null));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Coordinator.HistoryChanged += (_, _) =>
        {
            fixture.Events.Add("history-changed");
            changed.TrySetResult();
        };
        await fixture.Coordinator.StartAsync();
        fixture.Monitor.RaiseChanged();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["monitor-start", "read:1", "add:1", "history-changed"], fixture.Events);
    }
    private static ClipboardSnapshot TextSnapshot(string text)
        => new(ClipboardContentKind.Text, text, ReadOnlyMemory<byte>.Empty, DateTime.UtcNow);

    private static async Task WaitForFileAsync(string path)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!File.Exists(path) && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(File.Exists(path));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cm-capture-test-{Guid.NewGuid():N}");

        public Fixture(TimeSpan debounce)
        {
            Directory.CreateDirectory(_root);
            LogPath = Path.Combine(_root, "app.log");
            Monitor = new FakeMonitor(Events);
            Reader = new FakeReader(Events);
            History = new FakeHistoryService(Events);
            Coordinator = new ClipboardCaptureCoordinator(Monitor, Reader, History,
                new AppLogger(LogPath), debounce);
        }

        public List<string> Events { get; } = [];
        public string LogPath { get; }
        public FakeMonitor Monitor { get; }
        public FakeReader Reader { get; }
        public FakeHistoryService History { get; }
        public ClipboardCaptureCoordinator Coordinator { get; }

        public void Dispose()
        {
            try { Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { }
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }
    }

    private sealed class FakeMonitor(List<string> events) : IClipboardMonitor
    {
        public event EventHandler? ClipboardChanged;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public void Start() { StartCount++; events.Add("monitor-start"); }
        public void Stop() { StopCount++; events.Add("monitor-stop"); }
        public void RaiseChanged() => ClipboardChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }

    private sealed class FakeReader(List<string> events) : IClipboardReader
    {
        private readonly Queue<ClipboardReadResult> _results = new();
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public Exception? Failure { get; set; }
        public bool BlockReads { get; set; }
        public bool ReadCancellationObserved { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Enqueue(ClipboardReadResult result) => _results.Enqueue(result);

        public async Task<ClipboardReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            int count = Interlocked.Increment(ref _readCount);
            events.Add($"read:{count}");
            ReadStarted.TrySetResult();
            if (BlockReads)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ReadCancellationObserved = true;
                    throw;
                }
            }
            if (Failure is not null) throw Failure;
            lock (_results)
            {
                if (_results.Count == 0) return new ClipboardReadResult((uint)count, null, null);
                return _results.Dequeue();
            }
        }

        public async Task WaitForReadCountAsync(int count)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (ReadCount < count && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.True(ReadCount >= count, $"Expected {count} reads, observed {ReadCount}.");
        }
    }

    private sealed class FakeHistoryService(List<string> events) : IHistoryService
    {
        private int _activeAdds;
        private int _addCount;
        public int AddCount => Volatile.Read(ref _addCount);
        public int MaximumConcurrentAdds { get; private set; }
        public Exception? Failure { get; set; }
        public bool ReturnMutation { get; set; }
        private readonly TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HistoryPage> SearchAsync(HistoryQuery query, CancellationToken cancellationToken)
            => Task.FromResult(new HistoryPage([], 0, query.Offset, query.Limit));

        public async Task<HistoryChangeResult> AddAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _activeAdds);
            MaximumConcurrentAdds = Math.Max(MaximumConcurrentAdds, active);
            int count = Interlocked.Increment(ref _addCount);
            events.Add($"add:{count}");
            try
            {
                if (Failure is not null) throw Failure;
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                _changed.TrySetResult();
                ClipboardRecord? record = ReturnMutation
                    ? new ClipboardRecord(Guid.NewGuid(), snapshot.Kind, snapshot.Text, null, 1, 0, 0,
                        new string('0', 64), snapshot.CapturedAtUtc, false)
                    : null;
                return new HistoryChangeResult(record is null ? [] : [record], [], null);
            }
            finally { Interlocked.Decrement(ref _activeAdds); }
        }

        public Task<HistoryChangeResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(new HistoryChangeResult([], [], null));
        public Task<HistoryChangeResult> ClearAsync(CancellationToken cancellationToken)
            => Task.FromResult(new HistoryChangeResult([], [], null));
        public Task<HistoryChangeResult> SetFavoriteAsync(Guid id, bool isFavorite, CancellationToken cancellationToken)
            => Task.FromResult(new HistoryChangeResult([], [], null));
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AppSettings(500, 512, false, true, false, true, "System"));
        public Task<HistoryChangeResult> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new HistoryChangeResult([], [], null));

        public async Task WaitForAddCountAsync(int count)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (AddCount < count && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.True(AddCount >= count, $"Expected {count} additions, observed {AddCount}.");
        }
    }
}
