using System.IO;
using ClipboardManager.Models;
using ClipboardManager.Services;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class HistoryServiceTests
{
    [Fact]
    public async Task LoadBacksUpHistoryBeforeSkippedRecordsAreDropped()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HistoryStore(root);
            var invalid = new ClipboardRecord { Kind = ClipboardRecordKind.Text, Text = "broken", ContentHash = "short" };
            var valid = new ClipboardRecord { Kind = ClipboardRecordKind.Text, Text = "kept", ContentHash = new string('A', 64), ByteLength = 4 };
            await store.SaveAsync(new HistoryDocument { Records = [valid, invalid] });
            string original = File.ReadAllText(store.HistoryFilePath);

            var history = new HistoryService(store);
            string? warning = await history.LoadAsync();

            string backup = Assert.Single(Directory.GetFiles(root, "history.skipped-*.json"));
            Assert.Contains(Path.GetFileName(backup), warning);
            Assert.Equal(valid.Id, Assert.Single(history.Records).Id);

            await history.AddAsync(ClipboardSnapshot.FromText("new"));
            Assert.DoesNotContain(invalid.Id.ToString(), File.ReadAllText(store.HistoryFilePath));
            Assert.Equal(original, File.ReadAllText(backup));

            // A second launch over the same file must not create another copy.
            File.WriteAllText(store.HistoryFilePath, original);
            await new HistoryService(store).LoadAsync();
            Assert.Single(Directory.GetFiles(root, "history.skipped-*.json"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
