namespace DevOverlay.Metrics.Demo;

/// <summary>Phase-1 sample data. Replace this provider with real providers without changing presentation code.</summary>
public sealed class DemoMetricProvider : IMetricProvider
{
    public string Name => "Demo metrics";
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(2);

    public Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyCollection<MetricSnapshot> metrics =
        [
            new(MetricId.CpuTemperature, MetricCategory.Cpu, "CPU 온도", 70, "°C", true, now),
            new(MetricId.FramesPerSecond, MetricCategory.Frame, "FPS", 144, "", true, now),
            new(MetricId.OnePercentLow, MetricCategory.Frame, "1% Low", 118, "", true, now),
            new(MetricId.FrameTime, MetricCategory.Frame, "프레임 시간", 6.9, "ms", true, now),
            new(MetricId.Latency, MetricCategory.Latency, "지연 시간", 14, "ms", true, now)
        ];
        return Task.FromResult(metrics);
    }
}
