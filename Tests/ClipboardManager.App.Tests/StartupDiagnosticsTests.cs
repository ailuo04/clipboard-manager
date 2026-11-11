using ClipboardManager.App;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class StartupDiagnosticsTests
{
    [Fact]
    public void WriteStartupDiagnostic_appends_resource_trace_without_overwriting_failure_details()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-startup-diagnostic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "startup-failure.log");
        File.WriteAllText(path, "earlier diagnostic");

        try
        {
            Program.WriteStartupDiagnostic("XAML resource lookup details", root);

            string log = File.ReadAllText(path);
            Assert.Contains("earlier diagnostic", log, StringComparison.Ordinal);
            Assert.Contains("XAML resource lookup details", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

