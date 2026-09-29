namespace DevOverlay.Configuration;

public enum OverlayAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum PopupBehavior
{
    Click,
    Hover,
    Disabled
}

/// <summary>Coordinates are WPF device-independent pixels; monitor identity is reserved for multi-monitor settings.</summary>
public sealed record OverlayPosition(string MonitorId, OverlayAnchor Anchor, double OffsetX, double OffsetY)
{
    public static OverlayPosition CreateDefault() => new("PrimaryDisplay", OverlayAnchor.TopRight, 16, 16);
}
