using System.Windows;

namespace ClipboardManager.Utilities;

internal enum HoverPreviewSide
{
    Left,
    Right
}

internal readonly record struct HoverPreviewLayout(
    double Left,
    double Top,
    double Width,
    double Height,
    HoverPreviewSide Side);

internal static class HoverPreviewPositioning
{
    internal const double DefaultMargin = 8;
    internal const double DefaultGap = 10;

    /// <summary>
    /// Places the preview beside the record, preferring its left side. A preview that
    /// cannot fit on the left flips to the right; both axes stay inside the work area.
    /// </summary>
    internal static HoverPreviewLayout Calculate(Rect workArea, Rect target, Size preferredSize,
        double margin = DefaultMargin, double gap = DefaultGap)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
            return new HoverPreviewLayout(workArea.Left, workArea.Top, 0, 0, HoverPreviewSide.Left);

        margin = Math.Max(0, margin);
        gap = Math.Max(0, gap);
        double maxWidth = Math.Max(0, workArea.Width - margin * 2);
        double maxHeight = Math.Max(0, workArea.Height - margin * 2);
        double width = Math.Min(Math.Max(0, preferredSize.Width), maxWidth);
        double height = Math.Min(Math.Max(0, preferredSize.Height), maxHeight);

        double leftSpace = Math.Max(0, target.Left - workArea.Left - margin - gap);
        double rightSpace = Math.Max(0, workArea.Right - target.Right - margin - gap);

        HoverPreviewSide side;
        if (leftSpace >= width) side = HoverPreviewSide.Left;
        else if (rightSpace >= width) side = HoverPreviewSide.Right;
        else side = leftSpace > rightSpace ? HoverPreviewSide.Left : HoverPreviewSide.Right;

        double available = side == HoverPreviewSide.Left ? leftSpace : rightSpace;
        double placedWidth = Math.Min(width, available);
        double left = side == HoverPreviewSide.Left
            ? target.Left - gap - placedWidth
            : target.Right + gap;
        left = Math.Clamp(left, workArea.Left + margin,
            Math.Max(workArea.Left + margin, workArea.Right - margin - placedWidth));

        double top = target.Top + (target.Height - height) / 2;
        top = Math.Clamp(top, workArea.Top + margin,
            Math.Max(workArea.Top + margin, workArea.Bottom - margin - height));

        return new HoverPreviewLayout(left, top, placedWidth, height, side);
    }
}
