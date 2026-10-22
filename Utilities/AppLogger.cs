using System.Diagnostics;
using System.Text;

namespace ClipboardManager.Utilities;

public static class AppLogger
{
    private static readonly object SyncRoot = new();

    public static void Log(string message)
    {
        Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [INFO] {message}");
    }

    public static void LogException(string message, Exception exception)
    {
        Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [ERROR] {message}\n{exception}");
    }

    private static void Write(string message)
    {
        try
        {
            AppPaths.EnsureDirectories();
            lock (SyncRoot)
            {
                if (File.Exists(AppPaths.LogFilePath) && new FileInfo(AppPaths.LogFilePath).Length > 2 * 1024 * 1024)
                    File.Move(AppPaths.LogFilePath, AppPaths.LogFilePath + ".old", overwrite: true);
                File.AppendAllText(AppPaths.LogFilePath, message + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception loggingException)
        {
            Debug.WriteLine($"日志写入失败: {loggingException}");
            Debug.WriteLine(message);
        }
    }
}
