namespace DevOverlay.Metrics;

/// <summary>Providers collect one independent category or integration without referencing any UI types.</summary>
public interface IMetricProvider
{
    string Name { get; }
    TimeSpan RefreshInterval { get; }
    Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken);
}
