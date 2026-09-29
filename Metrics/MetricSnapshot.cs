namespace DevOverlay.Metrics;

public sealed record MetricSnapshot(
    MetricId Id,
    MetricCategory Category,
    string DisplayName,
    double? Value,
    string Unit,
    bool IsAvailable,
    DateTimeOffset UpdatedAt)
{
    public static MetricSnapshot Unavailable(MetricId id, MetricCategory category, string displayName, string unit) =>
        new(id, category, displayName, null, unit, false, DateTimeOffset.UtcNow);
}
