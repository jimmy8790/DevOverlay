namespace DevOverlay.Metrics;

/// <summary>Runs each provider on its own periodic background task, isolating provider failures from the overlay.</summary>
public sealed class MetricUpdateService : IAsyncDisposable
{
    private readonly IReadOnlyCollection<IMetricProvider> _providers;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly List<Task> _providerTasks = [];
    private bool _started;

    public MetricUpdateService(IReadOnlyCollection<IMetricProvider> providers)
    {
        _providers = providers;
    }

    public event Action<IReadOnlyCollection<MetricSnapshot>>? MetricsUpdated;
    public event Action<IMetricProvider, Exception>? ProviderFaulted;

    public void Start()
    {
        if (_started) return;
        _started = true;
        foreach (var provider in _providers)
        {
            _providerTasks.Add(RunProviderAsync(provider, _cancellationTokenSource.Token));
        }
    }

    private async Task RunProviderAsync(IMetricProvider provider, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(provider.RefreshInterval);
        do
        {
            try
            {
                var metrics = await Task.Run(
                    () => provider.CollectAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                MetricsUpdated?.Invoke(metrics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A provider must not end the overlay, but its failure remains observable to the host.
                ProviderFaulted?.Invoke(provider, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        try { await Task.WhenAll(_providerTasks); }
        catch (OperationCanceledException) { }
        foreach (var provider in _providers.OfType<IAsyncDisposable>())
        {
            await provider.DisposeAsync();
        }

        _cancellationTokenSource.Dispose();
    }
}
