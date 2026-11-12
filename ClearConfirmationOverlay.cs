using System.Windows;

namespace ClipboardManager;

internal sealed class ClearConfirmationOverlay : OverlayView
{
    internal ClearConfirmationOverlay(int recordCount, int pinnedCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        ArgumentOutOfRangeException.ThrowIfNegative(pinnedCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pinnedCount, recordCount);

        var view = new Views.ClearConfirmationView();
        Content = view;
        view.RecordSummaryText.Text = $"将清空全部 {recordCount} 条记录，其中 {pinnedCount} 条固定记录。";
        view.CancelButton.Click += (_, _) => Complete(false);
        view.ClearButton.Click += (_, _) => Complete(true);
    }

    internal override string? PageTitle => "清空历史记录";
}
