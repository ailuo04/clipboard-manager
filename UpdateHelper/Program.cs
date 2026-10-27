using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ClipboardManager.Updates;

internal static class UpdateWorker
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Length == 1 && arguments[0] == "--uninstall-managed")
        {
            try
            {
                using (var appGate = AcquireApplication())
                {
                    try { UpdateFiles.RemoveManagedFiles(AppDomain.CurrentDomain.BaseDirectory); }
                    finally { appGate.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception exception) { MessageBox.Show("增量更新文件清理未完成：" + exception.Message, "剪贴板卸载"); return 1; }
        }
        string workspace = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string root = Path.GetDirectoryName(workspace);
        if (Path.GetFileName(workspace) != UpdateFiles.WorkspaceName) return 1;
        try { UpdateFiles.ResolvePath(root, UpdateFiles.WorkspaceName); }
        catch { return 1; }
        using (var gate = new Mutex(false, UpdateFiles.LockName(root)))
        {
            bool ownsGate = false;
            try
            {
                try { ownsGate = gate.WaitOne(0); }
                catch (AbandonedMutexException) { ownsGate = true; }
                if (!ownsGate) return 2;
                if (arguments.Length == 1 && arguments[0] == "--recover")
                {
                    using (var appGate = AcquireApplication())
                    {
                        try
                        {
                            if (!File.Exists(Path.Combine(workspace, "ready")))
                            {
                                UpdateFiles.Rollback(root, workspace);
                                UpdateRegistration(root, UpdateFiles.ReadJson<UpdateManifest>(Path.Combine(root, UpdateFiles.ManifestName)).Version);
                            }
                            File.WriteAllText(Path.Combine(workspace, "complete"), "");
                            return 0;
                        }
                        finally { appGate.ReleaseMutex(); }
                    }
                }
                if (arguments.Length != 3 || arguments[0] != "--apply") throw new InvalidDataException("Invalid updater arguments.");
                int processId = Int32.Parse(arguments[1]);
                long started = Int64.Parse(arguments[2]);
                try
                {
                    using (var parent = Process.GetProcessById(processId))
                    {
                        if (parent.StartTime.ToUniversalTime().Ticks != started) throw new InvalidOperationException("The application process changed.");
                        if (!parent.WaitForExit(120000)) throw new TimeoutException("The application did not close; no files were changed.");
                    }
                }
                catch (ArgumentException) { }
                using (var appGate = AcquireApplication())
                {
                    try
                    {
                        if (File.Exists(Path.Combine(workspace, "journal.json"))) throw new IOException("An unfinished update must be recovered first.");
                        var operations = UpdateFiles.PrepareOperations(root, workspace);
                        UpdateFiles.Apply(root, workspace, operations);
                        UpdateRegistration(root, UpdateFiles.ReadJson<UpdateManifest>(Path.Combine(root, UpdateFiles.ManifestName)).Version);
                    }
                    finally { appGate.ReleaseMutex(); }
                }
                var start = new ProcessStartInfo(Path.Combine(root, "ClipboardManager.exe"), "--verify-update")
                { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
                using (var launched = Process.Start(start))
                {
                    if (!launched.WaitForExit(60000)) { launched.Kill(); launched.WaitForExit(); throw new TimeoutException("Updated application startup timed out."); }
                    if (launched.ExitCode != 0 || !File.Exists(Path.Combine(workspace, "ready"))) throw new IOException("The updated application did not become ready.");
                }
                File.WriteAllText(Path.Combine(workspace, "complete"), "");
                return 0;
            }
            catch (Exception exception)
            {
                string message = exception.Message;
                bool recovered = false;
                try
                {
                    using (var appGate = AcquireApplication())
                    {
                        try
                        {
                            UpdateFiles.Rollback(root, workspace);
                            UpdateRegistration(root, UpdateFiles.ReadJson<UpdateManifest>(Path.Combine(root, UpdateFiles.ManifestName)).Version);
                            File.WriteAllText(Path.Combine(workspace, "complete"), "");
                        }
                        finally { appGate.ReleaseMutex(); }
                    }
                    recovered = true;
                    message += "\n已恢复更新前的版本。";
                }
                catch (Exception recovery) { message += "\n自动恢复未完成：" + recovery.Message + "\n请保留 " + workspace + "，再次启动程序以重试恢复。"; }
                MessageBox.Show("更新未完成：" + message, "剪贴板更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (recovered && arguments.Length > 0 && arguments[0] == "--apply")
                    Process.Start(new ProcessStartInfo(Path.Combine(root, "ClipboardManager.exe")) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true });
                return 1;
            }
            finally { if (ownsGate) gate.ReleaseMutex(); }
        }
    }

    private static Mutex AcquireApplication()
    {
        var mutex = new Mutex(false, "Local\\ClipboardManager.Desktop.v1");
        bool acquired;
        try { acquired = mutex.WaitOne(15000); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); throw new IOException("应用仍在运行，更新文件保持不变。"); }
        return mutex;
    }

    private static void UpdateRegistration(string root, string version)
    {
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8BCDA40E-D12B-4A21-BBD5-EF1653BD6F7B}_is1", true))
        {
            if (key == null) return;
            string location = key.GetValue("InstallLocation") as string;
            if (location != null && Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                key.SetValue("DisplayVersion", version, Microsoft.Win32.RegistryValueKind.String);
        }
    }
}
