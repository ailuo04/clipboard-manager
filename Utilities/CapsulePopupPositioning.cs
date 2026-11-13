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
        double below = Math.Max(0, workArea.Bottom - target.Bottom - PopupGap);
        double above = Math.Max(0, target.Top - workArea.Top - PopupGap);
        bool useBelow = below >= preferredSize.Height || (above < preferredSize.Height && below >= above);
        PlacementMode placement = useBelow ? PlacementMode.Bottom : PlacementMode.Top;
        double availableHeight = useBelow ? below : above;
        double left = Math.Clamp(target.Left, workArea.Left, workArea.Right - width);

        return new CapsulePopupLayout(placement, width, Math.Clamp(availableHeight, 0, preferredSize.Height), left - target.Left);
    }
}
