namespace ClipboardManager.Core.Clipboard;

public interface IClipboardReader
{
    Task<ClipboardReadResult> ReadAsync(CancellationToken cancellationToken);
}
