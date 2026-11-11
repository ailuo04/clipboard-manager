using System.Windows;
using System.Windows.Threading;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager;

public partial class App : Application
{
    private const string ActivationEventName = "Local\\ClipboardManager.Desktop.v1.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private int _exitCode;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "Local\\ClipboardManager.Desktop.v1", out bool created);
        if (!created)
        {
            if (SignalRunningInstance())
            {
                _exitCode = 0;
                Shutdown();
                return;
            }
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
            ListenForActivationRequests();
        }
        catch (Exception exception)
        {
            AppLogger.LogException("启动应用失败", exception);
            MessageBox.Show($"应用无法启动：{exception.Message}\n日志：{AppPaths.LogFilePath}", "剪贴板", MessageBoxButton.OK, MessageBoxImage.Error);
            _exitCode = 1;
            Shutdown();
        }
    }

    // The running instance may be hidden with no taskbar button, so a second launch asks it to show itself.
    private static bool SignalRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivationEventName, out var activation)) return false;
            using (activation)
            {
                NativeMethods.AllowSetForegroundWindow(-1);
                return activation.Set();
            }
        }
        catch (Exception exception)
        {
            AppLogger.LogException("通知已运行的实例显示窗口失败", exception);
            return false;
        }
    }

    private void ListenForActivationRequests()
    {
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activationEvent, (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.ShowFromExternalRequest())),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception exception) { AppLogger.LogException("监听再次启动的显示请求失败", exception); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
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
