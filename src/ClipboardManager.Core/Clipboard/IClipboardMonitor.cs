namespace ClipboardManager.Core.Clipboard;

public interface IClipboardMonitor : IDisposable
{
    event EventHandler? ClipboardChanged;
    void Start();
}
