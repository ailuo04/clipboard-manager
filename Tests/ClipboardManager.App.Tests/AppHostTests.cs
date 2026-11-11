using Microsoft.Extensions.DependencyInjection;
using ClipboardManager.App;
using ClipboardManager.App.Services;
using ClipboardManager.App.ViewModels;
using ClipboardManager.App.Utilities;
using Microsoft.UI.Xaml;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class AppHostTests
{
    [Fact]
    public void Create_validates_the_composition_graph_without_starting_windows_services_or_touching_user_data()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-host-test-{Guid.NewGuid():N}");
        using ServiceProvider host = AppHost.Create(new AppPaths(root), new FakeWindowService());

        Assert.NotNull(host);
        Assert.NotNull(host.GetRequiredService<UpdateCoordinator>());
        Assert.NotNull(host.GetRequiredService<IUpdateCheckScheduler>());
        Assert.NotNull(host.GetRequiredService<IUpdateApplyService>());
        using (UpdatesViewModel updates = host.GetRequiredService<UpdatesViewModel>())
            Assert.NotNull(updates);
        Assert.False(Directory.Exists(root));
        Assert.False(Directory.Exists(new AppPaths(root).UpdateStagingRoot));
    }

    private sealed class FakeWindowService : IAppWindowService
    {
        public Task ShowMainWindowAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void ApplySettings(ClipboardManager.Core.Settings.AppSettings settings, bool isLightTheme) { }
        public void ApplyTheme(string preference, bool isLightTheme) { }
        public void HideAfterCopyIfEnabled() { }
        public void NotifyHistoryChanged() { }
        public Task OpenDataDirectoryAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
        public void RequestExit() { }
    }
}


