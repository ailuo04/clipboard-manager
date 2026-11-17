using System.IO;
using ClipboardManager.Services;
using ClipboardManager.Updates;
using Microsoft.Win32;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class StartupServiceTests : IDisposable
{
    private readonly string _registryPath = @"Software\ClipboardManager.Tests\Startup-" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clipboard startup " + Guid.NewGuid().ToString("N"));

    private StartupService CreateService() => new(Path.Combine(_directory, "剪贴板 应用.exe"), _registryPath);

    [Fact]
    public void ReadingAndDisablingAbsentRegistrationDoNotCreateAKey()
    {
        var service = CreateService();

        Assert.False(service.IsEnabled);
        service.SetEnabled(false);

        using var key = Registry.CurrentUser.OpenSubKey(_registryPath);
        Assert.Null(key);
    }

    [Fact]
    public void EnablePersistsAQuotedExecutableAndPreservesOtherStartupEntries()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "剪贴板 应用.exe"), "test executable");
        using var key = Registry.CurrentUser.CreateSubKey(_registryPath);
        key.SetValue("OtherApp", "leave this alone");

        CreateService().SetEnabled(true);

        Assert.Equal("\"" + _directory + "\\剪贴板 应用.exe\"", key.GetValue("ClipboardManager"));
        Assert.Equal(RegistryValueKind.String, key.GetValueKind("ClipboardManager"));
        Assert.Equal("leave this alone", key.GetValue("OtherApp"));
        Assert.True(CreateService().IsEnabled);
    }

    [Fact]
    public void DisableRemovesOnlyTheAppEntryAndCanBeRepeated()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_registryPath);
        key.SetValue("ClipboardManager", @"""C:\Old Location\ClipboardManager.exe""");
        key.SetValue("OtherApp", "leave this alone");
        var service = CreateService();
        Assert.True(service.IsEnabled);

        service.SetEnabled(false);
        service.SetEnabled(false);

        Assert.Null(key.GetValue("ClipboardManager"));
        Assert.Equal("leave this alone", key.GetValue("OtherApp"));
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void MissingExecutableCannotReplaceAnExistingRegistration()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_registryPath);
        key.SetValue("ClipboardManager", "previous command");

        Assert.Throws<FileNotFoundException>(() => CreateService().SetEnabled(true));

        Assert.Equal("previous command", key.GetValue("ClipboardManager"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UninstallRemovesOnlyRegistrationForItsOwnExecutable(bool sameInstallation)
    {
        string executable = Path.Combine(_directory, "ClipboardManager.exe");
        string registered = sameInstallation ? executable : @"C:\Portable\ClipboardManager.exe";
        using var key = Registry.CurrentUser.CreateSubKey(_registryPath);
        key.SetValue("ClipboardManager", "\"" + registered + "\"");
        key.SetValue("OtherApp", "leave this alone");

        StartupRegistration.RemoveForExecutable(executable, _registryPath);

        Assert.Equal(sameInstallation ? null : "\"" + registered + "\"", key.GetValue("ClipboardManager"));
        Assert.Equal("leave this alone", key.GetValue("OtherApp"));
    }
    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryPath, throwOnMissingSubKey: false);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
