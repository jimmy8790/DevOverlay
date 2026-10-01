using System.Windows;
using DevOverlay.Configuration;

namespace DevOverlay.Platform.Windows;

/// <summary>
/// Calculates overlay placement entirely in WPF device-independent units.
/// Keeping the calculation separate makes saved-position recovery testable without a Window.
/// </summary>
internal static class OverlayPlacementCalculator
{
    public static bool TryCalculate(
        Rect workArea,
        System.Windows.Size windowSize,
        OverlayPosition position,
        out System.Windows.Point placement)
    {
        placement = default;
        if (!IsUsableWorkArea(workArea) || !IsUsableWindowSize(windowSize))
        {
            return false;
        }

        var horizontalOffset = ToSafeOffset(position.OffsetX);
        var verticalOffset = ToSafeOffset(position.OffsetY);
        var desired = position.Mode == OverlayPositionMode.Custom
            ? new System.Windows.Point(workArea.Left + horizontalOffset, workArea.Top + verticalOffset)
            : position.Anchor switch
        {
            OverlayAnchor.TopLeft => new System.Windows.Point(workArea.Left + horizontalOffset, workArea.Top + verticalOffset),
            OverlayAnchor.TopRight => new System.Windows.Point(workArea.Right - windowSize.Width - horizontalOffset, workArea.Top + verticalOffset),
            OverlayAnchor.BottomLeft => new System.Windows.Point(workArea.Left + horizontalOffset, workArea.Bottom - windowSize.Height - verticalOffset),
            OverlayAnchor.BottomRight => new System.Windows.Point(
                workArea.Right - windowSize.Width - horizontalOffset,
                workArea.Bottom - windowSize.Height - verticalOffset),
            _ => new System.Windows.Point(workArea.Right - windowSize.Width - horizontalOffset, workArea.Top + verticalOffset)
        };

        // If the window is larger than the usable area, its complete bounds cannot fit.
        // Pinning its origin to the work-area origin leaves the largest possible visible portion.
        var maximumLeft = Math.Max(workArea.Left, workArea.Right - windowSize.Width);
        var maximumTop = Math.Max(workArea.Top, workArea.Bottom - windowSize.Height);
        placement = new System.Windows.Point(
            Math.Clamp(desired.X, workArea.Left, maximumLeft),
            Math.Clamp(desired.Y, workArea.Top, maximumTop));
        return true;
    }

    private static bool IsUsableWorkArea(Rect workArea) =>
        double.IsFinite(workArea.Left) &&
        double.IsFinite(workArea.Top) &&
        double.IsFinite(workArea.Width) &&
        double.IsFinite(workArea.Height) &&
        workArea.Width > 0 &&
        workArea.Height > 0;

    private static bool IsUsableWindowSize(System.Windows.Size windowSize) =>
        double.IsFinite(windowSize.Width) &&
        double.IsFinite(windowSize.Height) &&
        windowSize.Width > 0 &&
        windowSize.Height > 0;

    private static double ToSafeOffset(double offset) => double.IsFinite(offset) ? Math.Max(0, offset) : 0;
}
