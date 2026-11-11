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
        string details = $"HRESULT: 0x{exception.HResult:X8}{Environment.NewLine}{exception}";
        WriteStartupDiagnostic(details);
    }

    internal static void WriteStartupDiagnostic(string details, string? directory = null)
    {
        try
        {
            directory ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardManager");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "startup-failure.log");
            File.AppendAllText(path, $"[{DateTimeOffset.UtcNow:O}] {details}{Environment.NewLine}");
        }
        catch { }
    }
}
