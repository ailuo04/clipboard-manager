using ClipboardManager.Core.Settings;

namespace ClipboardManager.App.Services;

public enum AppStartupResult { Started, AlreadyRunning, AlreadyStarted }

public interface IUpdateRecoveryService
{
    Task RecoverIfNeededAsync(CancellationToken cancellationToken);
    Task MarkStartupHealthyAsync(CancellationToken cancellationToken);
}

public interface IAppStartupRuntime
{
    bool TryAcquireSingleInstance();
    void ReleaseSingleInstance();
    Task EnsureDataDirectoriesAsync(CancellationToken cancellationToken);
    Task InitializeStorageAndMigrateLegacyAsync(CancellationToken cancellationToken);
    Task<AppSettings> LoadSettingsAndApplyThemeAsync(CancellationToken cancellationToken);
    Task StartPlatformServicesAsync(AppSettings settings, CancellationToken cancellationToken);
    Task ShowMainWindowAsync(CancellationToken cancellationToken);
    Task WaitForMainWindowLoadedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    void StartClipboardMonitor();
    void ScheduleDelayedUpdateCheck();
    void StopClipboardMonitor();
    void StopPlatformServices();
}

/// <summary>Orders startup so data recovery and storage validation always precede capture or UI activation.</summary>
public sealed class AppStartupCoordinator
{
    private readonly IAppStartupRuntime _runtime;
    private readonly IUpdateRecoveryService _recovery;
    private int _state;

    public AppStartupCoordinator(IAppStartupRuntime runtime, IUpdateRecoveryService recovery)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
    }

    public async Task<AppStartupResult> StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return AppStartupResult.AlreadyStarted;

        bool acquired;
        try { acquired = _runtime.TryAcquireSingleInstance(); }
        catch
        {
            Volatile.Write(ref _state, 0);
            throw;
        }
        if (!acquired)
        {
            Volatile.Write(ref _state, 0);
            return AppStartupResult.AlreadyRunning;
        }

        bool platformStartAttempted = false;
        bool clipboardStartAttempted = false;
        try
        {
            await _recovery.RecoverIfNeededAsync(cancellationToken);
            await _runtime.EnsureDataDirectoriesAsync(cancellationToken);
            await _runtime.InitializeStorageAndMigrateLegacyAsync(cancellationToken);
            AppSettings settings = await _runtime.LoadSettingsAndApplyThemeAsync(cancellationToken);
            platformStartAttempted = true;
            await _runtime.StartPlatformServicesAsync(settings, cancellationToken);
            await _runtime.ShowMainWindowAsync(cancellationToken);
            await _runtime.WaitForMainWindowLoadedAsync(cancellationToken);
            clipboardStartAttempted = true;
            _runtime.StartClipboardMonitor();
            _runtime.ScheduleDelayedUpdateCheck();
            await _recovery.MarkStartupHealthyAsync(cancellationToken);
            Volatile.Write(ref _state, 2);
            return AppStartupResult.Started;
        }
        catch
        {
            try { if (clipboardStartAttempted) _runtime.StopClipboardMonitor(); } catch { }
            try { if (platformStartAttempted) _runtime.StopPlatformServices(); } catch { }
            try { _runtime.ReleaseSingleInstance(); }
            finally { Volatile.Write(ref _state, 0); }
            throw;
        }
    }

    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _state, 3, 2) != 2) return;
        try { _runtime.StopClipboardMonitor(); }
        finally
        {
            try { _runtime.StopPlatformServices(); }
            finally { _runtime.ReleaseSingleInstance(); }
        }
    }
}

public sealed class NoOpUpdateRecoveryService : IUpdateRecoveryService
{
    public Task RecoverIfNeededAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
    public Task MarkStartupHealthyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}


