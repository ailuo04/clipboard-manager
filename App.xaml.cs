using System.Windows;
using System.Windows.Threading;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private int _exitCode;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "Local\\ClipboardManager.Desktop.v1", out bool created);
        if (!created)
        {
            MessageBox.Show("剪贴板管理器已经在运行。", "剪贴板", MessageBoxButton.OK, MessageBoxImage.Information);
            _exitCode = 0;
            Shutdown();
            return;
        }

        try
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            ThemeManager.Initialize();
            Resources["AppIcon"] = ViewLoader.LoadIcon();
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception exception) AppLogger.LogException("未处理的后台异常", exception);
            };
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            AppLogger.LogException("启动应用失败", exception);
            MessageBox.Show($"应用无法启动：{exception.Message}\n日志：{AppPaths.LogFilePath}", "剪贴板", MessageBoxButton.OK, MessageBoxImage.Error);
            _exitCode = 1;
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        try { _instanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { }
        finally { _instanceMutex?.Dispose(); }
        if (_exitCode != 0) e.ApplicationExitCode = _exitCode;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.LogException("未处理的 UI 异常", e.Exception);
        MessageBox.Show($"应用遇到未处理的问题，将停止运行以保护数据。\n{e.Exception.Message}\n日志：{AppPaths.LogFilePath}",
            "剪贴板", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
