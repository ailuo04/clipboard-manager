using ClipboardManager.App;
using ClipboardManager.App.Utilities;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Storage.Migration;
using ClipboardManager.Windows.Clipboard;
using ClipboardManager.Windows.Input;
using ClipboardManager.Windows.Theme;
using ClipboardManager.Windows.Tray;

namespace ClipboardManager.App.Services;

public sealed class AppStartupRuntime : IAppStartupRuntime, IDisposable
{
    private const string SingleInstanceMutexName = "Local\\ClipboardManager.Desktop.v1";
    private readonly AppPaths _paths;
    private readonly IHistoryRepository _repository;
    private readonly IHistoryService _history;
    private readonly IAppSettingsEffects _settingsEffects;
    private readonly AppLogger _logger;
    private readonly SystemThemeService _theme;
    private readonly GlobalHotKeyService _hotKey;
    private readonly SystemTrayService _tray;
    private readonly ClipboardCaptureCoordinator _captureCoordinator;
    private readonly IUpdateCheckScheduler _updateCheckScheduler;
    private readonly IAppWindowService _window;
    private Mutex? _instanceMutex;
    private AppSettings? _settings;
    private bool _platformStarted;

    public AppStartupRuntime(AppPaths paths, IHistoryRepository repository, IHistoryService history,
        AppLogger logger, SystemThemeService theme, GlobalHotKeyService hotKey, SystemTrayService tray,
        ClipboardCaptureCoordinator captureCoordinator, IUpdateCheckScheduler updateCheckScheduler,
        IAppSettingsEffects settingsEffects, IAppWindowService window)
    {
        _paths = paths;
        _repository = repository;
        _history = history;
        _settingsEffects = settingsEffects;
        _logger = logger;
        _theme = theme;
        _hotKey = hotKey;
        _tray = tray;
        _captureCoordinator = captureCoordinator;
        _updateCheckScheduler = updateCheckScheduler;
        _window = window;
        _captureCoordinator.HistoryChanged += OnCapturedHistoryChanged;
    }

    public bool TryAcquireSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (createdNew) return true;
        _instanceMutex.Dispose();
        _instanceMutex = null;
        return false;
    }

    public void ReleaseSingleInstance()
    {
        Mutex? mutex = Interlocked.Exchange(ref _instanceMutex, null);
        if (mutex is null) return;
        try { mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        finally { mutex.Dispose(); }
    }

    public Task EnsureDataDirectoriesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _paths.EnsureDirectories();
        return Task.CompletedTask;
    }

    public Task InitializeStorageAndMigrateLegacyAsync(CancellationToken cancellationToken)
    {
        var migration = new LegacyMigrationCoordinator(_paths.LegacyHistoryFilePath, _paths.LegacyThemeFilePath,
            _paths.ImagesDirectory, _paths.MigrationBackupDirectory, _paths.MigrationCompletionPath, _repository);
        return migration.RunAsync(cancellationToken);
    }

    public async Task<AppSettings> LoadSettingsAndApplyThemeAsync(CancellationToken cancellationToken)
    {
        _settings = await _history.GetSettingsAsync(cancellationToken);
        _settingsEffects.Initialize(_settings, _theme.IsLightTheme ?? true);
        return _settings;
    }

    public Task StartPlatformServicesAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settings = settings;
        _platformStarted = true;
        _theme.ThemeChanged += OnThemeChanged;
        _hotKey.Activated += OnHotKeyActivated;
        _tray.OpenRequested += OnTrayOpenRequested;
        _tray.ExitRequested += OnTrayExitRequested;
        _tray.PauseChanged += OnTrayPauseChanged;

        TryStartPlatformService(_theme.Start, AppLogOperation.PlatformService);
        try
        {
            if (!_hotKey.TryStart(out _))
                _logger.Log(AppLogLevel.Warning, AppLogOperation.PlatformService, AppLogErrorCategory.HotKeyConflict);
        }
        catch (Exception exception) { LogPlatformFailure(exception); }
        TryStartPlatformService(() => _tray.Start(settings.MinimizeToTray), AppLogOperation.PlatformService);
        return Task.CompletedTask;
    }

    public Task ShowMainWindowAsync(CancellationToken cancellationToken)
    {
        _window.ApplySettings(_settingsEffects.Current, _theme.IsLightTheme ?? true);
        return _window.ShowMainWindowAsync(cancellationToken);
    }

    public Task WaitForMainWindowLoadedAsync(CancellationToken cancellationToken)
        => _window.WaitUntilLoadedAsync(cancellationToken);

    public void StartClipboardMonitor()
    {
        if (_tray.IsPaused) return;
        try
        {
            _captureCoordinator.StartAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Start may fail after a native listener was partially registered; always attempt rollback.
            try { _captureCoordinator.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception cleanupException) { LogPlatformFailure(cleanupException); }
            throw;
        }
    }

    public void ScheduleDelayedUpdateCheck() => _updateCheckScheduler.Schedule();

    public void StopClipboardMonitor()
    {
        // Stop is idempotent; invoke even after a failed Start so partial native registration is retried.
        try { _captureCoordinator.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception exception) { LogPlatformFailure(exception); }
    }

    public void StopPlatformServices()
    {
        if (!_platformStarted) return;
        _platformStarted = false;
        _theme.ThemeChanged -= OnThemeChanged;
        _hotKey.Activated -= OnHotKeyActivated;
        _tray.OpenRequested -= OnTrayOpenRequested;
        _tray.ExitRequested -= OnTrayExitRequested;
        _tray.PauseChanged -= OnTrayPauseChanged;

        // A failure in one native service must never prevent attempts to release the others.
        TryStopPlatformService(_tray.Stop);
        TryStopPlatformService(_hotKey.Stop);
        TryStopPlatformService(_theme.Stop);
    }

    public void Dispose()
    {
        _captureCoordinator.HistoryChanged -= OnCapturedHistoryChanged;
        try { StopClipboardMonitor(); }
        finally
        {
            try { StopPlatformServices(); }
            finally { ReleaseSingleInstance(); }
        }
    }

    private void TryStartPlatformService(Action start, AppLogOperation operation)
    {
        try { start(); }
        catch (Exception exception) { LogPlatformFailure(exception); }
    }

    private void TryStopPlatformService(Action stop)
    {
        try { stop(); }
        catch (Exception exception) { LogPlatformFailure(exception); }
    }

    private void LogPlatformFailure(Exception exception)
    {
        try { _logger.LogException(AppLogOperation.PlatformService, AppLogErrorCategory.PlatformService, exception); }
        catch { /* Diagnostics must not block release of other native services. */ }
    }

    private void OnThemeChanged(object? sender, bool isLight)
        => _settingsEffects.ApplySystemTheme(isLight);

    private void OnHotKeyActivated(object? sender, EventArgs e)
        => _ = _window.ShowMainWindowAsync(CancellationToken.None);

    private void OnTrayOpenRequested(object? sender, EventArgs e)
        => _ = _window.ShowMainWindowAsync(CancellationToken.None);

    private void OnTrayExitRequested(object? sender, EventArgs e) => _window.RequestExit();

    private void OnCapturedHistoryChanged(object? sender, EventArgs args)
        => _window.NotifyHistoryChanged();

    private async void OnTrayPauseChanged(object? sender, bool isPaused)
    {
        try
        {
            if (isPaused) await _captureCoordinator.PauseAsync();
            else await _captureCoordinator.ResumeAsync();
        }
        catch (Exception exception) { LogPlatformFailure(exception); }
    }
}