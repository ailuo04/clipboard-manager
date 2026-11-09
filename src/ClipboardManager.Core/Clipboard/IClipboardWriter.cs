namespace ClipboardManager.Core.Clipboard;

public interface IClipboardWriter
{
    Task WriteTextAsync(string text, CancellationToken cancellationToken);
    Task WriteImageAsync(string imagePath, CancellationToken cancellationToken);
}
