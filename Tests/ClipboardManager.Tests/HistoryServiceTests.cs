using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Models;
using ClipboardManager.Services;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class HistoryServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FavoriteStateSurvivesDuplicateCopiesAndReload(bool image)
    {
        string root = Path.Combine(Path.GetTempPath(), "clipboard-favorite-dedup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new HistoryService(new HistoryStore(root));
            await history.LoadAsync();
            var first = (await history.AddAsync(Snapshot(image))).Record!;
            await history.TogglePinAsync(first.Id);
            var repeated = (await history.AddAsync(Snapshot(image))).Record!;
            Assert.Equal(first.Id, repeated.Id);
            Assert.True(repeated.IsPinned);
            Assert.True(repeated.CopiedAtUtc >= first.CopiedAtUtc);
            Assert.Single(history.Records);

            var reloaded = new HistoryService(new HistoryStore(root));
            await reloaded.LoadAsync();
            Assert.Equal(first.Id, Assert.Single(reloaded.Records).Id);
            Assert.True(reloaded.Records[0].IsPinned);
            await reloaded.TogglePinAsync(first.Id);
            await history.LoadAsync();
            Assert.False(Assert.Single(history.Records).IsPinned);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticRetentionProtectsFavoritesUntilTheyAreUnfavorited(bool image)
    {
        string root = Path.Combine(Path.GetTempPath(), "clipboard-favorite-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new HistoryService(new HistoryStore(root));
            await history.LoadAsync();
            var settings = history.Settings;
            settings.MaxRecords = 50;
            await history.UpdateSettingsAsync(settings);
            var favorite = (await history.AddAsync(Snapshot(image))).Record!;
            await history.TogglePinAsync(favorite.Id);
            for (int index = 0; index < 51; index++)
                await history.AddAsync(ClipboardSnapshot.FromText($"history-{index}"));

            Assert.Equal(50, history.Records.Count);
            Assert.Contains(history.Records, record => record.Id == favorite.Id && record.IsPinned);
            Assert.DoesNotContain(history.Records, record => record.Text is "history-0" or "history-1");
            if (image) Assert.True(File.Exists(history.GetImagePath(favorite)));

            await history.TogglePinAsync(favorite.Id);
            await history.AddAsync(ClipboardSnapshot.FromText("next"));
            Assert.Equal(50, history.Records.Count);
            Assert.DoesNotContain(history.Records, record => record.Id == favorite.Id);
            if (image) Assert.False(File.Exists(history.GetImagePath(favorite)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static ClipboardSnapshot Snapshot(bool image)
    {
        if (!image) return ClipboardSnapshot.FromText("frequently used");
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
        bitmap.Freeze();
        return ClipboardSnapshot.FromImage(bitmap, "FavoriteTest");
    }

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
