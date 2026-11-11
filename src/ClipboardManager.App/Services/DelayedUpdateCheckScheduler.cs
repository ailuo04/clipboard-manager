namespace ClipboardManager.App.Services;

public interface IUpdateCheckScheduler
{
    void SetEnabled(bool enabled);
    void Schedule();
}

/// <summary>Starts the first automatic check after startup and repeats at the configured interval while enabled.</summary>
public sealed class DelayedUpdateCheckScheduler : IUpdateCheckScheduler, IDisposable
{
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(60);
    private readonly Func<CancellationToken, Task>? _check;
    private readonly AppLogger _logger;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _activeSchedule;
    private bool _enabled = true;
    private bool _scheduleRequested;
    private bool _disposed;

    public DelayedUpdateCheckScheduler(Func<CancellationToken, Task>? check, AppLogger logger,
        TimeSpan? initialDelay = null, TimeSpan? interval = null)
    {
        _check = check;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _initialDelay = initialDelay ?? DefaultInitialDelay;
        _interval = interval ?? DefaultInterval;
        if (_initialDelay < TimeSpan.Zero || _interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialDelay), "Update-check delays must be non-negative and intervals must be positive.");
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || _enabled == enabled) return;
            _enabled = enabled;
            if (!enabled) _activeSchedule?.Cancel();
            else StartIfNeededLocked();
        }
    }

    public void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _scheduleRequested = true;
            StartIfNeededLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _activeSchedule?.Cancel();
            _lifetime.Cancel();
        }
    }

    private void StartIfNeededLocked()
    {
        if (!_enabled || !_scheduleRequested || _check is null || _activeSchedule is not null) return;
        _activeSchedule = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationTokenSource schedule = _activeSchedule;
        _ = RunAsync(schedule);
    }

    private async Task RunAsync(CancellationTokenSource schedule)
    {
        CancellationToken token = schedule.Token;
        try
        {
            await Task.Delay(_initialDelay, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try { await _check!(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    try { _logger.LogException(AppLogOperation.UpdateCheck, AppLogErrorCategory.Unknown, exception); } catch { }
                }
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeSchedule, schedule)) _activeSchedule = null;
                schedule.Dispose();
                if (!_disposed && _enabled && _scheduleRequested) StartIfNeededLocked();
            }
        }
    }
}



