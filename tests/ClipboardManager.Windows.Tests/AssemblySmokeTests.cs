using System.Reflection;
using Xunit;

namespace ClipboardManager.Windows.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void Windows_adapter_assembly_is_loadable()
    {
        Assert.Equal("ClipboardManager.Windows", Assembly.Load("ClipboardManager.Windows").GetName().Name);
    }
}
