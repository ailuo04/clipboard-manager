using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using ClipboardManager.ViewModels;
using ClipboardManager.Views;
using Microsoft.Win32;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class StartupSettingsTests(SettingsOverlayWpfFixture wpf)
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SavingAppliesStartupRegistrationAndReportsFailure(bool executableExists, bool registryReadable)
    {
        string directory = Path.Combine(Path.GetTempPath(), "clipboard-startup-settings-" + Guid.NewGuid().ToString("N"));
        string registryPath = @"Software\ClipboardManager.Tests\Startup-" + Guid.NewGuid().ToString("N");
        string executable = Path.Combine(directory, "ClipboardManager.exe");
        byte[]? savedTheme = File.Exists(AppPaths.ThemeFilePath) ? File.ReadAllBytes(AppPaths.ThemeFilePath) : null;
        MainViewModel? model = null;
        Window? window = null;
        WindowOverlayHost? host = null;
        WindowFrame? frame = null;
        try
        {
            var history = new HistoryService(new HistoryStore(directory));
            await history.LoadAsync();
            var settings = history.Settings;
            settings.AutoCheckUpdates = false;
            await history.UpdateSettingsAsync(settings);
            if (executableExists) File.WriteAllText(executable, "test executable");
            var startup = new StartupService(executable, registryReadable ? registryPath : registryPath + "\\" + new string('x', 256));
            Task initialized = Task.CompletedTask;
            wpf.Run(() =>
            {
                window = new Window { Width = 240, Height = 40, ShowInTaskbar = false };
                frame = new WindowFrame();
                window.Content = frame;
                host = new WindowOverlayHost(window, frame);
                model = new MainViewModel(window, history, host, startup);
                initialized = model.InitializeAsync();
            });
            await initialized;

            if (!registryReadable)
            {
                wpf.Run(() =>
                {
                    model!.SettingsCommand.Execute(null);
                    var overlay = Assert.IsType<SettingsOverlay>(((ContentControl)frame!.FindName("OverlayContent")).Content);
                    var view = Assert.IsType<SettingsView>(overlay.Content);
                    Assert.False(view.StartWithWindowsCheckBox.IsEnabled);
                    Assert.Contains("开机自启动", view.ValidationText.Text);
                    view.MaxRecordsBox.Text = "600";
                    view.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                await WaitForSettingsCommandAsync(model!);
                Assert.Equal(600, history.Settings.MaxRecords);
                wpf.Run(() => Assert.Equal("设置已保存", model!.Status));
                return;
            }

            wpf.Run(() =>
            {
                Assert.True(model!.IsReady, model.Notice);
                model.PauseCommand.Execute(null);
                model.SettingsCommand.Execute(null);
                var overlay = Assert.IsType<SettingsOverlay>(((ContentControl)frame!.FindName("OverlayContent")).Content);
                var view = Assert.IsType<SettingsView>(overlay.Content);
                Assert.False(view.StartWithWindowsCheckBox.IsChecked);
                view.StartWithWindowsCheckBox.IsChecked = true;
                view.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            await WaitForSettingsCommandAsync(model!);
            Assert.False(startup.IsEnabled);

            wpf.Run(() =>
            {
                model!.SettingsCommand.Execute(null);
                var overlay = Assert.IsType<SettingsOverlay>(((ContentControl)frame!.FindName("OverlayContent")).Content);
                var view = Assert.IsType<SettingsView>(overlay.Content);
                view.StartWithWindowsCheckBox.IsChecked = true;
                Assert.False(startup.IsEnabled);
                view.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            await WaitForSettingsCommandAsync(model!);
            Assert.Equal(executableExists, startup.IsEnabled);

            wpf.Run(() =>
            {
                if (!executableExists) Assert.Contains("开机自启动", model!.Notice);
                model!.SettingsCommand.Execute(null);
                var overlay = Assert.IsType<SettingsOverlay>(((ContentControl)frame!.FindName("OverlayContent")).Content);
                var view = Assert.IsType<SettingsView>(overlay.Content);
                Assert.Equal(executableExists, view.StartWithWindowsCheckBox.IsChecked);
                view.StartWithWindowsCheckBox.IsChecked = false;
                view.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            await WaitForSettingsCommandAsync(model!);
            Assert.False(startup.IsEnabled);
        }
        finally
        {
            Task shutdown = Task.CompletedTask;
            wpf.Run(() => { if (model is not null) shutdown = model.ShutdownAsync(); });
            await shutdown;
            wpf.Run(() => { host?.Dispose(); window?.Close(); });
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            if (savedTheme is null) File.Delete(AppPaths.ThemeFilePath);
            else File.WriteAllBytes(AppPaths.ThemeFilePath, savedTheme);
        }
    }

    private async Task WaitForSettingsCommandAsync(MainViewModel model)
    {
        for (int attempt = 0; attempt < 250; attempt++)
        {
            bool finished = false;
            wpf.Run(() => finished = model.SettingsCommand.CanExecute(null));
            if (finished) return;
            await Task.Delay(20);
        }
        Assert.Fail("Settings command did not finish within five seconds.");
    }
}
