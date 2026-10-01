using System.Diagnostics;
using System.Runtime.InteropServices;
using DevOverlay.Metrics.Windows;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using Xunit;

namespace DevOverlay.Tests;

public sealed class PresentMonNativeMappingTests
{
    [Fact]
    public void OptInClassificationQueryPreservesOnePercentLowAndLatencyIsolation()
    {
        var native = new FakeNative();
        using var client = native.CreateClient(true);
        client.Track(100);
        var values = client.Poll(100, true);
        Assert.Equal(100, values.OnePercentLow);
        Assert.Equal(144, values.FramesPerSecond);
        Assert.Equal(8.1, values.RenderLatencyMs);
        Assert.Equal(2, native.FrameRegistrations);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferedLivePresentsAdvanceSourceEvenWhenEventAgeExceedsFreshnessTimeout(bool configureDelivery)
    {
        var native = new FakeNative { NowQpc = (ulong)(Stopwatch.Frequency * 100), SimulateEtwBuffering = true };
        using var client = native.CreateClient();
        if (configureDelivery) Assert.True(client.Connect());
        client.Track(100);
        native.NowQpc += (ulong)(Stopwatch.Frequency * 2);
        var first = client.Poll(100, true, false);
        native.NowQpc += (ulong)(Stopwatch.Frequency * 1.2);
        var next = client.Poll(100, true, false);
        Assert.Equal(144, next.FramesPerSecond);
        Assert.Equal(6.9, next.FrameTimeMs);
        Assert.True(first.SourceQpc > 0);
        Assert.True(next.SourceQpc > first.SourceQpc);
        Assert.Equal(100, next.OnePercentLow);
        if (!configureDelivery) Assert.True((native.NowQpc!.Value - next.SourceQpc) / (double)Stopwatch.Frequency > 1.5);
    }
    [Fact]
    public void ConnectionRequestsBoundedEtwDeliveryOnceBeforeTracking()
    {
        var native = new FakeNative();
        using var client = native.CreateClient();
        Assert.True(client.Connect());
        Assert.True(client.Connect());
        Assert.Equal(new[] { PresentMonClient.EtwFlushPeriodMs }, native.FlushRequests);
        Assert.Equal(250u, PresentMonClient.EtwFlushPeriodMs);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), FpsMetricProvider.FreshnessTimeout);
    }

    [Fact]
    public void EtwConfigurationFailureFailsConnectionRatherThanPublishingFalseFreshness()
    {
        var native = new FakeNative { FlushStatus = 4 };
        using var client = native.CreateClient();
        Assert.False(client.Connect());
        Assert.Equal(0UL, client.Poll(100, false).SourceQpc);
    }

    [Fact]
    public void LiveRawSourceEstablishesBaselineAndAdvancesWithIdenticalAggregate()
    {
        var native = new FakeNative();
        using var client = native.CreateClient();
        Assert.True(client.Connect());
        client.Track(100);
        var first = client.Poll(100, true, false);
        var second = client.Poll(100, true, false);
        Assert.True(first.SourceQpc > 0);
        Assert.True(second.SourceQpc > first.SourceQpc);
        Assert.Equal(first.FramesPerSecond, second.FramesPerSecond);
        Assert.Equal(first.FrameTimeMs, second.FrameTimeMs);
        Assert.NotNull(second.OnePercentLow);
    }

    [Fact]
    public async Task BufferedNativeFramesReachProviderWarmupThenExpireWithoutAnyNewFrames()
    {
        var native = new FakeNative { NowQpc = (ulong)(Stopwatch.Frequency * 100), SimulateEtwBuffering = true };
        var clock = new IntegrationClock();
        await using var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), native.CreateClient, new IntegrationProcesses(), clock);
        Assert.DoesNotContain(await provider.CollectAsync(default), metric => metric.IsAvailable); // no baseline yet
        clock.Advance(TimeSpan.FromSeconds(2));
        native.NowQpc += (ulong)(Stopwatch.Frequency * 2);
        var first = await provider.CollectAsync(default);
        Assert.Contains(first, metric => metric.Id == MetricId.FramesPerSecond && metric.Value == 144);
        Assert.Contains(first, metric => metric.Id == MetricId.FrameTime && metric.Value == 6.9);
        Assert.Contains(first, metric => metric.Id == MetricId.OnePercentLow && !metric.IsAvailable);
        clock.Advance(TimeSpan.FromSeconds(11));
        native.NowQpc += (ulong)(Stopwatch.Frequency * 11);
        Assert.Contains(await provider.CollectAsync(default), metric => metric.Id == MetricId.OnePercentLow && metric.Value == 100);
        native.EmitFrames = false;
        clock.Advance(TimeSpan.FromSeconds(2));
        native.NowQpc += (ulong)(Stopwatch.Frequency * 2);
        Assert.All(await provider.CollectAsync(default), metric => Assert.False(metric.IsAvailable));
    }
    [Fact]
    public void ProductionClientUsesReturnedOffsetsAndSeparateFrameBlobStride()
    {
        var native = new FakeNative();
        using var client = native.CreateClient();
        Assert.True(client.Connect());
        Assert.True(client.Track(100));
        var values = client.Poll(100, true);
        Assert.Equal(144, values.FramesPerSecond);
        Assert.Equal(6.9, values.FrameTimeMs);
        Assert.Equal(100, values.OnePercentLow);
        Assert.True(values.SourceQpc > 0);
        Assert.Equal(8.1, values.RenderLatencyMs);
        // LAT frame-query blob stride includes native trailing padding; max(offset+size) is insufficient.
        Assert.Equal(40 * 1024, native.FrameBuffer!.Length);
        var fast = native.FastBuffer;
        var frame = native.FrameBuffer;
        client.Poll(100, false);
        Assert.Same(fast, native.FastBuffer);
        Assert.Same(frame, native.FrameBuffer);
        Assert.Equal(1, native.DynamicRegistrations);
        Assert.Equal(2, native.FrameRegistrations);
        Assert.Equal(32, Marshal.SizeOf<PresentMonClient.QueryElement>());
    }

    [Fact]
    public void FpsOnlyNeverRegistersOrConsumesLatencyFrames()
    {
        var native = new FakeNative();
        using var client = native.CreateClient();
        client.Track(100);
        Assert.Equal(144, client.Poll(100, false, false).FramesPerSecond);
        Assert.Equal(1, native.FrameRegistrations); // presented frames are required for freshness, even with LAT hidden
        Assert.Null(native.FrameBuffer);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LatencyRegistrationOrConsumeFailureKeepsFpsAndFrameTime(bool registrationFails, bool consumeFails)
    {
        var native = new FakeNative { RegistrationFails = registrationFails, ConsumeFails = consumeFails };
        using var client = native.CreateClient();
        client.Track(100);
        var values = client.Poll(100, false);
        Assert.Equal(144, values.FramesPerSecond);
        Assert.Equal(6.9, values.FrameTimeMs);
        Assert.Null(values.RenderLatencyMs);
    }

    [Fact]
    public void TargetChangeAndStopDiscardLatencyWindow()
    {
        var native = new FakeNative();
        using var client = native.CreateClient();
        client.Track(100);
        Assert.Equal(8.1, client.Poll(100, false).RenderLatencyMs);
        native.EmitFrames = false;
        client.Track(200);
        Assert.Null(client.Poll(200, false).RenderLatencyMs);
        client.StopTracking(200);
        Assert.Null(client.Poll(100, false).RenderLatencyMs);
    }

    [Fact]
    public void ReplayedAggregateAndRawFrameDoNotAdvanceSourceOrPolluteLowAverage()
    {
        var native = new FakeNative { FreezeFrameTimestamp = true };
        using var client = native.CreateClient();
        client.Track(100);
        var initial = client.Poll(100, true, false);
        var repeated = client.Poll(100, true, false);
        Assert.Equal(144, repeated.FramesPerSecond); // native query still returns the old numeric aggregate
        Assert.Equal(initial.SourceQpc, repeated.SourceQpc);
        Assert.Equal(initial.OnePercentLow, repeated.OnePercentLow);
        client.StopTracking(100);
        native.EmitFrames = false;
        client.Track(100);
        Assert.Equal(0UL, client.Poll(100, true, false).SourceQpc);
        Assert.Null(client.Poll(100, true, false).OnePercentLow);
    }

    [Fact]
    public async Task NativeDecodeThroughProviderExpiresFrozenRowsAndRequiresNewFramesForRecovery()
    {
        var native = new FakeNative { FreezeFrameTimestamp = true };
        var clock = new IntegrationClock();
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), native.CreateClient, new IntegrationProcesses(), clock);
        try
        {
            Assert.Contains(await provider.CollectAsync(default), metric => metric.Id == MetricId.FramesPerSecond && metric.Value == 144);
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.All(await provider.CollectAsync(default), metric => Assert.False(metric.IsAvailable));
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.DoesNotContain(await provider.CollectAsync(default), metric => metric.IsAvailable);
            native.FreezeFrameTimestamp = false;
            clock.Advance(TimeSpan.FromSeconds(1));
            var resumed = await provider.CollectAsync(default);
            Assert.Contains(resumed, metric => metric.Id == MetricId.FramesPerSecond && metric.Value == 144);
            Assert.Contains(resumed, metric => metric.Id == MetricId.FrameTime && metric.IsAvailable);
            Assert.Contains(resumed, metric => metric.Id == MetricId.OnePercentLow && !metric.IsAvailable);
        }
        finally { await provider.DisposeAsync(); }
    }

    private sealed class IntegrationClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class IntegrationProcesses : IFpsProcessSource
    {
        public FpsProcess? GetForeground() => new FpsProcess(100, "game.exe");
        public FpsProcess? FindSpecific(string identity) => GetForeground();
        public bool IsRunning(uint pid) => true;
        public IReadOnlyCollection<DeviceDescriptor> ListRunning() => [];
    }

    [Fact]
    public void LatencyMeanExpiresByTimestampAndPreservesValidZero()
    {
        var window = new RenderLatencyWindow();
        var now = (ulong)Stopwatch.GetTimestamp();
        window.Add(now, 0, now);
        Assert.Equal(0, window.Mean(now));
        window.Add(now, double.NaN, now);
        window.Add(now, double.PositiveInfinity, now);
        window.Add(now, -1, now);
        Assert.Equal(0, window.Mean(now));
        Assert.Null(window.Mean(now + (ulong)(Stopwatch.Frequency * 3)));
    }

    private sealed class FakeNative
    {
        private PresentMonClient.QueryElement[] _fast = [];
        private PresentMonClient.QueryElement[] _slow = [];
        private PresentMonClient.QueryElement[] _frame = [];
        private PresentMonClient.QueryElement[] _present = [];
        internal int DynamicRegistrations;
        internal int FrameRegistrations;
        internal byte[]? FastBuffer;
        internal byte[]? FrameBuffer;
        internal bool RegistrationFails;
        internal bool ConsumeFails;
        internal bool EmitFrames = true;
        internal bool FreezeFrameTimestamp;
        internal readonly List<uint> FlushRequests = [];
        internal int FlushStatus;
        internal ulong? NowQpc;
        internal bool SimulateEtwBuffering;
        private ulong _frameQpc;
        private int _presentStride = 40;

        internal PresentMonClient CreateClient() => CreateClient(false);
        internal PresentMonClient CreateClient(bool lowTrace) => new(Register, Poll, RegisterFrame, Consume, (_, _) => 0, _ => 0,
            (session, period) => { Assert.Equal(1, session); FlushRequests.Add(period); return FlushStatus; },
            () => NowQpc ?? (ulong)Stopwatch.GetTimestamp(), lowTrace);

        private int Register(nint session, out nint query, PresentMonClient.QueryElement[] elements,
            ulong count, double window, double offset)
        {
            Assert.Equal((ulong)elements.Length, count);
            Assert.DoesNotContain(elements, element => element.Metric == 82);
            DynamicRegistrations++;
            if (window == 2000)
            {
                Assert.Equal([1, 12, 87], elements.Select(element => element.Metric));
                Assert.Equal([12, 1, 1], elements.Select(element => element.Stat));
                query = 10;
                _fast = elements;
            }
            else
            {
                Assert.Equal(15000, window);
                Assert.Equal([1, 12], elements.Select(element => element.Metric));
                Assert.Equal([12, 5], elements.Select(element => element.Stat));
                query = 20;
                _slow = elements;
            }
            // Deliberately nonsequential/native-provided layout, not index * sizeof(double).
            for (var i = 0; i < elements.Length; i++)
            {
                elements[i].DataOffset = (ulong)(i == 0 ? 8 : i == 1 ? 24 : 0);
                elements[i].DataSize = 8;
            }
            return 0;
        }

        private int Poll(nint query, uint pid, byte[] buffer, ref uint count)
        {
            count = 1;
            var elements = query == 10 ? _fast : _slow;
            if (query == 10) FastBuffer = buffer;
            BitConverter.TryWriteBytes(buffer.AsSpan((int)elements[0].DataOffset), 999UL);
            BitConverter.TryWriteBytes(buffer.AsSpan((int)elements[1].DataOffset), query == 10 ? 144d : 118d);
            if (query == 10) BitConverter.TryWriteBytes(buffer.AsSpan((int)elements[2].DataOffset), 6.9);
            return 0;
        }

        private int RegisterFrame(nint session, out nint query, PresentMonClient.QueryElement[] elements,
            ulong count, out uint blobSize)
        {
            FrameRegistrations++;
            var presented = elements[2].Metric == 78;
            Assert.Equal(presented ? 2 : 1, session);
            Assert.Equal(presented ? (elements.Length == 6 ? [1, 77, 78, 80, 16, 63] : new[] { 1, 77, 78, 80 }) : new[] { 1, 77, 82 }, elements.Select(element => element.Metric));
            Assert.All(elements, element => Assert.Equal(0, element.Stat));
            query = presented ? 40 : RegistrationFails ? 0 : 30;
            blobSize = 40;
            _frame = elements;
            if (presented) _present = elements;
            for (var i = 0; i < elements.Length; i++)
            {
                elements[i].DataOffset = (ulong)(i == 0 ? 24 : i == 1 ? 8 : 0);
                elements[i].DataSize = 8;
            }
            if (presented)
            {
                // Display change (80) gets its own slot; it is read by metric id, not by a fixed index.
                elements[3].DataOffset = 32;
                blobSize = 40;
                _presentStride = 40;
            }
            if (presented && elements.Length == 6)
            {
                elements[4].DataOffset = 40; elements[4].DataSize = 1;
                elements[5].DataOffset = 44; elements[5].DataSize = 4;
                blobSize = 48;
                _presentStride = 48;
            }
            return !presented && RegistrationFails ? 21 : 0;
        }

        private int Consume(nint query, uint pid, byte[] buffer, ref uint count)
        {
            var elements = query == 40 ? _present : _frame;
            if (query != 40) FrameBuffer = buffer;
            count = EmitFrames ? 2u : 0;
            if (!FreezeFrameTimestamp || _frameQpc == 0)
                _frameQpc = NowQpc ?? (ulong)Stopwatch.GetTimestamp();
            if (SimulateEtwBuffering) _frameQpc = NowQpc!.Value - (ulong)(Stopwatch.Frequency * (FlushRequests.Count > 0 ? 0.25 : 1.8));
            for (var i = 0; i < count; i++)
            {
                var stride = query == 40 ? _presentStride : 40;
                var blob = buffer.AsSpan(i * stride, stride);
                BitConverter.TryWriteBytes(blob[(int)elements[0].DataOffset..], 999UL);
                BitConverter.TryWriteBytes(blob[(int)elements[1].DataOffset..], query == 40 ? _frameQpc : (ulong)Stopwatch.GetTimestamp() - (ulong)Stopwatch.Frequency);
                BitConverter.TryWriteBytes(blob[(int)elements[2].DataOffset..], query == 40 ? 10d : 8.1);
                if (query == 40) BitConverter.TryWriteBytes(blob[(int)elements[3].DataOffset..], 10d);
                if (query == 40 && elements.Length == 6)
                {
                    blob[(int)elements[4].DataOffset] = (byte)(i == 0 ? 1 : 0);
                    BitConverter.TryWriteBytes(blob[(int)elements[5].DataOffset..], i == 0 ? 50 : 2);
                }
            }
            return query != 40 && ConsumeFails ? 4 : 0;
        }
    }

    [Fact]
    public void InterleavedSwapChainsKeepIndependentFreshMeans()
    {
        var window = new RenderLatencyWindow();
        var now = (ulong)Stopwatch.GetTimestamp();
        window.Add(now, 4, now, 100);
        window.Add(now - (ulong)Stopwatch.Frequency, 20, now, 200);
        Assert.Equal(4, window.Mean(now, 100));
        Assert.Equal(20, window.Mean(now, 200));
        Assert.Null(window.Mean(now, 300));
        Assert.Equal(4, window.Mean(now + (ulong)(Stopwatch.Frequency * 1.5), 100));
        Assert.Null(window.Mean(now + (ulong)(Stopwatch.Frequency * 1.5), 200));
    }
}
