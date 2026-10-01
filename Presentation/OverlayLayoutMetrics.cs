using DevOverlay.Configuration;

namespace DevOverlay.Presentation;

/// <summary>
/// Pure mapping from persisted Spacing to horizontal WPF layout values in DIPs.
/// It is evaluated only when appearance settings change, never per telemetry sample.
/// Font and vertical dimensions deliberately remain fixed; normal WPF DPI behavior is unaffected.
/// </summary>
internal sealed record OverlayLayoutMetrics(
    double ValueFontSize,
    double LabelFontSize,
    double BorderPaddingX,
    double BorderPaddingY,
    double CornerRadius,
    double SeparatorHeight,
    double LabelGap,
    double PrefixValueGap,
    double MetricGap,
    double GroupPaddingLeft,
    double GroupPaddingRight,
    double GroupPaddingY,
    double SeparatorMarginLeft,
    double SeparatorMarginRight)
{
    private const double MinimumGap = 1;

    public static OverlayLayoutMetrics Create(OverlayAppearance appearance)
    {
        var normalized = appearance.Normalize();
        var spacing = normalized.Spacing;
        return new OverlayLayoutMetrics(
            ValueFontSize: 12,
            LabelFontSize: 11,
            BorderPaddingX: 8,
            BorderPaddingY: 5,
            CornerRadius: 7,
            SeparatorHeight: 14,
            // Group title doubles as the first metric's prefix (FPS/LAT/CPU); both inner gaps stay fixed.
            LabelGap: 2,
            PrefixValueGap: 2,
            MetricGap: HorizontalGap(spacing, 1, 4, 8),
            GroupPaddingLeft: HorizontalGap(spacing, 2, 4, 9),
            GroupPaddingRight: HorizontalGap(spacing, 2, 4, 9),
            GroupPaddingY: 1,
            SeparatorMarginLeft: HorizontalGap(spacing, 2, 5, 10),
            SeparatorMarginRight: HorizontalGap(spacing, 2, 5, 10));
    }

    // The midpoint matches the compact default. Explicit anchors make the full slider range visible.
    private static double HorizontalGap(double spacing, double compact, double normal, double wide) =>
        Math.Max(MinimumGap, Snap(spacing <= OverlayAppearance.DefaultSpacing
            ? Interpolate(OverlayAppearance.MinSpacing, compact, OverlayAppearance.DefaultSpacing, normal, spacing)
            : Interpolate(OverlayAppearance.DefaultSpacing, normal, OverlayAppearance.MaxSpacing, wide, spacing)));

    private static double Interpolate(double leftX, double leftY, double rightX, double rightY, double x) =>
        leftY + (rightY - leftY) * ((x - leftX) / (rightX - leftX));

    private static double Snap(double value) => Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;
}
