namespace ClipboardManager.Core.History;

/// <summary>
/// Stores PNG data under an application-generated record ID. Callers cannot supply arbitrary file paths.
/// Implementations must publish a complete PNG atomically so failures do not expose a partial image.
/// </summary>
public interface IImageStore
{
    /// <summary>Atomically stores the complete PNG as {id:N}.png.</summary>
    Task SavePngAsync(Guid id, ReadOnlyMemory<byte> png, CancellationToken cancellationToken);

    /// <summary>Deletes only a valid application image name; deleting a missing file succeeds.</summary>
    Task DeleteAsync(string fileName, CancellationToken cancellationToken);

    /// <summary>Resolves a valid image name within the configured image directory.</summary>
    string GetPath(string fileName);
}
