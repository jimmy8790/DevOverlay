namespace DevOverlay.Metrics;

/// <summary>Providers collect one independent category or integration without referencing any UI types.</summary>
public interface IMetricProvider
{
    string Name { get; }
    TimeSpan RefreshInterval { get; }
    /// <summary>True when a provider deliberately owns a slower cache/query cadence than the live HUD setting.</summary>
    bool UsesFixedRefreshInterval => false;
    Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken);
}
