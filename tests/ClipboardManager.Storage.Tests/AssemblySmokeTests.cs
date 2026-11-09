using System.Reflection;
using Xunit;

namespace ClipboardManager.Storage.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void Storage_assembly_is_loadable()
    {
        Assert.Equal("ClipboardManager.Storage", Assembly.Load("ClipboardManager.Storage").GetName().Name);
    }
}
