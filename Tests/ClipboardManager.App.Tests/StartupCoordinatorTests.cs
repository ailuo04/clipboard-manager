using ClipboardManager.App.Services;
using ClipboardManager.Core.Settings;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class StartupCoordinatorTests
{
    [Fact]
    public async Task StartAsync_runs_recovery_storage_settings_platform_window_monitor_and_delayed_check_in_order()
    {
        var runtime = new FakeStartupRuntime();
        var coordinator = new AppStartupCoordinator(runtime, new FakeUpdateRecoveryService(runtime.Events));

        AppStartupResult result = await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(AppStartupResult.Started, result);
        Assert.Equal(
        [
            "single-instance", "update-recovery", "data-directories", "sqlite-and-legacy-migration",
            "settings-and-theme", "platform-services", "window", "window-loaded", "clipboard-monitor-start-attempt",
            "clipboard-monitor", "delayed-update-check", "update-health-ready"
        ], runtime.Events);
        Assert.Equal(new AppSettings(500, 512, false, true, false, true, "Light"), runtime.ReceivedSettings);
    }

    [Fact]
    public async Task StartAsync_does_not_mark_update_healthy_until_the_main_window_is_loaded()
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeStartupRuntime { MainWindowLoaded = loaded };
        var coordinator = new AppStartupCoordinator(runtime, new FakeUpdateRecoveryService(runtime.Events));

        Task<AppStartupResult> startup = coordinator.StartAsync(CancellationToken.None);

        Assert.False(startup.IsCompleted);
        Assert.DoesNotContain("update-health-ready", runtime.Events);
        loaded.SetResult();
        Assert.Equal(AppStartupResult.Started, await startup);
        Assert.True(runtime.Events.IndexOf("window-loaded") < runtime.Events.IndexOf("update-health-ready"));
    }
    [Fact]
    public async Task StartAsync_does_not_recover_or_start_services_when_another_instance_owns_the_lock()
    {
        var runtime = new FakeStartupRuntime { AlreadyRunning = true };
        var coordinator = new AppStartupCoordinator(runtime, new FakeUpdateRecoveryService(runtime.Events));

        AppStartupResult result = await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(AppStartupResult.AlreadyRunning, result);
        Assert.Equal(["single-instance"], runtime.Events);
    }

    [Fact]
    public async Task StartAsync_never_starts_platform_or_clipboard_when_schema_or_legacy_migration_fails()
    {
        var runtime = new FakeStartupRuntime { StorageFailure = new InvalidDataException("synthetic corrupt database") };
        var coordinator = new AppStartupCoordinator(runtime, new FakeUpdateRecoveryService(runtime.Events));

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.StartAsync(CancellationToken.None));

        Assert.Equal(["single-instance", "update-recovery", "data-directories", "sqlite-and-legacy-migration", "release-instance"], runtime.Events);
        Assert.DoesNotContain("clipboard-monitor-start-attempt", runtime.Events);
        Assert.DoesNotContain("window", runtime.Events);
    }

    [Fact]
    public async Task StartAsync_stops_platform_services_when_window_creation_fails()
    {
        var runtime = new FakeStartupRuntime { WindowFailure = new InvalidOperationException("synthetic window error") };
        var coordinator = new AppStartupCoordinator(runtime, new FakeUpdateRecoveryService(runtime.Events));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(CancellationToken.None));

        Assert.Contains("stop-platform-services", runtime.Events);
        Assert.Equal("release-instance", runtime.Events[^1]);
        Assert.DoesNotContain("clipboard-monitor-start-attempt", runtime.Events);
        Assert.DoesNotContain("delayed-update-check", runtime.Events);
    }

    [Fact]
    public async Task StartAsync_rolls_back_a_clipboard_monitor_that_throws_during_start()
    {
        var runtime = new FakeStartupRuntime { MonitorStartFailure = new InvalidOperationException("synthetic partial listener failure") };
        var coordinator = new AppStartupCoordinator(runtime, new FakeUpdateRecoveryService(runtime.Events));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(CancellationToken.None));

        Assert.Contains("clipboard-monitor-start-attempt", runtime.Events);
        Assert.Contains("stop-clipboard-monitor", runtime.Events);
        Assert.Contains("stop-platform-services", runtime.Events);
        Assert.Equal("release-instance", runtime.Events[^1]);
    }

    [Fact]
    public async Task Stop_releases_platform_services_and_instance_even_when_monitor_cleanup_fails()
    {
        var runtime = new FakeStartupRuntime { ClipboardStopFailure = new InvalidOperationException("synthetic stop failure") };
        var coordinator = new AppStartupCoordinator(runtime, new FakeUpdateRecoveryService(runtime.Events));
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => coordinator.Stop());

        Assert.Contains("stop-clipboard-monitor", runtime.Events);
        Assert.Contains("stop-platform-services", runtime.Events);
        Assert.Equal("release-instance", runtime.Events[^1]);
    }

    [Fact]
    public async Task Stop_releases_the_instance_even_when_platform_cleanup_fails()
    {
        var runtime = new FakeStartupRuntime { PlatformStopFailure = new InvalidOperationException("synthetic platform cleanup failure") };
        var coordinator = new AppStartupCoordinator(runtime, new FakeUpdateRecoveryService(runtime.Events));
        await coordinator.StartAsync(CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => coordinator.Stop());

        Assert.Contains("stop-clipboard-monitor", runtime.Events);
        Assert.Contains("stop-platform-services", runtime.Events);
        Assert.Equal("release-instance", runtime.Events[^1]);
    }
    private sealed class FakeUpdateRecoveryService(List<string> events) : IUpdateRecoveryService
    {
        public void SetLaunchArguments(string? arguments) { }
        public Task RecoverIfNeededAsync(CancellationToken token)
        {
            events.Add("update-recovery");
            return Task.CompletedTask;
        }
        public Task MarkStartupHealthyAsync(CancellationToken token)
        {
            events.Add("update-health-ready");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStartupRuntime : IAppStartupRuntime
    {
        public List<string> Events { get; } = [];
        public bool AlreadyRunning { get; init; }
        public Exception? StorageFailure { get; init; }
        public Exception? WindowFailure { get; init; }
        public Exception? MonitorStartFailure { get; init; }
        public Exception? ClipboardStopFailure { get; init; }
        public Exception? PlatformStopFailure { get; init; }
        public AppSettings? ReceivedSettings { get; private set; }
        public TaskCompletionSource MainWindowLoaded { get; init; } = CompletedWindowLoad();

        public bool TryAcquireSingleInstance() { Events.Add("single-instance"); return !AlreadyRunning; }
        public void ReleaseSingleInstance() => Events.Add("release-instance");
        public Task EnsureDataDirectoriesAsync(CancellationToken token) { Events.Add("data-directories"); return Task.CompletedTask; }
        public Task InitializeStorageAndMigrateLegacyAsync(CancellationToken token)
        {
            Events.Add("sqlite-and-legacy-migration");
            return StorageFailure is null ? Task.CompletedTask : Task.FromException(StorageFailure);
        }
        public Task<AppSettings> LoadSettingsAndApplyThemeAsync(CancellationToken token)
        {
            Events.Add("settings-and-theme");
            return Task.FromResult(new AppSettings(500, 512, false, true, false, true, "Light"));
        }
        public Task StartPlatformServicesAsync(AppSettings settings, CancellationToken token)
        {
            Events.Add("platform-services");
            ReceivedSettings = settings;
            return Task.CompletedTask;
        }
        public Task ShowMainWindowAsync(CancellationToken token)
        {
            Events.Add("window");
            return WindowFailure is null ? Task.CompletedTask : Task.FromException(WindowFailure);
        }
        public async Task WaitForMainWindowLoadedAsync(CancellationToken token)
        {
            Events.Add("window-loaded");
            await MainWindowLoaded.Task.WaitAsync(token);
        }

        private static TaskCompletionSource CompletedWindowLoad()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetResult();
            return completion;
        }
        public void StartClipboardMonitor()
        {
            Events.Add("clipboard-monitor-start-attempt");
            if (MonitorStartFailure is not null) throw MonitorStartFailure;
            Events.Add("clipboard-monitor");
        }
        public void ScheduleDelayedUpdateCheck() => Events.Add("delayed-update-check");
        public void StopClipboardMonitor()
        {
            Events.Add("stop-clipboard-monitor");
            if (ClipboardStopFailure is not null) throw ClipboardStopFailure;
        }
        public void StopPlatformServices()
        {
            Events.Add("stop-platform-services");
            if (PlatformStopFailure is not null) throw PlatformStopFailure;
        }
    }
}


