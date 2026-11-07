using System.Windows.Controls;

namespace ClipboardManager;

internal abstract class OverlayView : UserControl
{
    internal Action<bool?>? CloseRequest { get; set; }

    internal virtual string? PageTitle => null;

    internal virtual void OnDismissed(bool? result) { }

    protected void Complete(bool? result) => CloseRequest?.Invoke(result);
}
