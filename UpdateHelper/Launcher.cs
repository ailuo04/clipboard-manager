using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using ClipboardManager.Updates;

internal static class DesktopLauncher
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        Process child = null;
        try
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string workspace = UpdateFiles.ResolvePath(root, UpdateFiles.WorkspaceName);
            bool probe = arguments.Length == 1 && arguments[0] == "--verify-update";
            if (File.Exists(Path.Combine(workspace, "journal.json")))
            {
                var recovery = new ProcessStartInfo(Path.Combine(workspace, "worker.exe"), "--recover")
                { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
                using (var process = Process.Start(recovery))
                {
                    process.WaitForExit();
                    if (process.ExitCode == 2)
                    {
                        if (!File.Exists(Path.Combine(workspace, "applied"))) throw new IOException("程序正在更新，请稍后再打开。");
                    }
                    else if (process.ExitCode != 0) return process.ExitCode;
                }
            }
            if (File.Exists(Path.Combine(workspace, "complete")))
                UpdateFiles.Retry(delegate { UpdateFiles.CleanWorkspace(root); });
            string executable = Path.Combine(root, "ClipboardManager.App.exe");
            var start = new ProcessStartInfo { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
            if (File.Exists(executable)) start.FileName = executable;
            else
            {
                string host = Path.Combine(root, "runtime", "dotnet.exe");
                if (!File.Exists(host)) host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
                start.FileName = host;
                start.Arguments = "\"" + Path.Combine(root, "ClipboardManager.dll") + "\"";
            }
            child = Process.Start(start);
            if (probe)
            {
                for (int attempt = 0; attempt < 150; attempt++)
                {
                    if (File.Exists(Path.Combine(workspace, "ready"))) return 0;
                    if (child.WaitForExit(200)) return 1;
                }
                child.Kill();
                child.WaitForExit();
                return 1;
            }
            child.WaitForExit();
            if (child.ExitCode != 0) MessageBox.Show("应用已异常退出。请检查 %LOCALAPPDATA%\\ClipboardManager\\app.log。", "剪贴板");
            return child.ExitCode;
        }
        catch (Exception exception)
        {
            if (child != null && arguments.Length > 0 && !child.HasExited) { child.Kill(); child.WaitForExit(); }
            if (arguments.Length == 0) MessageBox.Show(exception.Message, "剪贴板");
            return 1;
        }
        finally { if (child != null) child.Dispose(); }
    }
}
