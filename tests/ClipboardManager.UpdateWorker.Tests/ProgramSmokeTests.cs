using ClipboardManager.UpdateWorker;
using Xunit;

namespace ClipboardManager.UpdateWorker.Tests;

public sealed class ProgramSmokeTests
{
    [Fact]
    public void Worker_rejects_unrecognized_commands_without_touching_user_data()
    {
        Assert.Equal(Program.InvalidArgumentsExitCode, Program.Main(Array.Empty<string>()));
        Assert.Equal(Program.InvalidArgumentsExitCode, Program.Main(["--unknown"]));
    }
}
