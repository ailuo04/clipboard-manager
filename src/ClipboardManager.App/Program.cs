using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClipboardManager.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                try
                {
                    DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread()
                        ?? throw new InvalidOperationException("The WinUI dispatcher is unavailable during application startup.");
                    SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                    new App();
                }
                catch (Exception exception)
                {
                    WriteStartupFailure(exception);
                    throw;
                }
            });
        }
        catch (Exception exception)
        {
            WriteStartupFailure(exception);
            throw;
        }
    }

    internal static void WriteStartupFailure(Exception exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardManager");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "startup-failure.log"), exception.ToString());
        }
        catch { }
    }
}
