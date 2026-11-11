using System.Security.Cryptography;

namespace ClipboardManager.Core.Updates;

public interface IUpdateFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    bool IsReparsePoint(string path);
    long GetLength(string path);
    byte[] ReadAllBytes(string path);
    string ComputeSha256(string path);
    IEnumerable<string> EnumerateEntries(string directory);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination, bool overwrite = false);
    void MoveFile(string source, string destination);
    void ReplaceFile(string source, string destination);
    void DeleteFile(string path);
    void WriteAtomic(string path, ReadOnlySpan<byte> contents);
    void DeleteDirectory(string path, bool recursive);
}

public class PhysicalUpdateFileSystem : IUpdateFileSystem
{
    public virtual bool FileExists(string path) => File.Exists(path);
    public virtual bool DirectoryExists(string path) => Directory.Exists(path);
    public virtual bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    public virtual long GetLength(string path) => new FileInfo(path).Length;
    public virtual byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public virtual string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public virtual IEnumerable<string> EnumerateEntries(string directory)
        => Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
    public virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public virtual void CopyFile(string source, string destination, bool overwrite = false)
    {
        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("Update file has no parent directory.");
        Directory.CreateDirectory(parent);
        File.Copy(source, destination, overwrite);
        using var stream = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }
    public virtual void MoveFile(string source, string destination)
    {
        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("Update file has no parent directory.");
        Directory.CreateDirectory(parent);
        File.Move(source, destination);
    }
    public virtual void ReplaceFile(string source, string destination)
        => File.Replace(source, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
    public virtual void DeleteFile(string path) => File.Delete(path);
    public virtual void WriteAtomic(string path, ReadOnlySpan<byte> contents)
    {
        string parent = Path.GetDirectoryName(path) ?? throw new IOException("Journal has no parent directory.");
        Directory.CreateDirectory(parent);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* A later workspace recovery can safely remove an orphaned unique temp file. */ }
        }
    }
    public virtual void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
}

