using System.Windows;
using DevOverlay.Configuration;

namespace DevOverlay.Platform.Windows;

/// <summary>Uses WPF's device-independent working area; future monitor selection can resolve MonitorId here.</summary>
public sealed class OverlayPositioningService
{
    public void Apply(Window window, OverlayPosition position)
    {
        var workArea = SystemParameters.WorkArea;
        (window.Left, window.Top) = position.Anchor switch
        {
            OverlayAnchor.TopLeft => (workArea.Left + position.OffsetX, workArea.Top + position.OffsetY),
            OverlayAnchor.TopRight => (workArea.Right - window.ActualWidth - position.OffsetX, workArea.Top + position.OffsetY),
            OverlayAnchor.BottomLeft => (workArea.Left + position.OffsetX, workArea.Bottom - window.ActualHeight - position.OffsetY),
            OverlayAnchor.BottomRight => (workArea.Right - window.ActualWidth - position.OffsetX, workArea.Bottom - window.ActualHeight - position.OffsetY),
            _ => (workArea.Right - window.ActualWidth - position.OffsetX, workArea.Top + position.OffsetY)
        };
    }
}
