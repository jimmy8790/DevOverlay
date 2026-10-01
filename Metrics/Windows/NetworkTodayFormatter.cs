namespace DevOverlay.Metrics.Windows;

internal static class NetworkTodayFormatter
{
    public static MetricSnapshot CreateSnapshot(ulong? bytes, DateTimeOffset timestamp)
    {
        if (bytes is null) return MetricSnapshot.Unavailable(MetricId.NetworkTodayTotal, MetricCategory.Network, "오늘 합계", "");
        var value = (double)bytes.Value;
        string unit;
        if (value >= 1024d * 1024 * 1024 * 1024) { value /= 1024d * 1024 * 1024 * 1024; unit = "TB"; }
        else if (value >= 1024d * 1024 * 1024) { value /= 1024d * 1024 * 1024; unit = "GB"; }
        else if (value >= 1024d * 1024) { value /= 1024d * 1024; unit = "MB"; }
        else if (value >= 1024) { value /= 1024d; unit = "KB"; }
        else unit = "B";
        value = value < 10 && unit != "B" ? Math.Round(value, 1) : Math.Round(value);
        return new MetricSnapshot(MetricId.NetworkTodayTotal, MetricCategory.Network, "오늘 합계", value, unit, true, timestamp);
    }
}
