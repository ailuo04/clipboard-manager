using ClipboardManager.App.Services;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class DelayedUpdateCheckSchedulerTests
{
    [Fact]
    public void Uses_twenty_second_startup_delay_and_sixty_minute_repeat_interval_by_default()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), DelayedUpdateCheckScheduler.DefaultInitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(60), DelayedUpdateCheckScheduler.DefaultInterval);
    }

    [Fact]
    public async Task Disabled_scheduler_makes_no_check_until_enabled_after_startup_was_scheduled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-scheduler-test-{Guid.NewGuid():N}");
        using var logger = new LoggerFixture(root);
        int calls = 0;
        using var scheduler = new DelayedUpdateCheckScheduler(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, logger.Logger,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        scheduler.SetEnabled(false);
        scheduler.Schedule();
        await Task.Delay(80);
        Assert.Equal(0, Volatile.Read(ref calls));

        scheduler.SetEnabled(true);
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Enabled_scheduler_rechecks_at_the_configured_interval_and_stops_when_disabled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-scheduler-test-{Guid.NewGuid():N}");
        using var logger = new LoggerFixture(root);
        int calls = 0;
        using var scheduler = new DelayedUpdateCheckScheduler(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, logger.Logger,
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20));
        scheduler.Schedule();
        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 2);
        scheduler.SetEnabled(false);
        int stoppedAt = Volatile.Read(ref calls);
        await Task.Delay(60);

        Assert.Equal(stoppedAt, Volatile.Read(ref calls));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class LoggerFixture(string root) : IDisposable
    {
        public AppLogger Logger { get; } = new(Path.Combine(root, "scheduler.log"));
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}


