using System.IO;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Presentation;
using DevOverlay.Platform.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class FpsMetricProviderTests
{
    [Fact]
    public async Task SuccessfulRepeatedAggregateWithoutNewSourceFramesExpiresAllFrameMetrics()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(138, 7.3, 111);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => client, processes, clock);
        Assert.Equal(138, Value(await provider.CollectAsync(default), MetricId.FramesPerSecond));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(111, Value(await provider.CollectAsync(default), MetricId.OnePercentLow));
        client.AdvanceSource = false;
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Empty(await provider.CollectAsync(default));
        clock.Advance(TimeSpan.FromSeconds(2));
        AssertAllUnavailable(await provider.CollectAsync(default));
        for (var index = 0; index < 5; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.DoesNotContain(await provider.CollectAsync(default), metric => metric.IsAvailable);
        }
        processes.Running = false;
        processes.Foreground = null;
        await provider.CollectAsync(default);
        processes.Running = true;
        processes.Foreground = new FpsProcess(101, "game.exe");
        client.AdvanceSource = true;
        client.Values[101] = new PresentMonValues(120, 8.3, 80);
        clock.Advance(TimeSpan.FromSeconds(1));
        var resumed = await provider.CollectAsync(default);
        Assert.Equal(120, Value(resumed, MetricId.FramesPerSecond));
        Assert.Equal(8.3, Value(resumed, MetricId.FrameTime));
        Assert.Null(Value(resumed, MetricId.OnePercentLow));
        await provider.DisposeAsync();
    }
    [Fact]
    public async Task DisabledGroupOrAllChildrenDoesNotConnect()
    {
        var client = new FakeClient();
        var settings = OverlaySettings.CreateDefault() with
        {
            EnabledGroups = new HashSet<MetricCategory> { MetricCategory.Cpu }
        };
        var provider = new FpsMetricProvider(settings, () => client, new FakeProcesses(), new FakeClock());
        Assert.Empty(await provider.CollectAsync(default));
        Assert.Equal(0, client.ConnectCalls);

        provider.Configure(settings with
        {
            EnabledGroups = new HashSet<MetricCategory> { MetricCategory.Frame },
            EnabledMetrics = new HashSet<MetricId>()
        });
        await provider.CollectAsync(default);
        Assert.Equal(0, client.ConnectCalls);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task AutoRetainsGameDuringSettingsFocusAndReusesQueries()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(120, 8.3, 90);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => client, processes, clock);

        var first = await provider.CollectAsync(default);
        Assert.Equal(120, Value(first, MetricId.FramesPerSecond));
        Assert.Equal(8.3, Value(first, MetricId.FrameTime));
        Assert.Null(Value(first, MetricId.OnePercentLow));
        Assert.Equal(1, client.ConnectCalls);
        Assert.Equal([100u], client.Tracked);

        processes.Foreground = null; // DevOverlay Settings owns the foreground.
        clock.Advance(TimeSpan.FromSeconds(1));
        await provider.CollectAsync(default);
        Assert.Equal(100u, provider.CurrentTarget?.Pid);
        Assert.Single(client.Tracked);

        clock.Advance(TimeSpan.FromSeconds(10));
        var warmed = await provider.CollectAsync(default);
        Assert.Equal(90, Value(warmed, MetricId.OnePercentLow));
        Assert.Equal(1, client.ConnectCalls);
        Assert.Equal(1, client.SlowPolls);
        await provider.DisposeAsync();
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task AutoSwitchesOnlyAfterNewForegroundHasPresents()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "first.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(60, 16.7, 50);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => client, processes, clock);
        await provider.CollectAsync(default);

        processes.Foreground = new FpsProcess(200, "second.exe");
        clock.Advance(TimeSpan.FromSeconds(1));
        var whileCandidateIsCold = await provider.CollectAsync(default);
        Assert.Equal(60, Value(whileCandidateIsCold, MetricId.FramesPerSecond));
        Assert.Equal(100u, provider.CurrentTarget?.Pid);

        client.Values[200] = new PresentMonValues(144, 6.9, 100);
        var switched = await provider.CollectAsync(default);
        Assert.Equal(144, Value(switched, MetricId.FramesPerSecond));
        Assert.Null(Value(switched, MetricId.OnePercentLow));
        Assert.Contains(100u, client.Stopped);
        Assert.Equal(200u, provider.CurrentTarget?.Pid);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SpecificNeverFallsBackAndReacquiresAfterExit()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(300, "unrelated.exe") };
        var client = new FakeClient();
        client.Values[300] = new PresentMonValues(200, 5, 180);
        var settings = OverlaySettings.CreateDefault() with { FpsTargetSelection = DeviceSelection.Specific("game.exe") };
        var provider = new FpsMetricProvider(settings, () => client, processes, clock);
        Assert.All(await provider.CollectAsync(default), metric => Assert.False(metric.IsAvailable));
        Assert.Empty(client.Tracked);

        processes.Specific = new FpsProcess(100, "game.exe");
        client.Values[100] = new PresentMonValues(60, 16.7, 50);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(60, Value(await provider.CollectAsync(default), MetricId.FramesPerSecond));
        processes.Running = false;
        Assert.Null(Value(await provider.CollectAsync(default), MetricId.FramesPerSecond));
        Assert.Contains(100u, client.Stopped);

        processes.Running = true;
        processes.Specific = new FpsProcess(101, "game.exe");
        client.Values[101] = new PresentMonValues(120, 8.3, 90);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(120, Value(await provider.CollectAsync(default), MetricId.FramesPerSecond));
        Assert.DoesNotContain(300u, client.Tracked);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task AutoTargetExitInvalidatesAllThreeMetricsAndReleasesTracking()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(114, 8.7, 28);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => client, processes, clock);
        await provider.CollectAsync(default);
        clock.Advance(TimeSpan.FromSeconds(10));
        var warmed = await provider.CollectAsync(default);
        Assert.Equal(114, Value(warmed, MetricId.FramesPerSecond));
        Assert.Equal(28, Value(warmed, MetricId.OnePercentLow));

        processes.Running = false;
        processes.Foreground = null;
        clock.Advance(TimeSpan.FromMilliseconds(250));
        AssertAllUnavailable(await provider.CollectAsync(default));
        Assert.Null(provider.CurrentTarget);
        Assert.Contains(100u, client.Stopped);
        // Already published as unavailable: nothing numeric may be republished while no target exists.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(await provider.CollectAsync(default));
        await provider.DisposeAsync();
    }

    [Theory]
    [InlineData(60, 16.7, 50)]
    [InlineData(120, 8.3, 100)]
    public async Task IdenticalValidSamplesStayAvailableIndefinitely(double fps, double frameTime, double low)
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(fps, frameTime, low, 8.1);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => client, processes, clock);
        await provider.CollectAsync(default);

        // 60 seconds of polling at the 250 ms cadence with exactly the same aggregate every time.
        IReadOnlyCollection<MetricSnapshot> last = [];
        for (var poll = 0; poll < 240; poll++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            last = await provider.CollectAsync(default);
            Assert.Equal(fps, Value(last, MetricId.FramesPerSecond));
            Assert.Equal(frameTime, Value(last, MetricId.FrameTime));
            Assert.Equal(8.1, Value(last, MetricId.Latency));
            Assert.All(last.Where(metric => metric.Id != MetricId.OnePercentLow), metric => Assert.True(metric.IsAvailable));
        }
        // Successful, numerically identical slow-query results remain valid after warm-up.
        Assert.Equal(low, Value(last, MetricId.OnePercentLow));
        Assert.NotNull(provider.CurrentTarget);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task NoValidResultBecomesUnavailableOnlyAfterTheFreshnessTimeoutThenRecovers()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(60, 16.7, 50);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => client, processes, clock);
        await provider.CollectAsync(default);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(50, Value(await provider.CollectAsync(default), MetricId.OnePercentLow));

        client.Values.Remove(100); // process alive, but the service reports no valid result
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Empty(await provider.CollectAsync(default)); // within the timeout: no new sample published, nothing refreshed
        Assert.NotNull(provider.CurrentTarget);
        clock.Advance(FpsMetricProvider.FreshnessTimeout);
        AssertAllUnavailable(await provider.CollectAsync(default));
        Assert.Null(provider.CurrentTarget);
        Assert.Contains(100u, client.Stopped);

        client.Values[100] = new PresentMonValues(60, 16.7, 50); // presents resume: fresh state, warm-up restarts
        clock.Advance(TimeSpan.FromSeconds(1));
        var resumed = await provider.CollectAsync(default);
        Assert.Equal(60, Value(resumed, MetricId.FramesPerSecond));
        Assert.Null(Value(resumed, MetricId.OnePercentLow));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task TargetSwitchDiscardsOldOnePercentLowState()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "first.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(60, 16.7, 50);
        client.Values[200] = new PresentMonValues(144, 6.9, 100);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => client, processes, clock);
        await provider.CollectAsync(default);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(50, Value(await provider.CollectAsync(default), MetricId.OnePercentLow));

        processes.Foreground = new FpsProcess(200, "second.exe");
        clock.Advance(TimeSpan.FromSeconds(1));
        var switched = await provider.CollectAsync(default);
        Assert.Equal(144, Value(switched, MetricId.FramesPerSecond));
        Assert.Null(Value(switched, MetricId.OnePercentLow));
        // Warm-up equals the 3 s window: the new target's 1% Low stays unavailable until its own window can be full.
        clock.Advance(TimeSpan.FromSeconds(2.9));
        Assert.Null(Value(await provider.CollectAsync(default), MetricId.OnePercentLow));
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(100, Value(await provider.CollectAsync(default), MetricId.OnePercentLow));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task UnexpectedPollExceptionInvalidatesValuesAndReconnects()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var first = new FakeClient();
        first.Values[100] = new PresentMonValues(60, 16.7, 50);
        var second = new FakeClient();
        second.Values[100] = first.Values[100];
        var clients = new Queue<FakeClient>([first, second]);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => clients.Dequeue(), processes, clock);
        Assert.Equal(60, Value(await provider.CollectAsync(default), MetricId.FramesPerSecond));

        first.FailWith = new InvalidCastException("simulated native/interop failure not in the old filter");
        clock.Advance(TimeSpan.FromMilliseconds(250));
        AssertAllUnavailable(await provider.CollectAsync(default));
        Assert.True(first.Disposed);
        Assert.Null(provider.CurrentTarget);

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(60, Value(await provider.CollectAsync(default), MetricId.FramesPerSecond));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SpecificRelaunchWithNewPidResumesFreshAndWarmsUpAgain()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(300, "other.exe"), Specific = new FpsProcess(100, "game.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(60, 16.7, 50);
        client.Values[101] = new PresentMonValues(90, 11.1, 70);
        client.Values[300] = new PresentMonValues(200, 5, 180);
        var settings = OverlaySettings.CreateDefault() with { FpsTargetSelection = DeviceSelection.Specific("game.exe") };
        var provider = new FpsMetricProvider(settings, () => client, processes, clock);
        await provider.CollectAsync(default);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(50, Value(await provider.CollectAsync(default), MetricId.OnePercentLow));

        processes.Running = false;
        processes.Specific = null;
        clock.Advance(TimeSpan.FromMilliseconds(250));
        AssertAllUnavailable(await provider.CollectAsync(default));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(await provider.CollectAsync(default)); // waits; never falls back to the foreground process

        processes.Running = true;
        processes.Specific = new FpsProcess(101, "game.exe");
        clock.Advance(TimeSpan.FromSeconds(1));
        var relaunched = await provider.CollectAsync(default);
        Assert.Equal(90, Value(relaunched, MetricId.FramesPerSecond));
        Assert.Null(Value(relaunched, MetricId.OnePercentLow));
        Assert.DoesNotContain(300u, client.Tracked);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task FailureDisconnectsAndReconnectsWithoutTightLoop()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var failed = new FakeClient { FailPoll = true };
        var recovered = new FakeClient();
        recovered.Values[100] = new PresentMonValues(60, 16.7, 50);
        var clients = new Queue<FakeClient>([failed, recovered]);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => clients.Dequeue(), processes, clock);

        Assert.Null(Value(await provider.CollectAsync(default), MetricId.FramesPerSecond));
        Assert.True(failed.Disposed);
        Assert.Single(clients);
        await provider.CollectAsync(default);
        Assert.Single(clients);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(60, Value(await provider.CollectAsync(default), MetricId.FramesPerSecond));
        Assert.Empty(clients);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task HidingOverlayKeepsPresentMonConnectedAndPreservesWarmup()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var first = new FakeClient();
        first.Values[100] = new PresentMonValues(60, 16.7, 50);
        var second = new FakeClient();
        second.Values[100] = first.Values[100];
        var clients = new Queue<FakeClient>([first, second]);
        var settings = OverlaySettings.CreateDefault();
        var provider = new FpsMetricProvider(settings, () => clients.Dequeue(), processes, clock);
        await provider.CollectAsync(default);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(50, Value(await provider.CollectAsync(default), MetricId.OnePercentLow));

        provider.Configure(settings with { IsVisible = false });
        var hidden = await provider.CollectAsync(default);
        Assert.Equal(60, Value(hidden, MetricId.FramesPerSecond));
        Assert.False(first.Disposed);
        provider.Configure(settings);
        var resumed = await provider.CollectAsync(default);
        Assert.Equal(60, Value(resumed, MetricId.FramesPerSecond));
        Assert.Equal(50, Value(resumed, MetricId.OnePercentLow));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SpecificTargetChangeDoesNotReopenPresentMonSession()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Specific = new FpsProcess(100, "first.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(60, 16.7, 50);
        client.Values[200] = new PresentMonValues(120, 8.3, 90);
        var settings = OverlaySettings.CreateDefault() with { FpsTargetSelection = DeviceSelection.Specific("first.exe") };
        var provider = new FpsMetricProvider(settings, () => client, processes, clock);
        await provider.CollectAsync(default);
        processes.Specific = new FpsProcess(200, "second.exe");
        provider.Configure(settings with { FpsTargetSelection = DeviceSelection.Specific("second.exe") });
        Assert.Equal(120, Value(await provider.CollectAsync(default), MetricId.FramesPerSecond));
        Assert.Equal(1, client.ConnectCalls);
        Assert.Equal([100u, 200u], client.Tracked);
        Assert.Contains(100u, client.Stopped);
        await provider.DisposeAsync();
    }

    [Fact]
    public void DefaultsPersistenceAndLegacyOrderIncludeOneFpsGroup()
    {
        var settings = OverlaySettings.CreateDefault();
        Assert.Equal([MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Frame, MetricCategory.Latency,
            MetricCategory.Storage, MetricCategory.Network, MetricCategory.AiUsage], settings.GroupOrder);
        Assert.True(settings.EnabledGroups.Contains(MetricCategory.Frame));
        Assert.True(settings.EnabledMetrics.Contains(MetricId.OnePercentLow));
        Assert.Equal([MetricCategory.Network, MetricCategory.Gpu, MetricCategory.Frame, MetricCategory.Latency,
            MetricCategory.Cpu, MetricCategory.Storage, MetricCategory.AiUsage], OverlaySettings.NormalizeGroupOrder(
                [MetricCategory.Network, MetricCategory.Gpu, MetricCategory.Cpu, MetricCategory.Storage]));

        var directory = Path.Combine(Path.GetTempPath(), "DevOverlay-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new OverlaySettingsStore(path);
            store.Save(settings with { FpsTargetSelection = DeviceSelection.Specific(@"C:\Games\game.exe") });
            Assert.Equal(@"C:\Games\game.exe", Assert.IsType<SpecificDeviceSelection>(store.Load().FpsTargetSelection).DeviceId);
            File.WriteAllText(path, "{}");
            Assert.IsType<AutomaticDeviceSelection>(store.Load().FpsTargetSelection);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void SettingsKeepSpecificTargetOnListRefreshAndGroupCanHide()
    {
        var settings = OverlaySettings.CreateDefault() with { FpsTargetSelection = DeviceSelection.Specific("game.exe") };
        var editor = new SettingsViewModel(settings);
        editor.UpdateFpsTargets([new DeviceDescriptor("game.exe", "game.exe")]);
        Assert.Equal("game.exe", editor.SelectedFpsTarget.Id);
        editor.UpdateFpsTargets([]);
        Assert.Equal("game.exe", editor.SelectedFpsTarget.Id);

        var overlay = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        overlay.ApplyMetrics([new MetricSnapshot(MetricId.FramesPerSecond, MetricCategory.Frame, "FPS", 144, "", true,
            DateTimeOffset.UtcNow)]);
        Assert.Single(overlay.Groups);
        overlay.ApplySettings(settings with { EnabledGroups = new HashSet<MetricCategory>() });
        Assert.Empty(overlay.Groups);
        overlay.ApplySettings(settings);
        Assert.Single(overlay.Groups);
    }

    [Fact]
    public void SettingsServiceStatesAndFpsFieldWidthsAreStable()
    {
        var editor = new SettingsViewModel(OverlaySettings.CreateDefault());
        editor.UpdatePresentMonStatus(new PresentMonStatus(PresentMonRunState.NotInstalled));
        Assert.Contains("Not installed", editor.PresentMonStatusText);
        editor.UpdatePresentMonStatus(new PresentMonStatus(PresentMonRunState.Running, "2.6.0"));
        Assert.Contains("Running", editor.PresentMonStatusText);
        editor.UpdatePresentMonStatus(new PresentMonStatus(PresentMonRunState.Unavailable, "2.5.1"));
        Assert.Contains("Unavailable", editor.PresentMonStatusText);
        Assert.True(MetricDisplayLayout.GetTextWidth(MetricId.FrameTime) >
            MetricDisplayLayout.GetTextWidth(MetricId.FramesPerSecond));

        var unavailable = new MetricItemViewModel(MetricSnapshot.Unavailable(
            MetricId.FrameTime, MetricCategory.Frame, "Frame Time", "ms"));
        Assert.Equal("FT N/A", unavailable.Text);
        unavailable.Update(new MetricSnapshot(MetricId.FrameTime, MetricCategory.Frame,
            "Frame Time", 16.7, "ms", true, DateTimeOffset.UtcNow));
        Assert.Equal("FT 16.7ms", unavailable.Text);
    }

    [Fact]
    public async Task LatencyOnlyKeepsTheSharedPresentMonTargetActive()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(120, 8.3, 90, 8.1);
        var settings = OverlaySettings.CreateDefault() with
        {
            EnabledGroups = new HashSet<MetricCategory> { MetricCategory.Latency },
            EnabledMetrics = new HashSet<MetricId> { MetricId.Latency }
        };
        var provider = new FpsMetricProvider(settings, () => client, processes, clock);

        var samples = await provider.CollectAsync(default);
        var latency = Assert.Single(samples);
        Assert.Equal(MetricId.Latency, latency.Id);
        Assert.Equal(MetricCategory.Latency, latency.Category);
        Assert.True(latency.IsAvailable);
        Assert.Equal(8.1, latency.Value);
        Assert.Equal(1, client.ConnectCalls);
        Assert.Equal([100u], client.Tracked);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task FpsOnlyKeepsCaptureActiveWithoutPublishingLatency()
    {
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(120, 8.3, 90, 8.1);
        var settings = OverlaySettings.CreateDefault() with
        {
            EnabledGroups = new HashSet<MetricCategory> { MetricCategory.Frame },
            EnabledMetrics = new HashSet<MetricId>
            {
                MetricId.FramesPerSecond, MetricId.OnePercentLow, MetricId.FrameTime
            }
        };
        var provider = new FpsMetricProvider(settings, () => client, processes, new FakeClock());

        var samples = await provider.CollectAsync(default);
        Assert.Equal(3, samples.Count);
        Assert.DoesNotContain(samples, metric => metric.Id == MetricId.Latency);
        Assert.Equal(1, client.ConnectCalls);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task UnavailableLatencyDoesNotInvalidateFrameMetrics()
    {
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(144, 6.9, 118, null);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => client, processes, new FakeClock());

        var samples = await provider.CollectAsync(default);
        Assert.Equal(144, Value(samples, MetricId.FramesPerSecond));
        Assert.Equal(6.9, Value(samples, MetricId.FrameTime));
        var latency = samples.Single(metric => metric.Id == MetricId.Latency);
        Assert.False(latency.IsAvailable);
        Assert.Null(latency.Value);
        Assert.NotNull(provider.CurrentTarget);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task DisablingBothFrameAndLatencyReleasesPresentMon()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(120, 8.3, 90, 8.1);
        var provider = new FpsMetricProvider(OverlaySettings.CreateDefault(), () => client, processes, clock);
        await provider.CollectAsync(default);

        provider.Configure(OverlaySettings.CreateDefault() with
        {
            EnabledGroups = new HashSet<MetricCategory>(),
            EnabledMetrics = new HashSet<MetricId>()
        });
        Assert.Empty(await provider.CollectAsync(default));
        Assert.True(client.Disposed);
        await provider.DisposeAsync();
    }

    private static double? Value(IReadOnlyCollection<MetricSnapshot> metrics, MetricId id) =>
        metrics.Single(metric => metric.Id == id).Value;

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ProductionActivationAndHudGroupsFollowIndependentFrameAndLatencyVisibility(bool fps, bool latency)
    {
        var defaults = OverlaySettings.CreateDefault();
        var settings = defaults with
        {
            EnabledGroups = new HashSet<MetricCategory>(new[] {
                fps ? MetricCategory.Frame : (MetricCategory)(-1),
                latency ? MetricCategory.Latency : (MetricCategory)(-1) }.Where(Enum.IsDefined))
        };
        var client = new FakeClient();
        client.Values[100] = new PresentMonValues(144, 6.9, 118, 8.1);
        var provider = new FpsMetricProvider(settings, () => client,
            new FakeProcesses { Foreground = new FpsProcess(100, "game.exe") }, new FakeClock());
        var snapshots = await provider.CollectAsync(default);
        Assert.Equal(fps || latency, provider.HasConnection);
        var overlay = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        overlay.ApplyMetrics(snapshots);
        Assert.Equal(fps, overlay.Groups.Any(group => group.Category == MetricCategory.Frame));
        Assert.Equal(latency, overlay.Groups.Any(group => group.Category == MetricCategory.Latency));
        Assert.Equal(fps || latency ? 1 : 0, client.ConnectCalls);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task EnablingNewChildWhileTargetIsUnavailablePublishesItsPlaceholder()
    {
        var settings = OverlaySettings.CreateDefault() with
        {
            EnabledGroups = new HashSet<MetricCategory> { MetricCategory.Frame },
            EnabledMetrics = new HashSet<MetricId> { MetricId.FramesPerSecond }
        };
        var provider = new FpsMetricProvider(settings, () => new FakeClient(), new FakeProcesses(), new FakeClock());
        Assert.Single(await provider.CollectAsync(default));
        Assert.Empty(await provider.CollectAsync(default));
        provider.Configure(settings with { EnabledMetrics = new HashSet<MetricId> { MetricId.FramesPerSecond, MetricId.FrameTime } });
        var snapshots = await provider.CollectAsync(default);
        Assert.Equal(2, snapshots.Count);
        Assert.Contains(snapshots, metric => metric.Id == MetricId.FrameTime && !metric.IsAvailable);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SpecificExitRelaunchAndClientReconnectRecoverLatencyWithoutOldValues()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Specific = new FpsProcess(100, "game.exe") };
        var first = new FakeClient();
        first.Values[100] = new PresentMonValues(144, 6.9, 118, 8.1);
        first.Values[101] = new PresentMonValues(120, 8.3, 90, 12.4);
        var second = new FakeClient();
        second.Values[101] = new PresentMonValues(100, 10, 80, 6.5);
        var clients = new Queue<FakeClient>([first, second]);
        var settings = OverlaySettings.CreateDefault() with { FpsTargetSelection = DeviceSelection.Specific("game.exe") };
        var provider = new FpsMetricProvider(settings, () => clients.Dequeue(), processes, clock);
        Assert.Equal(8.1, Value(await provider.CollectAsync(default), MetricId.Latency));
        processes.Running = false;
        processes.Specific = null;
        AssertAllUnavailable(await provider.CollectAsync(default));
        processes.Running = true;
        processes.Specific = new FpsProcess(101, "game.exe");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(12.4, Value(await provider.CollectAsync(default), MetricId.Latency));
        first.FailPoll = true;
        AssertAllUnavailable(await provider.CollectAsync(default));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(6.5, Value(await provider.CollectAsync(default), MetricId.Latency));
        await provider.DisposeAsync();
    }

    private static void AssertAllUnavailable(IReadOnlyCollection<MetricSnapshot> metrics)
    {
        Assert.Equal(4, metrics.Count);
        Assert.All(metrics, metric =>
        {
            Assert.False(metric.IsAvailable);
            Assert.Null(metric.Value);
        });
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }

    private sealed class FakeProcesses : IFpsProcessSource
    {
        public FpsProcess? Foreground { get; set; }
        public FpsProcess? Specific { get; set; }
        public bool Running { get; set; } = true;
        public FpsProcess? GetForeground() => Foreground;
        public FpsProcess? FindSpecific(string identity) => Specific?.Identity == identity ? Specific : null;
        public bool IsRunning(uint pid) => Running;
        public IReadOnlyCollection<DeviceDescriptor> ListRunning() => [];
    }

    private sealed class FakeClient : IPresentMonClient
    {
        public Dictionary<uint, PresentMonValues> Values { get; } = [];
        public List<uint> Tracked { get; } = [];
        public List<uint> Stopped { get; } = [];
        public int ConnectCalls { get; private set; }
        public int SlowPolls { get; private set; }
        public bool FailPoll { get; set; }
        public Exception? FailWith { get; set; }
        public bool Disposed { get; private set; }
        public bool AdvanceSource { get; set; } = true;
        private static long _nextSourceQpc;
        private ulong _sourceQpc;

        public bool Connect() { ConnectCalls++; return true; }
        public bool Track(uint processId) { Tracked.Add(processId); return true; }
        public void StopTracking(uint processId) => Stopped.Add(processId);
        public PresentMonValues Poll(uint processId, bool pollSlow, bool pollLatency = true)
        {
            if (FailWith is not null) throw FailWith;
            if (FailPoll) throw new InvalidOperationException("simulated disconnect");
            if (pollSlow) SlowPolls++;
            if (AdvanceSource) _sourceQpc = (ulong)Interlocked.Increment(ref _nextSourceQpc);
            // Identical valid samples are returned indefinitely; freshness must not depend on value changes.
            return Values.TryGetValue(processId, out var values)
                ? values with { OnePercentLow = pollSlow ? values.OnePercentLow : null,
                    SourceQpc = _sourceQpc } : default;
        }
        public void Dispose() => Disposed = true;
    }
}
