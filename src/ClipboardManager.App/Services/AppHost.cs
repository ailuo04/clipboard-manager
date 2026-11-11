using ClipboardManager.App;
using ClipboardManager.App.Utilities;
using ClipboardManager.App.ViewModels;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Storage;
using ClipboardManager.Storage.Sqlite;
using ClipboardManager.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ClipboardManager.App.Services;

public static class AppHost
{
    public static ServiceProvider Create(AppPaths paths, IAppWindowService window)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(window);
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(new AppLogger(paths.LogFilePath));
        services.AddSingleton<IAppWindowService>(window);
        services.AddSingleton<SqliteHistoryRepository>(_ => new SqliteHistoryRepository(paths.DatabasePath));
        services.AddSingleton<IHistoryRepository>(provider => provider.GetRequiredService<SqliteHistoryRepository>());
        services.AddSingleton<IDatabaseSchemaMigrator>(_ => new SqliteSchemaMigrationRunner(new SqliteConnectionFactory(paths.DatabasePath)));
        services.AddSingleton<FileImageStore>(_ => new FileImageStore(paths.ImagesDirectory));
        services.AddSingleton<IImageStore>(provider => provider.GetRequiredService<FileImageStore>());
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IAppSettingsEffects, AppSettingsEffects>();
        services.AddSingleton<ClipboardCaptureCoordinator>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<UpdatesViewModel>(provider =>
        {
            SettingsViewModel settings = provider.GetRequiredService<SettingsViewModel>();
            return new UpdatesViewModel(provider.GetRequiredService<UpdateCoordinator>(), settings.AutoCheckUpdates,
                async (enabled, cancellationToken) =>
                {
                    settings.AutoCheckUpdates = enabled;
                    bool saved = await settings.SaveAsync(cancellationToken: cancellationToken);
                    if (!saved)
                    {
                        string? message = settings.ErrorMessage;
                        settings.Cancel();
                        return new UpdateCheckPreferenceResult(settings.AutoCheckUpdates, false, message);
                    }
                    return new UpdateCheckPreferenceResult(settings.AutoCheckUpdates, true, settings.StatusMessage);
                }, paths.LogFilePath);
        });
        services.AddSingleton<IUpdateReleaseService, GitHubReleaseClient>();
        services.AddSingleton<IUpdatePackageService, UpdatePackageService>();
        services.AddSingleton<IUpdateWorkerLauncher, ProcessUpdateWorkerLauncher>();
        services.AddSingleton<IUpdateApplyService, UpdateHandoffService>();
        services.AddSingleton<UpdateCoordinator>();
        services.AddSingleton<IUpdateCheckScheduler>(provider => new DelayedUpdateCheckScheduler(
            cancellationToken => provider.GetRequiredService<UpdateCoordinator>().CheckForUpdatesAsync(cancellationToken),
            provider.GetRequiredService<AppLogger>()));
        services.AddSingleton<IUpdateRecoveryService, UpdateRecoveryService>();
        services.AddSingleton<IAppStartupRuntime, AppStartupRuntime>();
        services.AddSingleton<AppStartupCoordinator>();
        services.AddClipboardManagerWindowsServices();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}




