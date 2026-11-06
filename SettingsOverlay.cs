using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager;

internal sealed class SettingsOverlay : OverlayView
{
    private readonly AppTheme _openedTheme;

    internal SettingsOverlay(AppSettings settings, string dataDirectory, Func<Task>? showUpdates = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _openedTheme = ThemeManager.Preference;
        Settings = settings.Clone();
        var view = ViewLoader.Load<Grid>("Views/SettingsView.xaml");
        Content = view;

        var records = (TextBox)view.FindName("MaxRecordsBox");
        var storage = (TextBox)view.FindName("MaxStorageBox");
        var validation = (TextBlock)view.FindName("ValidationText");
        var themeDark = (RadioButton)view.FindName("ThemeDarkRadio");
        var themeLight = (RadioButton)view.FindName("ThemeLightRadio");
        var themeSystem = (RadioButton)view.FindName("ThemeSystemRadio");
        switch (_openedTheme)
        {
            case AppTheme.Light: themeLight.IsChecked = true; break;
            case AppTheme.System: themeSystem.IsChecked = true; break;
            default: themeDark.IsChecked = true; break;
        }
        AppTheme SelectedThemeValue() => themeSystem.IsChecked == true ? AppTheme.System
            : themeLight.IsChecked == true ? AppTheme.Light : AppTheme.Dark;
        void PreviewTheme(object? sender, RoutedEventArgs args) => ThemeManager.Apply(SelectedThemeValue());
        themeDark.Checked += PreviewTheme;
        themeLight.Checked += PreviewTheme;
        themeSystem.Checked += PreviewTheme;

        var alwaysOnTop = (CheckBox)view.FindName("AlwaysOnTopCheckBox");
        var autoUpdates = (CheckBox)view.FindName("AutoCheckUpdatesCheckBox");
        var minimizeToTray = (CheckBox)view.FindName("MinimizeToTrayCheckBox");
        autoUpdates.IsChecked = Settings.AutoCheckUpdates;
        minimizeToTray.IsChecked = Settings.MinimizeToTray;
        var updateButton = (Button)view.FindName("OpenUpdatesButton");
        updateButton.IsEnabled = showUpdates is not null;
        updateButton.Click += (_, _) => { if (showUpdates is not null) StartOperation(showUpdates); };
        ((TextBlock)view.FindName("SettingsVersionText")).Text = "当前版本 " + UpdateService.CurrentVersion;
        records.Text = Settings.MaxRecords.ToString();
        storage.Text = Settings.MaxStorageMb.ToString();
        alwaysOnTop.IsChecked = Settings.AlwaysOnTop;
        ((TextBox)view.FindName("DirectoryBox")).Text = dataDirectory;
        ((Button)view.FindName("CancelButton")).Click += (_, _) => Complete(false);
        ((Button)view.FindName("SaveButton")).Click += (_, _) =>
        {
            if (!int.TryParse(records.Text, out int count) || count is < 50 or > 5000)
            {
                validation.Text = "记录数量必须是 50–5000 之间的整数。";
                records.Focus();
                return;
            }
            if (!int.TryParse(storage.Text, out int megabytes) || megabytes is < 64 or > 4096)
            {
                validation.Text = "内容空间必须是 64–4096 MB 之间的整数。";
                storage.Focus();
                return;
            }
            if (count < settings.MaxRecords || megabytes < settings.MaxStorageMb)
            {
                const string message = "降低上限可能立即清理最早的未固定记录，确定保存吗？";
                const string title = "调整保留上限";
                Window? owner = Window.GetWindow(this);
                MessageBoxResult confirmation = owner is null
                    ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                    : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (confirmation != MessageBoxResult.Yes) return;
            }

            AppTheme selectedTheme = SelectedThemeValue();
            Settings = Settings.Clone();
            Settings.MaxRecords = count;
            Settings.MaxStorageMb = megabytes;
            Settings.AlwaysOnTop = alwaysOnTop.IsChecked == true;
            Settings.AutoCheckUpdates = autoUpdates.IsChecked == true;
            Settings.MinimizeToTray = minimizeToTray.IsChecked == true;
            SelectedTheme = selectedTheme;
            Complete(true);
        };
        ((Button)view.FindName("OpenDirectoryButton")).Click += (_, _) => OpenPath(dataDirectory, validation);
        ((Button)view.FindName("OpenLogButton")).Click += (_, _) =>
        {
            if (!File.Exists(AppPaths.LogFilePath)) { validation.Text = "暂无日志文件。"; return; }
            OpenPath(AppPaths.LogFilePath, validation);
        };
    }

    public AppSettings Settings { get; private set; }

    public AppTheme SelectedTheme { get; private set; } = ThemeManager.Preference;

    internal override void OnDismissed(bool? result)
    {
        if (result != true) ThemeManager.Apply(_openedTheme);
    }

    private static void StartOperation(Func<Task> operation) => _ = ObserveOperationAsync(operation);

    private static async Task ObserveOperationAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception) { AppLogger.LogException("打开更新失败", exception); }
    }
    private static void OpenPath(string path, TextBlock validation)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            validation.Text = "打开失败：" + exception.Message;
            AppLogger.LogException("打开数据目录或日志失败", exception);
        }
    }
}
