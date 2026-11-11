namespace ClipboardManager.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { XamlGeneratedProgram.XamlGeneratedMain(); }
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
