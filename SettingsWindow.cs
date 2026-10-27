using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Models;
using ClipboardManager.Utilities;

namespace ClipboardManager;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(Window owner, AppSettings settings, string dataDirectory, Action? showUpdates = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = "设置";
        Width = 510;
        Height = 660;
        MinWidth = 460;
        MinHeight = 510;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Settings = settings.Clone();
        var view = ViewLoader.Load<Grid>("Views/SettingsView.xaml");
        Content = view;
        var records = (TextBox)view.FindName("MaxRecordsBox");
        var storage = (TextBox)view.FindName("MaxStorageBox");
        var validation = (TextBlock)view.FindName("ValidationText");
        var alwaysOnTop = (CheckBox)view.FindName("AlwaysOnTopCheckBox");
        var autoUpdates = (CheckBox)view.FindName("AutoCheckUpdatesCheckBox");
        autoUpdates.IsChecked = Settings.AutoCheckUpdates;
        var updateButton = (Button)view.FindName("OpenUpdatesButton");
        updateButton.IsEnabled = showUpdates is not null;
        updateButton.Click += (_, _) => showUpdates?.Invoke();
        ((TextBlock)view.FindName("SettingsVersionText")).Text = "当前版本 " + Services.UpdateService.CurrentVersion;
        records.Text = Settings.MaxRecords.ToString();
        storage.Text = Settings.MaxStorageMb.ToString();
        alwaysOnTop.IsChecked = Settings.AlwaysOnTop;
        ((TextBox)view.FindName("DirectoryBox")).Text = dataDirectory;
        ((Button)view.FindName("CancelButton")).Click += (_, _) => DialogResult = false;
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
            if ((count < settings.MaxRecords || megabytes < settings.MaxStorageMb)
                && MessageBox.Show(this, "降低上限可能立即清理最早的未固定记录，确定保存吗？", "调整保留上限",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            Settings = Settings.Clone();
            Settings.MaxRecords = count;
            Settings.MaxStorageMb = megabytes;
            Settings.AlwaysOnTop = alwaysOnTop.IsChecked == true;
            Settings.AutoCheckUpdates = autoUpdates.IsChecked == true;
            DialogResult = true;
        };
        ((Button)view.FindName("OpenDirectoryButton")).Click += (_, _) => OpenPath(dataDirectory, validation);
        ((Button)view.FindName("OpenLogButton")).Click += (_, _) =>
        {
            if (!File.Exists(AppPaths.LogFilePath)) { validation.Text = "暂无日志文件。"; return; }
            OpenPath(AppPaths.LogFilePath, validation);
        };
    }

    public AppSettings Settings { get; private set; }

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
