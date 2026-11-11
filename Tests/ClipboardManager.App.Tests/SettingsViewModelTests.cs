using ClipboardManager.App.Services;
using ClipboardManager.App.ViewModels;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void Theme_changes_preview_immediately_and_cancel_restores_the_persisted_theme()
    {
        using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();

        viewModel.ThemePreference = "Dark";
        Assert.Equal("Dark", fixture.Effects.PreviewedThemes[^1]);
        Assert.Equal("Light", fixture.Effects.Current.ThemePreference);

        viewModel.Cancel();

        Assert.Equal("Light", fixture.Effects.PreviewedThemes[^1]);
        Assert.Equal("Light", viewModel.ThemePreference);
        Assert.Empty(fixture.Effects.AppliedSettings);
    }

    [Fact]
    public void Tray_topmost_hide_and_automatic_update_preferences_preview_before_saving()
    {
        using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();

        viewModel.MinimizeToTray = true;
        viewModel.AlwaysOnTop = true;
        viewModel.HideAfterCopy = false;
        viewModel.AutoCheckUpdates = false;

        AppSettings preview = fixture.Effects.PreviewedSettings[^1];
        Assert.True(preview.MinimizeToTray);
        Assert.True(preview.AlwaysOnTop);
        Assert.False(preview.HideAfterCopy);
        Assert.False(preview.AutoCheckUpdates);
        Assert.Empty(fixture.Effects.AppliedSettings);
    }

    [Fact]
    public void Cancel_reports_when_some_system_previews_could_not_be_restored()
    {
        using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();
        viewModel.ThemePreference = "Dark";
        fixture.Effects.RestoreFailures = ["系统托盘"];

        viewModel.Cancel();

        Assert.Contains("系统托盘", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("Light", viewModel.ThemePreference);
    }
    [Fact]
    public async Task Save_normalizes_capacity_through_AppSettings_and_persists_before_applying_effects()
    {
        using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();
        viewModel.MaxRecordsText = "1";
        viewModel.MaxStorageMbText = "99999";
        viewModel.AlwaysOnTop = true;
        viewModel.AutoCheckUpdates = false;
        viewModel.MinimizeToTray = true;
        viewModel.HideAfterCopy = false;
        viewModel.ThemePreference = "Dark";

        bool saved = await viewModel.SaveAsync(confirmCapacityChange: true);

        Assert.True(saved);
        AppSettings expected = fixture.History.SavedSettings!.Normalize();
        Assert.Equal(50, expected.MaxRecords);
        Assert.Equal(4096, expected.MaxStorageMb);
        Assert.True(expected.AlwaysOnTop);
        Assert.False(expected.AutoCheckUpdates);
        Assert.True(expected.MinimizeToTray);
        Assert.False(expected.HideAfterCopy);
        Assert.Equal("Dark", expected.ThemePreference);
        Assert.Equal(["database-save", "apply-effects"], fixture.Events);
        Assert.Contains("收藏", viewModel.CapacityWarning, StringComparison.Ordinal);
        Assert.Contains("保留", viewModel.CapacityWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lowering_retention_limits_requires_confirmation_before_database_changes()
    {
        using var fixture = new Fixture(current: new AppSettings(600, 1024, false, true, false, true, "Light"));
        var viewModel = fixture.CreateViewModel();
        viewModel.MaxRecordsText = "300";
        viewModel.MaxStorageMbText = "512";

        bool saved = await viewModel.SaveAsync();

        Assert.False(saved);
        Assert.True(viewModel.NeedsCapacityConfirmation);
        Assert.Null(fixture.History.SavedSettings);
        Assert.Empty(fixture.Events);
        Assert.Contains("收藏", viewModel.CapacityWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_failure_does_not_apply_window_or_tray_state_and_rolls_back_theme_preview()
    {
        using var fixture = new Fixture { SaveFailure = new IOException("synthetic database failure") };
        var viewModel = fixture.CreateViewModel();
        viewModel.ThemePreference = "Dark";
        viewModel.AlwaysOnTop = true;
        viewModel.MinimizeToTray = true;

        bool saved = await viewModel.SaveAsync();

        Assert.False(saved);
        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Equal("Light", fixture.Effects.PreviewedThemes[^1]);
        Assert.Empty(fixture.Effects.AppliedSettings);
        Assert.Equal("Light", fixture.Effects.Current.ThemePreference);
        Assert.Equal(["database-save"], fixture.Events);
    }

    [Fact]
    public async Task Data_directory_launch_failure_is_reported_as_failure_not_success()
    {
        using var fixture = new Fixture();
        fixture.Effects.OpenDirectoryFailure = new IOException("synthetic shell failure");
        var viewModel = fixture.CreateViewModel();

        await viewModel.OpenDataDirectoryAsync();

        Assert.Contains("打开", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("成功", viewModel.StatusMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Effects.OpenDirectoryCalls);
    }

    [Fact]
    public async Task Invalid_numeric_text_is_rejected_without_persisting_settings()
    {
        using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();
        viewModel.MaxRecordsText = "many";

        bool saved = await viewModel.SaveAsync();

        Assert.False(saved);
        Assert.Contains("整数", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(fixture.History.SavedSettings);
        Assert.Empty(fixture.Events);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"cm-settings-test-{Guid.NewGuid():N}");

        public Fixture(AppSettings? current = null)
        {
            Directory.CreateDirectory(_root);
            History = new FakeHistoryService(Events) { SaveFailure = () => SaveFailure };
            Effects = new FakeSettingsEffects(current ?? new AppSettings(500, 512, false, true, false, true, "Light"), Events);
        }

        public List<string> Events { get; } = [];
        public FakeHistoryService History { get; }
        public FakeSettingsEffects Effects { get; }
        public Exception? SaveFailure { get; init; }

        public SettingsViewModel CreateViewModel()
            => new(History, Effects, new AppLogger(Path.Combine(_root, "app.log")));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }
    }

    private sealed class FakeSettingsEffects(AppSettings current, List<string> events) : IAppSettingsEffects
    {
        public AppSettings Current { get; private set; } = current;
        public List<string> PreviewedThemes { get; } = [];
        public List<AppSettings> PreviewedSettings { get; } = [];
        public List<AppSettings> AppliedSettings { get; } = [];
        public IReadOnlyList<string> RestoreFailures { get; set; } = [];
        public Exception? OpenDirectoryFailure { get; set; }
        public int OpenDirectoryCalls { get; private set; }

        public void Initialize(AppSettings settings, bool isLightTheme) => Current = settings;
        public void ApplySystemTheme(bool isLightTheme) { }
        public void PreviewSettings(AppSettings settings) { PreviewedSettings.Add(settings); PreviewedThemes.Add(settings.ThemePreference); }
        public IReadOnlyList<string> RestoreSettingsPreview() { PreviewedThemes.Add(Current.ThemePreference); return RestoreFailures; }
        public string DataDirectoryPath => "synthetic-data-directory";

        public IReadOnlyList<string> ApplyPersistedSettings(AppSettings settings)
        {
            events.Add("apply-effects");
            Current = settings;
            AppliedSettings.Add(settings);
            return [];
        }

        public Task OpenDataDirectoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenDirectoryCalls++;
            return OpenDirectoryFailure is null ? Task.CompletedTask : Task.FromException(OpenDirectoryFailure);
        }
    }

    private sealed class FakeHistoryService(List<string> events) : IHistoryService
    {
        public Func<Exception?> SaveFailure { get; init; } = () => null;
        public AppSettings? SavedSettings { get; private set; }

        public Task<HistoryPage> SearchAsync(HistoryQuery query, CancellationToken cancellationToken)
            => Task.FromResult(new HistoryPage([], 0, query.Offset, query.Limit));
        public Task<HistoryChangeResult> AddAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
            => Task.FromResult(new HistoryChangeResult([], [], null));
        public Task<HistoryChangeResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(new HistoryChangeResult([], [], null));
        public Task<HistoryChangeResult> ClearAsync(CancellationToken cancellationToken)
            => Task.FromResult(new HistoryChangeResult([], [], null));
        public Task<HistoryChangeResult> SetFavoriteAsync(Guid id, bool isFavorite, CancellationToken cancellationToken)
            => Task.FromResult(new HistoryChangeResult([], [], null));
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult(SavedSettings ?? new AppSettings(500, 512, false, true, false, true, "Light"));
        public Task<HistoryChangeResult> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("database-save");
            Exception? failure = SaveFailure();
            if (failure is not null) return Task.FromException<HistoryChangeResult>(failure);
            SavedSettings = settings;
            return Task.FromResult(new HistoryChangeResult([], [], null));
        }
    }
}
