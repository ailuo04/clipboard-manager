using ClipboardManager.Core.History;

namespace ClipboardManager.App.Utilities;

/// <summary>Atomically stores PNG files using application-generated names inside one image directory.</summary>
public sealed class FileImageStore : IImageStore
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly string _directory;

    public FileImageStore(string directory) => _directory = Path.GetFullPath(directory);

    public async Task SavePngAsync(Guid id, ReadOnlyMemory<byte> png, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) throw new ArgumentException("Image ID must not be empty.", nameof(id));
        if (png.Length < PngSignature.Length || !png.Span[..PngSignature.Length].SequenceEqual(PngSignature))
            throw new InvalidDataException("Image payload is not a PNG.");
        Directory.CreateDirectory(_directory);
        string target = GetPath($"{id:N}.png");
        string temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(png, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task DeleteAsync(string fileName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetPath(fileName);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public string GetPath(string fileName)
    {
        if (fileName is null || fileName.Length != 36 || !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(fileName[..32], "N", out Guid id) || id == Guid.Empty)
            throw new InvalidDataException("Image file name is invalid.");
        return Path.Combine(_directory, $"{id:N}.png");
    }
}