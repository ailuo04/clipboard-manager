using Microsoft.Win32;

namespace ClipboardManager.Services;

internal sealed class StartupService
{
    private const string ValueName = "ClipboardManager";
    private readonly string _executablePath;
    private readonly string _registryKeyPath;

    internal StartupService(string? executablePath = null,
        string registryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        _executablePath = Path.GetFullPath(executablePath ?? Path.Combine(AppContext.BaseDirectory, "ClipboardManager.exe"));
        _registryKeyPath = registryKeyPath;
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryKeyPath);
            return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!File.Exists(_executablePath))
                throw new FileNotFoundException("找不到应用启动器，无法启用开机自启动。", _executablePath);
            using var key = Registry.CurrentUser.CreateSubKey(_registryKeyPath);
            key.SetValue(ValueName, "\"" + _executablePath + "\"", RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
