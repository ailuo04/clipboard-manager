using System.Text;
using ClipboardManager.Core.Updates;
using Xunit;

namespace ClipboardManager.Update.Tests;

public sealed class UpdateFileSystemTests
{
    [Fact]
    public void Atomic_write_recovers_when_a_previous_temporary_file_was_left_by_a_crash()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-atomic-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string destination = Path.Combine(root, "journal.json");
            File.WriteAllText(destination + ".tmp", "interrupted-write");
            var fileSystem = new PhysicalUpdateFileSystem();

            fileSystem.WriteAtomic(destination, Encoding.UTF8.GetBytes("complete-journal"));

            Assert.Equal("complete-journal", File.ReadAllText(destination));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
