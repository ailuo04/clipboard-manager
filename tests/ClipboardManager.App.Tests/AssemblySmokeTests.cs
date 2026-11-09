using ClipboardManager.App;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void App_bootstrap_type_is_in_the_new_assembly()
    {
        Assert.Equal("ClipboardManager.App", typeof(App).Assembly.GetName().Name);
    }
}
