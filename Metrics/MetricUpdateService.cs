namespace DevOverlay.Metrics;

/// <summary>Runs each provider on its own periodic background task, isolating provider failures from the overlay.</summary>
public sealed class MetricUpdateService : IAsyncDisposable
{
    private readonly IReadOnlyCollection<IMetricProvider> _providers;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly List<Task> _providerTasks = [];
    private readonly object _scheduleGate = new();
    private ScheduleVersion _scheduleVersion;
    private bool _started;

    public MetricUpdateService(IReadOnlyCollection<IMetricProvider> providers, TimeSpan refreshInterval)
    {
        _providers = providers;
        _scheduleVersion = new ScheduleVersion(refreshInterval);
    }

    public event Action<IReadOnlyCollection<MetricSnapshot>>? MetricsUpdated;
    public event Action<IMetricProvider, Exception>? ProviderFaulted;
    public IReadOnlyCollection<IMetricProvider> Providers => _providers;
    public TimeSpan RefreshInterval
    {
        get { lock (_scheduleGate) return _scheduleVersion.Interval; }
    }

    /// <summary>Wakes normal provider loops without recreating them or their backend state.</summary>
    public void SetRefreshInterval(TimeSpan refreshInterval)
    {
        if (refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        lock (_scheduleGate)
        {
            if (_scheduleVersion.Interval == refreshInterval) return;
            var previous = _scheduleVersion;
            _scheduleVersion = new ScheduleVersion(refreshInterval);
            previous.Cancellation.Cancel();
            if (previous.WaiterCount == 0) previous.Cancellation.Dispose();
        }
    }

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
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                FrameCaptureActivity.Mark(provider.Name);
                var metrics = await Task.Run(
                    () => provider.CollectAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (metrics.Count > 0)
                {
                    FrameCaptureActivity.Mark("MetricsUpdated");
                    MetricsUpdated?.Invoke(metrics);
                }
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
            if (provider.UsesFixedRefreshInterval)
            {
                await Task.Delay(provider.RefreshInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var schedule = AcquireSchedule();
            try
            {
                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, schedule.Cancellation.Token);
                await Task.Delay(schedule.Interval, delayCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Settings changed: the next iteration reads the new cadence without starting another loop.
            }
            finally { ReleaseSchedule(schedule); }
        }
    }

    private ScheduleVersion AcquireSchedule()
    {
        lock (_scheduleGate)
        {
            _scheduleVersion.WaiterCount++;
            return _scheduleVersion;
        }
    }

    private void ReleaseSchedule(ScheduleVersion schedule)
    {
        lock (_scheduleGate)
        {
            schedule.WaiterCount--;
            if (schedule.WaiterCount == 0 && !ReferenceEquals(schedule, _scheduleVersion))
                schedule.Cancellation.Dispose();
        }
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
        lock (_scheduleGate) _scheduleVersion.Cancellation.Dispose();
    }

    private sealed class ScheduleVersion(TimeSpan interval)
    {
        public TimeSpan Interval { get; } = interval;
        public CancellationTokenSource Cancellation { get; } = new();
        public int WaiterCount { get; set; }
    }
}
