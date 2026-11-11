using ClipboardManager.App.Services;
using ClipboardManager.App.Utilities;
using ClipboardManager.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using ClipboardManager.Windows.Tray;

namespace ClipboardManager.App;

public partial class App : Application
{
    private ServiceProvider? _host;
    private AppStartupCoordinator? _startup;
    private AppLogger? _logger;
    private AppPaths? _paths;
    private MainWindow? _window;

    public App()
    {
        try { InitializeComponent(); }
        catch (Exception exception)
        {
            Program.WriteStartupFailure(exception);
            throw;
        }
        if (Environment.GetEnvironmentVariable("CLIPBOARDMANAGER_STARTUP_DIAGNOSTICS") == "1")
        {
            DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
            DebugSettings.XamlResourceReferenceFailed += OnXamlResourceReferenceFailed;
        }
        UnhandledException += OnUnhandledException;
    }

    private static void OnXamlResourceReferenceFailed(object sender, XamlResourceReferenceFailedEventArgs args)
    {
        string properties = string.Join(Environment.NewLine,
            args.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property =>
                {
                    try { return $"{property.Name}: {property.GetValue(args)}"; }
                    catch { return $"{property.Name}: <unavailable>"; }
                }));
        Program.WriteStartupDiagnostic($"XAML resource reference failed:{Environment.NewLine}{properties}");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_host is not null) return;
        if (SynchronizationContext.Current is null)
        {
            DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("The WinUI dispatcher is unavailable during launch.");
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher));
        }
        _paths = new AppPaths();
        _host = AppHost.Create(_paths, new WinUiAppWindowService(CreateMainWindow));
        _ = _host.GetRequiredService<UpdateCoordinator>();
        _host.GetRequiredService<IUpdateRecoveryService>().SetLaunchArguments(args.Arguments);
        _startup = _host.GetRequiredService<AppStartupCoordinator>();
        _logger = _host.GetRequiredService<AppLogger>();
        while (true)
        {
            try
            {
                AppStartupResult result = await _startup.StartAsync(CancellationToken.None);
                if (result == AppStartupResult.AlreadyRunning)
                {
                    CloseHost();
                    Environment.Exit(0);
                }
                return;
            }
            catch (Exception exception)
            {
                if (Environment.GetEnvironmentVariable("CLIPBOARDMANAGER_STARTUP_DIAGNOSTICS") == "1")
                {
                    Program.WriteStartupFailure(exception);
                    Environment.FailFast("Failing fast to preserve the WinUI startup exception for isolated diagnostics.", exception);
                }
                try { _logger.LogException(AppLogOperation.Startup, AppLogErrorCategory.Startup, exception); }
                catch { }
                try
                {
                    MainWindow window = CreateMainWindow();
                    window.Activate();
                    if (await window.ShowStartupFailureDialogAsync(_paths.LogFilePath)) continue;
                }
                catch { }
                try { _window?.Close(); } catch { }
                CloseHost();
                Environment.Exit(1);
                return;
            }
        }
    }

    private MainWindow CreateMainWindow()
    {
        if (_window is not null) return _window;
        _window = new MainWindow(
            () => _host!.GetRequiredService<HistoryViewModel>(),
            () => _host!.GetRequiredService<SettingsViewModel>(),
            () => _host!.GetRequiredService<IAppWindowService>().HideAfterCopyIfEnabled(),
            () => _host!.GetRequiredService<SystemTrayService>().IsEnabled,
            _host!.GetRequiredService<UpdateCoordinator>(),
            () => _host!.GetRequiredService<UpdatesViewModel>());
        _window.Closed += OnMainWindowClosed;
        return _window;
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        try { _startup?.Stop(); }
        catch (Exception exception)
        {
            try { _logger?.LogException(AppLogOperation.PlatformService, AppLogErrorCategory.PlatformService, exception); }
            catch { }
        }
        finally { CloseHost(); }
    }

    private async void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        try { _logger?.LogException(AppLogOperation.UnhandledException, AppLogErrorCategory.Unknown, args.Exception); }
        catch { }
        try
        {
            MainWindow window = CreateMainWindow();
            window.Activate();
            await window.ShowUnhandledErrorDialogAsync(_paths?.LogFilePath);
        }
        catch { }
        finally
        {
            try { _window?.Close(); } catch { }
            CloseHost();
            Environment.Exit(1);
        }
    }

    private void CloseHost()
    {
        ServiceProvider? host = Interlocked.Exchange(ref _host, null);
        if (host is null) return;
        try { host.Dispose(); } catch { }
    }
}
