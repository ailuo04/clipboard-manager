using System;
using System.IO;
using Microsoft.Win32;

namespace ClipboardManager.Updates
{
    // Shared with the .NET Framework uninstall helper; keep this compatible with C# 5.
    internal static class StartupRegistration
    {
        internal static void RemoveForExecutable(string executablePath,
            string registryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
        {
            string expected = "\"" + Path.GetFullPath(executablePath) + "\"";
            using (var key = Registry.CurrentUser.OpenSubKey(registryKeyPath))
            {
                if (key == null || !string.Equals(key.GetValue("ClipboardManager") as string, expected, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            using (var key = Registry.CurrentUser.OpenSubKey(registryKeyPath, true))
            {
                if (key != null && string.Equals(key.GetValue("ClipboardManager") as string, expected, StringComparison.OrdinalIgnoreCase))
                    key.DeleteValue("ClipboardManager", false);
            }
        }
    }
}
