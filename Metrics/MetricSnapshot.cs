namespace DevOverlay.Metrics;

// IsApplicable은 장치 없음과 일시적인 측정 불가를 구분한다. 기존 지표는 기본 true.
public sealed record MetricSnapshot(
    MetricId Id,
    MetricCategory Category,
    string DisplayName,
    double? Value,
    string Unit,
    bool IsAvailable,
    DateTimeOffset UpdatedAt,
    bool IsApplicable = true)
{
    public static MetricSnapshot Unavailable(MetricId id, MetricCategory category, string displayName, string unit) =>
        new(id, category, displayName, null, unit, false, DateTimeOffset.UtcNow);
}
