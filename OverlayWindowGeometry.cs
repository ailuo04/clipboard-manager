using System;
using System.Windows;

namespace ClipboardManager;

internal static class OverlayWindowGeometry
{
    internal static Rect Fit(Rect workArea, Size desired, double margin)
    {
        double effectiveMargin = Math.Max(0, margin);
        double availableWidth = Math.Max(0, workArea.Width - 2 * effectiveMargin);
        double availableHeight = Math.Max(0, workArea.Height - 2 * effectiveMargin);
        double width = Math.Min(Math.Max(0, desired.Width), availableWidth);
        double height = Math.Min(Math.Max(0, desired.Height), availableHeight);
        double left = workArea.Left + (workArea.Width - width) / 2;
        double top = workArea.Top + (workArea.Height - height) / 2;

        return new Rect(left, top, width, height);
    }
}
