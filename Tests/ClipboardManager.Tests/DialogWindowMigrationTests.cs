using ClipboardManager;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class DialogWindowMigrationTests
{
    [Fact]
    public void AuxiliaryWindows_InheritSharedGlassDialogWindow()
    {
        Assert.Equal(typeof(GlassDialogWindow), typeof(DetailsWindow).BaseType);
        Assert.Equal(typeof(GlassDialogWindow), typeof(SettingsWindow).BaseType);
        Assert.Equal(typeof(GlassDialogWindow), typeof(UpdateWindow).BaseType);
    }
}
