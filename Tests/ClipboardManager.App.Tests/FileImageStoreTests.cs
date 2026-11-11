using ClipboardManager.App.Utilities;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class FileImageStoreTests
{
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task Save_and_delete_uses_only_the_record_id_and_preserves_the_complete_png()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-image-store-{Guid.NewGuid():N}");
        var store = new FileImageStore(root);
        Guid id = Guid.NewGuid();
        byte[] png = Convert.FromBase64String(OnePixelPng);
        try
        {
            await store.SavePngAsync(id, png, CancellationToken.None);
            string path = store.GetPath($"{id:N}.png");

            Assert.Equal(png, await File.ReadAllBytesAsync(path));
            await store.DeleteAsync($"{id:N}.png", CancellationToken.None);
            Assert.False(File.Exists(path));
            await store.DeleteAsync($"{id:N}.png", CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_rejects_non_png_payloads_without_creating_an_image_file()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-image-store-{Guid.NewGuid():N}");
        var store = new FileImageStore(root);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SavePngAsync(Guid.NewGuid(), new byte[] { 1, 2, 3 }, CancellationToken.None));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetPath_and_delete_reject_path_traversal_and_non_generated_names()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cm-image-store-{Guid.NewGuid():N}");
        var store = new FileImageStore(root);

        Assert.Throws<InvalidDataException>(() => store.GetPath("..\\history.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.DeleteAsync("C:\\user-data\\image.png", CancellationToken.None));
    }
}