using ClipboardManager.App;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class StartupExceptionRecorderTests
{
    [Fact]
    public void Captures_first_chance_exceptions_during_the_recording_scope()
    {
        using var recorder = new StartupExceptionRecorder();
        try { throw new InvalidOperationException("synthetic XAML activation failure"); }
        catch (InvalidOperationException) { }

        string diagnostic = Assert.Single(recorder.Snapshot());
        Assert.Contains(nameof(InvalidOperationException), diagnostic, StringComparison.Ordinal);
        Assert.Contains("synthetic XAML activation failure", diagnostic, StringComparison.Ordinal);
        Assert.Contains("HRESULT 0x", diagnostic, StringComparison.Ordinal);
    }
}
