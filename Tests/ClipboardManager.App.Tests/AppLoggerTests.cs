using ClipboardManager.App.Services;
using ClipboardManager.App.Utilities;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class AppLoggerTests
{
    [Fact]
    public void LogException_writes_structured_diagnostics_without_exception_message_or_clipboard_content()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-app-logger-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppPaths(root);
            var logger = new AppLogger(paths.LogFilePath, "test-version");
            var exception = new InvalidOperationException("synthetic clipboard secret Authorization: Bearer synthetic-token");

            logger.LogException(AppLogOperation.ClipboardRead, AppLogErrorCategory.NativeClipboard, exception);
            string line = File.ReadAllText(paths.LogFilePath);

            Assert.Contains("clipboardRead", line, StringComparison.Ordinal);
            Assert.Contains("nativeClipboard", line, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", line, StringComparison.Ordinal);
            Assert.Contains("test-version", line, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic clipboard secret", line, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-token", line, StringComparison.Ordinal);
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}