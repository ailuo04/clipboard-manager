using System.Windows;
using ClipboardManager;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class OverlayArchitectureTests
{
    [Fact]
    public void MainWindowIsTheOnlyApplicationWindow()
    {
        Type[] auxiliaryWindows = typeof(MainWindow).Assembly.GetTypes()
            .Where(type => type != typeof(MainWindow) && typeof(Window).IsAssignableFrom(type))
            .ToArray();

        Assert.Empty(auxiliaryWindows);
    }
}
