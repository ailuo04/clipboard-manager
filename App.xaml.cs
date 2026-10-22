using System.Windows;
using System.Windows.Threading;
using ClipboardManager.Utilities;

namespace ClipboardManager;

public sealed class App : Application
{
    [STAThread]
    public static int Main()
    {
        using var mutex = new Mutex(true, "Local\\ClipboardManager.Desktop.v1", out bool created);
        if (!created)
        {
            MessageBox.Show("剪贴板管理器已经在运行。", "剪贴板", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        try
        {
            var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources = ViewLoader.Load<ResourceDictionary>("App.xaml");
            app.DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception exception) AppLogger.LogException("未处理的后台异常", exception);
            };
            return app.Run(new MainWindow());
        }
        catch (Exception exception)
        {
            AppLogger.LogException("启动应用失败", exception);
            MessageBox.Show($"应用无法启动：{exception.Message}\n日志：{AppPaths.LogFilePath}", "剪贴板", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.LogException("未处理的 UI 异常", e.Exception);
        MessageBox.Show($"应用遇到未处理的问题，将停止运行以保护数据。\n{e.Exception.Message}\n日志：{AppPaths.LogFilePath}",
            "剪贴板", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
