using System.Reflection;
using Xunit;

namespace ClipboardManager.Core.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void Core_assembly_is_loadable()
    {
        Assert.Equal("ClipboardManager.Core", Assembly.Load("ClipboardManager.Core").GetName().Name);
    }
}
