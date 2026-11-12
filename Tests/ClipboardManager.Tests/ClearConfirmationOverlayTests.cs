using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipboardManager;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using ClipboardManager.ViewModels;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class ClearConfirmationOverlayTests(SettingsOverlayWpfFixture wpf)
{
    [Theory]
    [InlineData(AppTheme.Light, false)]
    [InlineData(AppTheme.Light, true)]
    [InlineData(AppTheme.Dark, false)]
    [InlineData(AppTheme.Dark, true)]
    public async Task ClearCommandWaitsForTheThemedConfirmation(AppTheme theme, bool confirm)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        MainViewModel? viewModel = null;
        WindowOverlayHost? host = null;
        Window? window = null;
        HistoryService? history = null;
        AsyncRelayCommand? command = null;
        try
        {
            history = new HistoryService(new HistoryStore(root));
            await history.LoadAsync();
            await history.AddAsync(ClipboardSnapshot.FromText("first"));
            await history.AddAsync(ClipboardSnapshot.FromText("second"));
            await history.TogglePinAsync(history.Records[1].Id);

            Task initialized = Task.CompletedTask;
            wpf.Run(() =>
            {
                ThemeManager.Apply(theme);
                window = new Window { Width = 300, Height = 400 };
                var frame = new ClipboardManager.Views.WindowFrame();
                window.Content = frame;
                host = new WindowOverlayHost(window, frame);
                viewModel = new MainViewModel(window, history, null, host);
                initialized = viewModel.InitializeAsync();
            });
            await initialized;

            Assert.Equal(new Size(284, 280), OverlaySizePolicy.GetDesiredSize(OverlaySize.ClearConfirmation));
            Assert.True(OverlaySizePolicy.StaysInWidget(OverlaySize.ClearConfirmation));
            wpf.Run(() =>
            {
                command = Assert.IsType<AsyncRelayCommand>(viewModel!.ClearCommand);
                Assert.True(command.CanExecute(null));
                command.Execute(null);
                var overlay = Assert.IsType<ClearConfirmationOverlay>(
                    ((ClipboardManager.Views.WindowFrame)window!.Content).OverlayContent.Content);
                var view = Assert.IsType<ClipboardManager.Views.ClearConfirmationView>(overlay.Content);
                Assert.Equal(284, ((ClipboardManager.Views.WindowFrame)window.Content).OverlayPanel.Width);
                Assert.Equal(300, window.Width);
                Assert.Contains("2 条记录", view.RecordSummaryText.Text, StringComparison.Ordinal);
                Assert.Contains("1 条固定记录", view.RecordSummaryText.Text, StringComparison.Ordinal);
                Assert.True(view.CancelButton.IsDefault);
                Assert.Equal(0, KeyboardNavigation.GetTabIndex(view.CancelButton));
                Assert.Equal(1, KeyboardNavigation.GetTabIndex(view.ClearButton));
                Assert.Equal(2, history!.Records.Count);

                var clicked = confirm ? view.ClearButton : view.CancelButton;
                clicked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, clicked));
            });

            bool commandFinished = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!commandFinished && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
                wpf.Run(() => commandFinished = !viewModel!.IsBusy
                    && (confirm ? history!.Records.Count == 0 : command!.CanExecute(null)));
            }
            Assert.True(commandFinished);
            Assert.Equal(confirm ? 0 : 2, history!.Records.Count);
        }
        finally
        {
            if (viewModel is not null)
            {
                Task shutdown = Task.CompletedTask;
                wpf.Run(() => shutdown = viewModel.ShutdownAsync());
                await shutdown;
            }
            wpf.Run(() =>
            {
                host?.Dispose();
                if (window is not null) window.Content = null;
            });
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
