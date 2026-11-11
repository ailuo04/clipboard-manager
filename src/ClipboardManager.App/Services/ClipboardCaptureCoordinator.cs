using ClipboardManager.App.Utilities;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;

namespace ClipboardManager.App.Services;

/// <summary>Debounces Windows clipboard notifications and serializes local read-to-history capture.</summary>
public sealed class ClipboardCaptureCoordinator : IDisposable, IAsyncDisposable
{
    public static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly IClipboardMonitor _monitor;
    private readonly IClipboardReader _reader;
    private readonly IHistoryService _history;
    private readonly AppLogger _logger;
    private readonly TimeSpan _debounceInterval;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly HashSet<Task> _operations = [];
    private CancellationTokenSource? _generationLifetime;
    private CancellationTokenSource? _pendingDebounce;
    private uint? _lastSequenceNumber;
    private bool _running;
    private bool _paused;
    private bool _disposed;

    public ClipboardCaptureCoordinator(IClipboardMonitor monitor, IClipboardReader reader,
        IHistoryService history, AppLogger logger, TimeSpan? debounceInterval = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _debounceInterval = debounceInterval ?? DebounceInterval;
        if (_debounceInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounceInterval));
    }

    public event EventHandler? HistoryChanged;

    public bool IsRunning { get { lock (_gate) return _running; } }
    public bool IsPaused { get { lock (_gate) return _paused; } }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running || _paused) return;
            _generationLifetime = new CancellationTokenSource();
            _running = true;
            _monitor.ClipboardChanged += OnClipboardChanged;
        }

        try { _monitor.Start(); }
        catch
        {
            CancellationTokenSource? generation;
            Task[] operations;
            lock (_gate)
            {
                _running = false;
                _monitor.ClipboardChanged -= OnClipboardChanged;
                generation = CancelGenerationLocked();
                operations = _operations.ToArray();
            }
            try { _monitor.Stop(); }
            catch (Exception cleanupException) { Log(AppLogOperation.ClipboardCapture, AppLogErrorCategory.NativeClipboard, cleanupException); }
            try { await WaitForOperationsAsync(operations).ConfigureAwait(false); }
            finally { generation?.Dispose(); }
            throw;
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task[] operations;
        bool stopMonitor;
        CancellationTokenSource? generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_paused) return;
            _paused = true;
            stopMonitor = _running;
            _running = false;
            _monitor.ClipboardChanged -= OnClipboardChanged;
            generation = CancelGenerationLocked();
            operations = _operations.ToArray();
        }
        Exception? stopFailure = null;
        if (stopMonitor)
        {
            try { _monitor.Stop(); }
            catch (Exception exception) { stopFailure = exception; }
        }
        try { await WaitForOperationsAsync(operations).ConfigureAwait(false); }
        finally { generation?.Dispose(); }
        if (stopFailure is not null) throw stopFailure;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_paused) return _running ? Task.CompletedTask : StartAsync(cancellationToken);
            _paused = false;
        }
        return StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task[] operations;
        bool stopMonitor;
        CancellationTokenSource? generation;
        lock (_gate)
        {
            stopMonitor = _running;
            _running = false;
            _paused = false;
            _monitor.ClipboardChanged -= OnClipboardChanged;
            generation = CancelGenerationLocked();
            operations = _operations.ToArray();
        }

        Exception? stopFailure = null;
        if (stopMonitor)
        {
            try { _monitor.Stop(); }
            catch (Exception exception) { stopFailure = exception; }
        }
        try { await WaitForOperationsAsync(operations).ConfigureAwait(false); }
        finally { generation?.Dispose(); }
        if (stopFailure is not null) throw stopFailure;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            _monitor.ClipboardChanged -= OnClipboardChanged;
            _captureGate.Dispose();
        }
    }

    private void OnClipboardChanged(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (!_running || _paused || _disposed || _generationLifetime is null) return;
            _pendingDebounce?.Cancel();
            CancellationTokenSource debounce = CancellationTokenSource.CreateLinkedTokenSource(_generationLifetime.Token);
            _pendingDebounce = debounce;
            CancellationToken generationToken = _generationLifetime.Token;
            Task operation = Task.Run(() => CaptureAfterDebounceAsync(debounce, generationToken));
            _operations.Add(operation);
            _ = operation.ContinueWith(_ => RemoveOperation(operation), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task CaptureAfterDebounceAsync(CancellationTokenSource debounce, CancellationToken generationToken)
    {
        try
        {
            await Task.Delay(_debounceInterval, debounce.Token).ConfigureAwait(false);
            await _captureGate.WaitAsync(generationToken).ConfigureAwait(false);
            try
            {
                if (!IsGenerationActive(generationToken)) return;
                ClipboardReadResult result;
                try { result = await _reader.ReadAsync(generationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (generationToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    Log(AppLogOperation.ClipboardRead, AppLogErrorCategory.NativeClipboard, exception);
                    return;
                }

                if (result.Warning is not null)
                    SafeLog(AppLogLevel.Warning, AppLogOperation.ClipboardRead, AppLogErrorCategory.NativeClipboard);
                if (!IsGenerationActive(generationToken) || _lastSequenceNumber == result.SequenceNumber) return;
                _lastSequenceNumber = result.SequenceNumber;
                if (result.Snapshot is null) return;

                try
                {
                    HistoryChangeResult change = await _history.AddAsync(result.Snapshot, generationToken).ConfigureAwait(false);
                    if (change.Upserted.Count > 0 || change.DeletedIds.Count > 0)
                        PublishHistoryChanged();
                }
                catch (OperationCanceledException) when (generationToken.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    Log(AppLogOperation.ClipboardCapture, AppLogErrorCategory.Storage, exception);
                }
            }
            finally { _captureGate.Release(); }
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested || generationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (debounce.IsCancellationRequested || generationToken.IsCancellationRequested) { }
        catch (Exception exception) { Log(AppLogOperation.ClipboardCapture, AppLogErrorCategory.Unknown, exception); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingDebounce, debounce)) _pendingDebounce = null;
            }
            debounce.Dispose();
        }
    }

    private bool IsGenerationActive(CancellationToken generationToken)
    {
        lock (_gate) return _running && !_paused && !_disposed
            && _generationLifetime is { IsCancellationRequested: false }
            && generationToken == _generationLifetime.Token;
    }

    private CancellationTokenSource? CancelGenerationLocked()
    {
        _pendingDebounce?.Cancel();
        _pendingDebounce = null;
        CancellationTokenSource? generation = _generationLifetime;
        _generationLifetime = null;
        generation?.Cancel();
        return generation;
    }

    private async Task WaitForOperationsAsync(Task[] initial)
    {
        if (initial.Length > 0) await Task.WhenAll(initial).ConfigureAwait(false);
        while (true)
        {
            Task[] remaining;
            lock (_gate) remaining = _operations.ToArray();
            if (remaining.Length == 0) return;
            await Task.WhenAll(remaining).ConfigureAwait(false);
        }
    }

    private void RemoveOperation(Task operation)
    {
        lock (_gate) _operations.Remove(operation);
    }

    private void PublishHistoryChanged()
    {
        try { HistoryChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception) { Log(AppLogOperation.ClipboardCapture, AppLogErrorCategory.Unknown, exception); }
    }

    private void Log(AppLogOperation operation, AppLogErrorCategory category, Exception exception)
    {
        try { _logger.LogException(operation, category, exception); }
        catch { /* Clipboard diagnostics must not stop future capture or shutdown. */ }
    }

    private void SafeLog(AppLogLevel level, AppLogOperation operation, AppLogErrorCategory? category)
    {
        try { _logger.Log(level, operation, category); }
        catch { }
    }
}
