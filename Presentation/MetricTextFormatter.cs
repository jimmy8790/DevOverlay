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
        MetricId.ClaudePrimaryRateLimit or MetricId.ClaudeSecondaryRateLimit or MetricId.PeripheralBattery or MetricId.BatteryRemaining
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

    public static string FormatValue(MetricSnapshot metric)
    {
        if (metric.Id == MetricId.BatteryPower && metric.DisplayName == "AC") return "AC";
        if (!metric.IsAvailable || metric.Value is not { } value || !double.IsFinite(value)) return UnavailableMarker;
        if (metric.Id == MetricId.BatteryRemaining)
        {
            var minutes = (int)Math.Clamp(Math.Floor(value / 60), 0, 5999);
            return minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";
        }
        if (metric.Id == MetricId.BatteryPower) return FormatSignedWatts(value) + metric.Unit;
        return $"{FormatNumber(value, metric.Id)}{metric.Unit}";
    }

    // 부호가 방향 라벨을 대신하므로 +를 항상 표시한다. 반올림 후 0이면 부호 없이 0.0으로 정규화한다.
    internal static string FormatSignedWatts(double value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        if (rounded == 0) return 0d.ToString("0.0");
        return (rounded > 0 ? "+" : "-") + Math.Abs(rounded).ToString("0.0");
    }

    // Labelled metrics keep their label while unavailable; other metrics rely on the group title.
    private static string FormatNumber(double value, MetricId id) =>
        id is MetricId.FramesPerSecond or MetricId.OnePercentLow or MetricId.PeripheralBattery or MetricId.BatteryCharge ? value.ToString("0") :
        id is MetricId.FrameTime or MetricId.Latency or MetricId.BatteryPower ? value.ToString("0.0") :
        value % 1 == 0 ? value.ToString("0") : value.ToString("0.0");

}
