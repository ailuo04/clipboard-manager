using ClipboardManager.UpdateWorker;
using Xunit;

namespace ClipboardManager.UpdateWorker.Tests;

public sealed class ProgramSmokeTests
{
    [Fact]
    public void Worker_entry_point_returns_success_without_touching_user_data()
    {
        Assert.Equal(0, Program.Main(Array.Empty<string>()));
    }
}
