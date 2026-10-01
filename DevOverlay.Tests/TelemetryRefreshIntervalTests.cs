using DevOverlay.Metrics;
using DevOverlay.Configuration;
using DevOverlay.Metrics.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class TelemetryRefreshIntervalTests
{
    [Fact]
    public async Task ChangingCadenceWakesTheExistingSingleProviderLoopWithoutOverlap()
    {
        var provider = new RecordingProvider();
        await using var service = new MetricUpdateService([provider], TimeSpan.FromMilliseconds(200));
        service.Start();
        await provider.WaitForCallsAsync(1);

        service.SetRefreshInterval(TimeSpan.FromMilliseconds(20));
        await provider.WaitForCallsAsync(2);
        service.SetRefreshInterval(TimeSpan.FromMilliseconds(80));
        Assert.Equal(TimeSpan.FromMilliseconds(80), service.RefreshInterval);
        await provider.WaitForCallsAsync(3);

        Assert.Equal(1, provider.MaximumConcurrentCollections);
        Assert.Equal(1, provider.CreatedInstances);
    }

    [Fact]
    public async Task FixedCadenceProviderIsNotWokenByLiveRefreshChanges()
    {
        var provider = new RecordingProvider { UsesFixedRefreshInterval = true };
        await using var service = new MetricUpdateService([provider], TimeSpan.FromMilliseconds(20));
        service.Start();
        await provider.WaitForCallsAsync(1);

        service.SetRefreshInterval(TimeSpan.FromMilliseconds(5));
        await Task.Delay(50);

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task SpecializedProviderCadencesRemainTheirOwnSemantics()
    {
        var fps = new FpsMetricProvider(OverlaySettings.CreateDefault());
        var day = new NetworkTodayMetricProvider(DeviceSelection.Auto);
        try
        {
            Assert.Equal(TimeSpan.FromMilliseconds(250), fps.RefreshInterval);
            Assert.Equal(TimeSpan.FromMinutes(1), day.RefreshInterval);
            Assert.True(day.UsesFixedRefreshInterval);
        }
        finally { await fps.DisposeAsync(); }
    }

    private sealed class RecordingProvider : IMetricProvider
    {
        private readonly TaskCompletionSource _callsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;
        private int _concurrent;

        public string Name => "Recording";
        public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);
        public bool UsesFixedRefreshInterval { get; init; }
        public int MaximumConcurrentCollections { get; private set; }
        public int CreatedInstances => 1;
        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaximumConcurrentCollections = Math.Max(MaximumConcurrentCollections, concurrent);
            Interlocked.Increment(ref _callCount);
            _callsChanged.TrySetResult();
            try { await Task.Delay(5, cancellationToken); }
            finally { Interlocked.Decrement(ref _concurrent); }
            return [];
        }

        public async Task WaitForCallsAsync(int expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (CallCount < expected)
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(remaining > TimeSpan.Zero, $"Timed out waiting for {expected} calls; saw {CallCount}.");
                await _callsChanged.Task.WaitAsync(remaining);
                if (CallCount < expected)
                {
                    // A completed source is only a wake signal; polling keeps this helper allocation-free in production code.
                    await Task.Delay(1);
                }
            }
        }
    }
}
