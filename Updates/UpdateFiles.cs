using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClipboardManager.Updates
{
    public static class UpdateFiles
    {
        public const string ManifestName = "update-manifest.json";
        public const string WorkspaceName = ".clipboard-update";
        public const long MaximumFileSize = 512L * 1024 * 1024;

        public static T ReadJson<T>(string path)
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Update metadata is too large.");
            using (var stream = File.OpenRead(path))
            {
                var value = new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
                if (value == null) throw new InvalidDataException("Empty update metadata.");
                return (T)value;
            }
        }

        public static void WriteJson<T>(string path, T value)
        {
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public static void ValidateManifest(UpdateManifest manifest)
        {
            if (manifest == null || manifest.Schema != 1 || manifest.Application != "ClipboardManager" || manifest.Platform != "win-x64"
                || manifest.Version == null || !Regex.IsMatch(manifest.Version, @"^\d+\.\d+\.\d+$")
                || (manifest.Packaging != "sdk-self-contained" && manifest.Packaging != "offline-bundled-runtime")
                || manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > 5000)
                throw new InvalidDataException("Invalid update manifest.");
            Version.Parse(manifest.Version);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in manifest.Files)
            {
                if (file == null) throw new InvalidDataException("Invalid update file.");
                ValidateRelativePath(file.Path);
                string first = file.Path.Split('/')[0];
                if (first.Equals(WorkspaceName, StringComparison.OrdinalIgnoreCase) || first.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)
                    || first.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || first.Equals("history.json", StringComparison.OrdinalIgnoreCase)
                    || first.Equals("images", StringComparison.OrdinalIgnoreCase) || first.StartsWith("app.log", StringComparison.OrdinalIgnoreCase)
                    || !paths.Add(file.Path) || file.Sha256 == null || !Regex.IsMatch(file.Sha256, "^[0-9a-f]{64}$")
                    || file.Size < 0 || file.Size > MaximumFileSize || file.CompressedSize < 1 || file.CompressedSize > MaximumFileSize + 1024 * 1024)
                    throw new InvalidDataException("Invalid or protected update file: " + file.Path);
                total = checked(total + file.Size);
            }
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Update exceeds the application size limit.");
            foreach (string path in paths)
            {
                string parent = path;
                while (parent.Contains('/'))
                {
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                    if (paths.Contains(parent)) throw new InvalidDataException("Conflicting update paths.");
                }
            }
            foreach (string required in new[] { "ClipboardManager.exe", "ClipboardManager.dll", "ClipboardManager.runtimeconfig.json", "ClipboardManager.Update.exe" })
                if (!paths.Contains(required)) throw new InvalidDataException("Update is missing " + required);
        }

        public static void ValidateRelativePath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 220 || path.Contains('\\') || System.IO.Path.IsPathRooted(path))
                throw new InvalidDataException("Invalid update path.");
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ")
                    || part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
                    || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("Invalid update path: " + path);
            }
        }

        public static void RejectLinks(string path)
        {
            string current = System.IO.Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Updates cannot traverse a symbolic link: " + current);
                current = System.IO.Path.GetDirectoryName(current) ?? String.Empty;
            }
        }

        public static string ResolvePath(string root, string relative)
        {
            ValidateRelativePath(relative);
            root = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escapes the application.");
            string data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardManager");
            if (path.Equals(data, StringComparison.OrdinalIgnoreCase) || path.StartsWith(data + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Updates cannot modify clipboard history.");
            RejectLinks(path);
            return path;
        }

        public static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static bool Matches(string path, UpdateFile file)
        {
            return File.Exists(path) && new FileInfo(path).Length == file.Size && Hash(path) == file.Sha256;
        }

        public static UpdatePlan Plan(string root, UpdateManifest current, UpdateManifest next, CancellationToken token)
        {
            ValidateManifest(current);
            ValidateManifest(next);
            if (current.Packaging != next.Packaging) throw new InvalidDataException("新版与当前版本打包方式不同，无法增量更新；不会自动下载完整安装包。");
            var oldPaths = current.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var newPaths = new HashSet<string>(next.Files.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
            var plan = new UpdatePlan();
            foreach (var file in next.Files)
            {
                token.ThrowIfCancellationRequested();
                string path = ResolvePath(root, file.Path);
                if (Directory.Exists(path)) throw new IOException("A directory conflicts with an update file: " + file.Path);
                if (Matches(path, file)) continue;
                if (!oldPaths.ContainsKey(file.Path) && File.Exists(path)) throw new IOException("不会覆盖非发行版文件：" + file.Path);
                plan.Changes.Add(file);
            }
            foreach (var file in current.Files.Where(file => !newPaths.Contains(file.Path)))
            {
                token.ThrowIfCancellationRequested();
                string path = ResolvePath(root, file.Path);
                if (!File.Exists(path)) continue;
                if (!Matches(path, file)) throw new IOException("旧版文件已被手动修改，不会删除：" + file.Path);
                plan.Deletes.Add(file);
            }
            return plan;
        }

        public static List<UpdateOperation> PrepareOperations(string root, string workspace)
        {
            var current = ReadJson<UpdateManifest>(System.IO.Path.Combine(workspace, "old-manifest.json"));
            var next = ReadJson<UpdateManifest>(System.IO.Path.Combine(workspace, "new", ManifestName));
            if (Version.Parse(next.Version) <= Version.Parse(current.Version)) throw new InvalidDataException("Updates must advance the application version.");
            var plan = Plan(root, current, next, CancellationToken.None);
            var operations = new List<UpdateOperation>();
            foreach (var file in plan.Changes)
            {
                if (!Matches(ResolvePath(System.IO.Path.Combine(workspace, "new"), file.Path), file)) throw new InvalidDataException("Staged update failed SHA256 verification: " + file.Path);
                operations.Add(new UpdateOperation { Path = file.Path, HadOriginal = File.Exists(ResolvePath(root, file.Path)), Sha256 = file.Sha256 });
            }
            foreach (var file in plan.Deletes) operations.Add(new UpdateOperation { Path = file.Path, HadOriginal = true, DeleteOnly = true, Sha256 = file.Sha256 });
            operations.Add(new UpdateOperation { Path = ManifestName, HadOriginal = true, Sha256 = Hash(System.IO.Path.Combine(workspace, "new", ManifestName)) });
            return operations;
        }

        private static void ValidateOperations(string root, string workspace, List<UpdateOperation> operations)
        {
            var oldManifest = ReadJson<UpdateManifest>(System.IO.Path.Combine(workspace, "old-manifest.json"));
            // The new manifest may already have been moved into the application.
            string nextPath = System.IO.Path.Combine(workspace, "new", ManifestName);
            if (!File.Exists(nextPath)) nextPath = ResolvePath(root, ManifestName);
            var nextManifest = ReadJson<UpdateManifest>(nextPath);
            ValidateManifest(oldManifest);
            ValidateManifest(nextManifest);
            var oldFiles = oldManifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var nextFiles = nextManifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (operations == null || operations.Count == 0 || operations.Count > 10001) throw new InvalidDataException("Invalid update journal.");
            foreach (var operation in operations)
            {
                ResolvePath(root, operation.Path);
                if (!seen.Add(operation.Path)) throw new InvalidDataException("Duplicate update operation.");
                if (operation.Path == ManifestName)
                {
                    if (operation.DeleteOnly || !operation.HadOriginal) throw new InvalidDataException("Invalid manifest operation.");
                    continue;
                }
                if (operation.DeleteOnly)
                {
                    if (!oldFiles.ContainsKey(operation.Path) || nextFiles.ContainsKey(operation.Path) || !operation.HadOriginal)
                        throw new InvalidDataException("Invalid update deletion.");
                }
                else if (!nextFiles.ContainsKey(operation.Path)) throw new InvalidDataException("Invalid update replacement.");
                UpdateFile file = operation.DeleteOnly ? oldFiles[operation.Path] : nextFiles[operation.Path];
                if (operation.Sha256 != file.Sha256) throw new InvalidDataException("Invalid update operation checksum.");
            }
            if (operations[operations.Count - 1].Path != ManifestName) throw new InvalidDataException("The manifest must be committed last.");
        }

        public static void Apply(string root, string workspace, List<UpdateOperation> operations)
        {
            ValidateOperations(root, workspace, operations);
            WriteJson(System.IO.Path.Combine(workspace, "journal.json"), operations);
            foreach (var operation in operations)
            {
                string target = ResolvePath(root, operation.Path);
                string staged = ResolvePath(System.IO.Path.Combine(workspace, "new"), operation.Path);
                string backup = ResolvePath(System.IO.Path.Combine(workspace, "backup"), operation.Path);
                CreateParent(backup);
                if (operation.HadOriginal && !operation.DeleteOnly)
                {
                    // Atomic replacement keeps the launcher present even if power is lost during its own update.
                    Retry(delegate { File.Replace(staged, target, backup); });
                }
                else if (operation.DeleteOnly) Retry(delegate { File.Move(target, backup); });
                else
                {
                    CreateParent(target);
                    Retry(delegate { File.Move(staged, target); });
                }
            }
            using (var stream = new FileStream(System.IO.Path.Combine(workspace, "applied"), FileMode.Create, FileAccess.Write, FileShare.Read)) stream.Flush(true);
        }

        public static void Rollback(string root, string workspace)
        {
            string journal = System.IO.Path.Combine(workspace, "journal.json");
            if (!File.Exists(journal)) return;
            var operations = ReadJson<List<UpdateOperation>>(journal);
            ValidateOperations(root, workspace, operations);
            foreach (var operation in operations.AsEnumerable().Reverse())
            {
                string target = ResolvePath(root, operation.Path);
                string backup = ResolvePath(System.IO.Path.Combine(workspace, "backup"), operation.Path);
                string staged = ResolvePath(System.IO.Path.Combine(workspace, "new"), operation.Path);
                if (File.Exists(backup))
                {
                    // Keep a copy of the new manifest until rollback is complete, including recovery after a second interruption.
                    if (operation.Path == ManifestName && File.Exists(target) && !File.Exists(staged))
                    {
                        CreateParent(staged);
                        File.Copy(target, staged);
                    }
                    Retry(delegate
                    {
                        if (File.Exists(target)) File.Replace(backup, target, null);
                        else File.Move(backup, target);
                    });
                }
                else if (!operation.HadOriginal && !File.Exists(staged) && File.Exists(target))
                {
                    if (Hash(target) != operation.Sha256) throw new IOException("A new file was modified; recovery will not delete it: " + operation.Path);
                    Retry(delegate { File.Delete(target); });
                }
            }
            File.Delete(System.IO.Path.Combine(workspace, "applied"));
            File.Delete(journal);
        }

        public static void Retry(Action action)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { action(); return; }
                catch (IOException) { if (attempt >= 39) throw; Thread.Sleep(250); }
            }
        }

        private static void CreateParent(string path)
        {
            string parent = System.IO.Path.GetDirectoryName(path) ?? String.Empty;
            if (parent.Length == 0) throw new InvalidDataException("Missing update parent directory.");
            Directory.CreateDirectory(parent);
        }

        public static string LockName(string root)
        {
            using (var hash = SHA256.Create())
                return "Local\\ClipboardManager.Update." + BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar).ToUpperInvariant()))).Replace("-", "");
        }

        public static void CleanWorkspace(string root)
        {
            string workspace = ResolvePath(root, WorkspaceName);
            if (!Directory.Exists(workspace)) return;
            // Never recursively delete through links, including links introduced after staging.
            var directories = new Stack<string>();
            directories.Push(workspace);
            while (directories.Count > 0)
            {
                string directory = directories.Pop();
                RejectLinks(directory);
                foreach (string file in Directory.GetFiles(directory)) RejectLinks(file);
                foreach (string child in Directory.GetDirectories(directory)) { RejectLinks(child); directories.Push(child); }
            }
            Directory.Delete(workspace, true);
        }

        public static void RemoveManagedFiles(string root)
        {
            string manifestPath = ResolvePath(root, ManifestName);
            if (!File.Exists(manifestPath)) return;
            var manifest = ReadJson<UpdateManifest>(manifestPath);
            ValidateManifest(manifest);
            foreach (var file in manifest.Files)
            {
                if (file.Path.Equals("ClipboardManager.Update.exe", StringComparison.OrdinalIgnoreCase)) continue;
                string path = ResolvePath(root, file.Path);
                if (Matches(path, file)) File.Delete(path);
            }
            File.Delete(manifestPath);
            RemoveEmptyDirectories(root, manifest.Files.Select(file => file.Path));
        }

        public static void RemoveEmptyDirectories(string root, IEnumerable<string> paths)
        {
            root = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            foreach (string relative in paths)
            {
                string directory = System.IO.Path.GetDirectoryName(ResolvePath(root, relative)) ?? root;
                while (!directory.Equals(root, StringComparison.OrdinalIgnoreCase) && directory.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    RejectLinks(directory);
                    if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) break;
                    Directory.Delete(directory);
                    directory = System.IO.Path.GetDirectoryName(directory) ?? root;
                }
            }
        }
    }
}
