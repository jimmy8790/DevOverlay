using System.Collections.Concurrent;
using System.Globalization;
using DevOverlay.Metrics;

namespace DevOverlay.Presentation;

/// <summary>
/// Automatically measures compact, stable per-metric text fields using the
/// actual WPF value font instead of maintaining hard-coded DIP widths.
///
/// Widths are based on representative maximum display strings, not the current
/// telemetry value, so live telemetry changes do not resize the HUD.
/// </summary>
internal static class MetricDisplayLayout
{
    // Must match the HUD value font in OverlayLayoutMetrics / OverlayWindow.
    private const double ValueFontSize = 12;

    // Small extra room for glyph rendering and rounding.
    private const double SafetyPadding = 2;

    private static readonly System.Windows.Media.FontFamily ValueFontFamily =
        new("Segoe UI");

    private static readonly System.Windows.Media.Typeface ValueTypeface =
        new(
            ValueFontFamily,
            System.Windows.FontStyles.Normal,
            System.Windows.FontWeights.SemiBold,
            System.Windows.FontStretches.Normal);

    private static readonly ConcurrentDictionary<MetricId, double> WidthCache = new();

    /// <summary>
    /// Returns a stable automatically measured width for the metric value field.
    /// </summary>
    public static double GetTextWidth(MetricId id) =>
        WidthCache.GetOrAdd(id, MeasureMetricWidth);

    private static double MeasureMetricWidth(MetricId id)
    {
        var representative = GetRepresentativeValue(id);

        var representativeWidth = MeasureText(representative);
        var unavailableWidth = MeasureText("N/A");

        return Math.Ceiling(
            Math.Max(representativeWidth, unavailableWidth)
            + SafetyPadding);
    }

    /// <summary>
    /// Representative maximum or near-maximum formatted values.
    ///
    /// These strings define stable semantic field sizes.
    /// The actual WPF text width is measured automatically.
    /// </summary>
    private static string GetRepresentativeValue(MetricId id) => id switch
    {
        // Utilization is rendered with one decimal whenever it is non-integral.
        // "100%" alone does not reserve enough space for values such as 10.1%.
        MetricId.CpuUtilization => "100.0%",
        MetricId.GpuUtilization => "100.0%",

        MetricId.CpuTemperature => "105°C",
        MetricId.GpuTemperature => "105°C",

        MetricId.CpuPower => "999.9W",
        MetricId.GpuPower => "999.9W",

        MetricId.GpuVramUsed => "99.9GB",

        MetricId.NetworkDownload => "999.9MB/s",
        MetricId.NetworkUpload => "999.9MB/s",

        // DAY prefix is outside the value field.
        MetricId.NetworkTodayTotal => "1023GB",

        MetricId.StorageRead => "999.9MB/s",
        MetricId.StorageWrite => "999.9MB/s",

        MetricId.FramesPerSecond => "999",
        MetricId.OnePercentLow => "999",

        MetricId.FrameTime => "999.9ms",
        MetricId.Latency => "999.9ms",

        MetricId.CodexPrimaryRateLimit => "100%",
        MetricId.CodexSecondaryRateLimit => "100%",
        MetricId.ClaudePrimaryRateLimit => "100%",
        MetricId.ClaudeSecondaryRateLimit => "100%",

        _ => "N/A"
    };

    private static double MeasureText(string text)
    {
        var formattedText = new System.Windows.Media.FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            ValueTypeface,
            ValueFontSize,
            System.Windows.Media.Brushes.White,
            pixelsPerDip: 1.0);

        return formattedText.WidthIncludingTrailingWhitespace;
    }
}
