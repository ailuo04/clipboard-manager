using ClipboardManager.App.Utilities;
using ClipboardManager.Core.Settings;
using ClipboardManager.Windows.Tray;

namespace ClipboardManager.App.Services;

public interface IAppSettingsEffects
{
    AppSettings Current { get; }
    string DataDirectoryPath { get; }
    void Initialize(AppSettings settings, bool isLightTheme);
    void ApplySystemTheme(bool isLightTheme);
    void PreviewSettings(AppSettings settings);
    IReadOnlyList<string> RestoreSettingsPreview();
    IReadOnlyList<string> ApplyPersistedSettings(AppSettings settings);
    Task OpenDataDirectoryAsync(CancellationToken cancellationToken);
}

/// <summary>Applies persisted preferences and reversible previews to the live shell and platform services.</summary>
public sealed class AppSettingsEffects : IAppSettingsEffects
{
    private readonly IAppWindowService _window;
    private readonly SystemTrayService _tray;
    private readonly IUpdateCheckScheduler _updateCheckScheduler;
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private bool _isLightTheme = true;
    private AppSettings? _previewSettings;

    public AppSettingsEffects(IAppWindowService window, SystemTrayService tray,
        IUpdateCheckScheduler updateCheckScheduler, AppPaths paths, AppLogger logger)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _updateCheckScheduler = updateCheckScheduler ?? throw new ArgumentNullException(nameof(updateCheckScheduler));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AppSettings Current { get; private set; } = new(500, 512, false, true, false, true, "System");
    public string DataDirectoryPath => _paths.RootDirectory;

    public void Initialize(AppSettings settings, bool isLightTheme)
    {
        Current = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();
        _previewSettings = null;
        _isLightTheme = isLightTheme;
        _window.ApplySettings(Current, _isLightTheme);
        _updateCheckScheduler.SetEnabled(Current.AutoCheckUpdates);
    }

    public void ApplySystemTheme(bool isLightTheme)
    {
        _isLightTheme = isLightTheme;
        _window.ApplySettings(_previewSettings ?? Current, _isLightTheme);
    }

    public void PreviewSettings(AppSettings settings)
    {
        AppSettings preview = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();
        _previewSettings = preview;
        IReadOnlyList<string> failures = ApplyLiveSettings(preview);
        if (failures.Count > 0)
            throw new InvalidOperationException($"Could not preview: {string.Join(", ", failures)}.");
    }

    public IReadOnlyList<string> RestoreSettingsPreview()
    {
        _previewSettings = null;
        return ApplyLiveSettings(Current);
    }

    public IReadOnlyList<string> ApplyPersistedSettings(AppSettings settings)
    {
        Current = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();
        _previewSettings = null;
        return ApplyLiveSettings(Current);
    }

    public Task OpenDataDirectoryAsync(CancellationToken cancellationToken)
        => _window.OpenDataDirectoryAsync(DataDirectoryPath, cancellationToken);

    private IReadOnlyList<string> ApplyLiveSettings(AppSettings settings)
    {
        var failures = new List<string>();
        TryApply("窗口行为", () => _window.ApplySettings(settings, _isLightTheme), failures);
        TryApply("系统托盘", () => _tray.SetEnabled(settings.MinimizeToTray), failures);
        TryApply("自动检查更新", () => _updateCheckScheduler.SetEnabled(settings.AutoCheckUpdates), failures);
        return failures;
    }

    private void TryApply(string label, Action apply, List<string> failures)
    {
        try { apply(); }
        catch (Exception exception)
        {
            failures.Add(label);
            try { _logger.LogException(AppLogOperation.Settings, AppLogErrorCategory.PlatformService, exception); }
            catch { /* A diagnostics failure must not hide the persisted settings result. */ }
        }
    }
}
