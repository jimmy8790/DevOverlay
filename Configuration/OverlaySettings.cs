using DevOverlay.Metrics;

namespace DevOverlay.Configuration;

/// <summary>Settings intentionally remain UI-independent so they can later be persisted.</summary>
public sealed class OverlaySettings
{
    public bool IsVisible { get; init; } = true;
    public OverlayPosition Position { get; init; } = OverlayPosition.CreateDefault();
    public PopupBehavior PopupBehavior { get; init; } = PopupBehavior.Disabled;
    public IReadOnlySet<MetricCategory> EnabledGroups { get; init; } = new HashSet<MetricCategory>();
    public IReadOnlySet<MetricId> EnabledMetrics { get; init; } = new HashSet<MetricId>();

    public static OverlaySettings CreateDefault() => new()
    {
        EnabledGroups = new HashSet<MetricCategory>
        {
            MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Frame, MetricCategory.Latency
        },
        EnabledMetrics = new HashSet<MetricId>
        {
            MetricId.CpuUtilization, MetricId.CpuTemperature,
            MetricId.GpuUtilization, MetricId.GpuTemperature, MetricId.GpuPower, MetricId.GpuVramUsed,
            MetricId.FramesPerSecond, MetricId.OnePercentLow, MetricId.FrameTime, MetricId.Latency
        }
    };
}
