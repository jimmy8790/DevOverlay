namespace DevOverlay.Configuration;

public enum OverlayAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum OverlayPositionMode
{
    AutoTopRight,
    Custom
}

public enum PopupBehavior
{
    Click,
    Hover,
    Disabled
}

/// <summary>Custom offsets are WPF DIPs from the saved monitor's work-area origin.</summary>
public sealed record OverlayPosition(
    string MonitorId,
    OverlayAnchor Anchor,
    double OffsetX,
    double OffsetY,
    OverlayPositionMode Mode = OverlayPositionMode.AutoTopRight)
{
    public static OverlayPosition CreateDefault() => new("PrimaryDisplay", OverlayAnchor.TopRight, 16, 16);

    /// <summary>Offsets are WPF DIPs from the primary work area's top-left corner.</summary>
    public static OverlayPosition CreateCustom(double offsetX, double offsetY) =>
        new("PrimaryDisplay", OverlayAnchor.TopLeft, offsetX, offsetY, OverlayPositionMode.Custom);

    public static OverlayPosition CreateCustom(string monitorId, double offsetX, double offsetY) =>
        new(monitorId, OverlayAnchor.TopLeft, offsetX, offsetY, OverlayPositionMode.Custom);
}
