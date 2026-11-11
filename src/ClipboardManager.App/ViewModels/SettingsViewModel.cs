using ClipboardManager.App.Services;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClipboardManager.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    public const string RetentionWarning = "降低上限可能立即清理较早的未收藏记录。收藏会保留，不会被自动容量清理删除，即使收藏容量超过上限；手动删除或清空仍会删除收藏。";

    private readonly IHistoryService _history;
    private readonly IAppSettingsEffects _effects;
    private readonly AppLogger _logger;
    private AppSettings _baseline;
    private string _maxRecordsText;
    private string _maxStorageMbText;
    private string _themePreference;
    private bool _alwaysOnTop;
    private bool _autoCheckUpdates;
    private bool _minimizeToTray;
    private bool _hideAfterCopy;
    private bool _isBusy;
    private bool _isResettingDraft;
    private bool _needsCapacityConfirmation;
    private string? _errorMessage;
    private string? _statusMessage;

    public SettingsViewModel(IHistoryService history, IAppSettingsEffects effects, AppLogger logger)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _baseline = effects.Current.Normalize();
        _maxRecordsText = _baseline.MaxRecords.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _maxStorageMbText = _baseline.MaxStorageMb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _themePreference = _baseline.ThemePreference;
        _alwaysOnTop = _baseline.AlwaysOnTop;
        _autoCheckUpdates = _baseline.AutoCheckUpdates;
        _minimizeToTray = _baseline.MinimizeToTray;
        _hideAfterCopy = _baseline.HideAfterCopy;
    }

    public string DataDirectoryPath => _effects.DataDirectoryPath;
    public string CapacityWarning => RetentionWarning;

    public string MaxRecordsText
    {
        get => _maxRecordsText;
        set { if (SetProperty(ref _maxRecordsText, value)) ClearMessages(); }
    }

    public string MaxStorageMbText
    {
        get => _maxStorageMbText;
        set { if (SetProperty(ref _maxStorageMbText, value)) ClearMessages(); }
    }

    public string ThemePreference
    {
        get => _themePreference;
        set
        {
            if (!SetProperty(ref _themePreference, value)) return;
            ClearMessages();
            if (!_isResettingDraft) PreviewCurrentSettings();
        }
    }

    public bool AlwaysOnTop { get => _alwaysOnTop; set { if (SetProperty(ref _alwaysOnTop, value)) { ClearMessages(); if (!_isResettingDraft) PreviewCurrentSettings(); } } }
    public bool AutoCheckUpdates { get => _autoCheckUpdates; set { if (SetProperty(ref _autoCheckUpdates, value)) { ClearMessages(); if (!_isResettingDraft) PreviewCurrentSettings(); } } }
    public bool MinimizeToTray { get => _minimizeToTray; set { if (SetProperty(ref _minimizeToTray, value)) { ClearMessages(); if (!_isResettingDraft) PreviewCurrentSettings(); } } }
    public bool HideAfterCopy { get => _hideAfterCopy; set { if (SetProperty(ref _hideAfterCopy, value)) { ClearMessages(); if (!_isResettingDraft) PreviewCurrentSettings(); } } }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool NeedsCapacityConfirmation { get => _needsCapacityConfirmation; private set => SetProperty(ref _needsCapacityConfirmation, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public async Task<bool> SaveAsync(bool confirmCapacityChange = false, CancellationToken cancellationToken = default)
    {
        if (IsBusy) return false;
        ClearMessages();
        if (!int.TryParse(MaxRecordsText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int maxRecords)
            || !int.TryParse(MaxStorageMbText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int maxStorageMb))
        {
            ErrorMessage = "记录数量和容量必须填写整数。";
            return false;
        }

        string theme = ThemePreference switch
        {
            "Light" => "Light",
            "Dark" => "Dark",
            "System" => "System",
            _ => string.Empty
        };
        if (theme.Length == 0)
        {
            ErrorMessage = "请选择有效的主题。";
            return false;
        }

        AppSettings settings = new AppSettings(maxRecords, maxStorageMb, AlwaysOnTop,
            AutoCheckUpdates, MinimizeToTray, HideAfterCopy, theme).Normalize();
        NeedsCapacityConfirmation = settings.MaxRecords < _baseline.MaxRecords
            || settings.MaxStorageMb < _baseline.MaxStorageMb;
        if (NeedsCapacityConfirmation && !confirmCapacityChange)
        {
            ErrorMessage = "降低保留上限前请确认容量提示。";
            return false;
        }

        IsBusy = true;
        try
        {
            HistoryChangeResult result = await _history.UpdateSettingsAsync(settings, cancellationToken);
            IReadOnlyList<string> effectFailures;
            try { effectFailures = _effects.ApplyPersistedSettings(settings); }
            catch (Exception exception)
            {
                Log(AppLogOperation.Settings, exception);
                effectFailures = ["部分系统行为"];
            }

            _baseline = settings;
            ResetDraft(settings);
            string[] notices = [];
            if (effectFailures.Count > 0)
                notices = [$"设置已保存，但以下系统行为未能立即应用：{string.Join("、", effectFailures)}。可重试或重启应用。"];
            if (!string.IsNullOrWhiteSpace(result.Warning)) notices = [.. notices, result.Warning];
            StatusMessage = notices.Length == 0 ? "设置已保存。" : string.Join(" ", notices);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = RollbackPreviewAndDraft();
            throw;
        }
        catch (Exception exception)
        {
            Log(AppLogOperation.Settings, exception);
            string? rollbackWarning = RollbackPreviewAndDraft();
            ErrorMessage = rollbackWarning is null
                ? "设置保存失败，预览已恢复；未保存任何更改。请检查存储状态后重试。"
                : $"设置保存失败，数据库未接受更改；{rollbackWarning}";
            return false;
        }
        finally { IsBusy = false; }
    }

    public void Cancel()
    {
        if (IsBusy) return;
        ErrorMessage = null;
        StatusMessage = null;
        RollbackPreviewAndDraft();
    }

    public async Task OpenDataDirectoryAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            await _effects.OpenDataDirectoryAsync(cancellationToken);
            StatusMessage = "已请求打开数据目录。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Log(AppLogOperation.Settings, exception);
            ErrorMessage = "无法打开数据目录；请确认该位置可访问后重试。";
        }
    }

    private void PreviewCurrentSettings()
    {
        try { _effects.PreviewSettings(CreateDraft()); }
        catch (Exception exception)
        {
            Log(AppLogOperation.Settings, exception);
            ErrorMessage = "部分预览效果无法应用；取消设置可恢复已保存状态。";
        }
    }

    private AppSettings CreateDraft()
    {
        int maxRecords = int.TryParse(MaxRecordsText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int records) ? records : _baseline.MaxRecords;
        int maxStorageMb = int.TryParse(MaxStorageMbText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int storage) ? storage : _baseline.MaxStorageMb;
        string theme = ThemePreference is "Light" or "Dark" or "System" ? ThemePreference : _baseline.ThemePreference;
        return new AppSettings(maxRecords, maxStorageMb, AlwaysOnTop, AutoCheckUpdates, MinimizeToTray, HideAfterCopy, theme).Normalize();
    }

    private string? RollbackPreviewAndDraft()
    {
        string? warning = null;
        try
        {
            IReadOnlyList<string> failures = _effects.RestoreSettingsPreview();
            if (failures.Count > 0)
                warning = $"窗口或系统预览未能全部恢复：{string.Join("、", failures)}。";
        }
        catch (Exception exception)
        {
            Log(AppLogOperation.Settings, exception);
            warning = "窗口或系统预览未能全部恢复，请重试或重启应用。";
        }
        ResetDraft(_effects.Current.Normalize());
        if (warning is not null) ErrorMessage = warning;
        return warning;
    }

    private void ResetDraft(AppSettings settings)
    {
        _isResettingDraft = true;
        try
        {
            SetProperty(ref _maxRecordsText, settings.MaxRecords.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(MaxRecordsText));
            SetProperty(ref _maxStorageMbText, settings.MaxStorageMb.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(MaxStorageMbText));
            SetProperty(ref _themePreference, settings.ThemePreference, nameof(ThemePreference));
            SetProperty(ref _alwaysOnTop, settings.AlwaysOnTop, nameof(AlwaysOnTop));
            SetProperty(ref _autoCheckUpdates, settings.AutoCheckUpdates, nameof(AutoCheckUpdates));
            SetProperty(ref _minimizeToTray, settings.MinimizeToTray, nameof(MinimizeToTray));
            SetProperty(ref _hideAfterCopy, settings.HideAfterCopy, nameof(HideAfterCopy));
        }
        finally { _isResettingDraft = false; }
        NeedsCapacityConfirmation = false;
        OnPropertyChanged(nameof(DataDirectoryPath));
    }
    private void ClearMessages()
    {
        ErrorMessage = null;
        StatusMessage = null;
        NeedsCapacityConfirmation = false;
    }

    private void Log(AppLogOperation operation, Exception exception)
    {
        try { _logger.LogException(operation, AppLogErrorCategory.Storage, exception); }
        catch { /* Settings feedback remains available if diagnostics cannot be written. */ }
    }
}
