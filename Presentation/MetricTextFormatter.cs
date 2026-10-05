using DevOverlay.Metrics;

namespace DevOverlay.Presentation;

/// <summary>
/// Single place that turns typed snapshots into HUD text. Providers keep exposing typed
/// availability; only this layer renders the unavailable marker.
/// </summary>
internal static class MetricTextFormatter
{
    public const string UnavailableMarker = "N/A";

    public static string Format(MetricSnapshot metric)
    {
        var prefix = GetPrefix(metric);
        return prefix.Length == 0 ? FormatValue(metric) : $"{prefix} {FormatValue(metric)}";
    }

    public static string GetPrefix(MetricSnapshot metric) => metric.Id is MetricId.CodexPrimaryRateLimit or MetricId.CodexSecondaryRateLimit or
        MetricId.ClaudePrimaryRateLimit or MetricId.ClaudeSecondaryRateLimit or MetricId.PeripheralBattery
        ? metric.DisplayName
        : GetPrefix(metric.Id);

    public static string GetPrefix(MetricId id) => id switch
    {
        MetricId.NetworkDownload => "↓",
        MetricId.NetworkUpload => "↑",
        MetricId.NetworkTodayTotal => "DAY",
        MetricId.StorageRead => "R",
        MetricId.StorageWrite => "W",
        MetricId.OnePercentLow => "1%",
        MetricId.FrameTime => "FT",
        _ => string.Empty
    };

    public static string FormatValue(MetricSnapshot metric) =>
        !metric.IsAvailable || metric.Value is not { } value || !double.IsFinite(value)
            ? UnavailableMarker
            : $"{FormatNumber(value, metric.Id)}{metric.Unit}";

    // Labelled metrics keep their label while unavailable; other metrics rely on the group title.
    private static string FormatNumber(double value, MetricId id) =>
        id is MetricId.FramesPerSecond or MetricId.OnePercentLow or MetricId.PeripheralBattery ? value.ToString("0") :
        id is MetricId.FrameTime or MetricId.Latency ? value.ToString("0.0") :
        value % 1 == 0 ? value.ToString("0") : value.ToString("0.0");

}
