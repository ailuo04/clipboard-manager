using System.Windows;
using System.Windows.Controls.Primitives;

namespace ClipboardManager.Utilities;

internal readonly record struct CapsulePopupLayout(
    PlacementMode Placement,
    double Width,
    double MaxHeight,
    double HorizontalOffset);

internal static class CapsulePopupPositioning
{
    private const double PopupGap = 4;

    internal static CapsulePopupLayout Calculate(Rect workArea, Rect target, Size preferredSize)
    {
        double width = Math.Min(preferredSize.Width, workArea.Width);
        bool fitsBelow = workArea.Bottom - target.Bottom >= preferredSize.Height + PopupGap;
        PlacementMode placement = fitsBelow ? PlacementMode.Bottom : PlacementMode.Top;
        double availableHeight = placement == PlacementMode.Bottom
            ? workArea.Bottom - target.Bottom - PopupGap
            : target.Top - workArea.Top - PopupGap;
        double left = Math.Clamp(target.Left, workArea.Left, workArea.Right - width);

        return new CapsulePopupLayout(placement, width, Math.Clamp(availableHeight, 0, preferredSize.Height), left - target.Left);
    }
}
