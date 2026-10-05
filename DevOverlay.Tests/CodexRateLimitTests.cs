using System.Text.Json;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class CodexRateLimitTests
{
    [Fact]
    public void ParserKeepsOnlyValidWindowsAndDurationLabelsAreDerived()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":48,"windowDurationMins":300,"resetsAt":1790786403},"secondary":{"usedPercent":73,"windowDurationMins":10080}}}}
            """);

        var snapshot = CodexRateLimitResponseParser.Parse(document.RootElement);

        Assert.Equal(new CodexRateLimitWindow(300, 48, DateTimeOffset.FromUnixTimeSeconds(1790786403)), snapshot.Primary);
        Assert.Equal(new CodexRateLimitWindow(10080, 73, null), snapshot.Secondary);
        Assert.Equal("5H", CodexRateLimitFormatter.FormatDuration(300));
        Assert.Equal("7D", CodexRateLimitFormatter.FormatDuration(10080));
        Assert.Equal("3D", CodexRateLimitFormatter.FormatDuration(4320));
        Assert.Equal("90m", CodexRateLimitFormatter.FormatDuration(90));
    }

    [Fact]
    public void ParserRejectsMalformedPercentageWithoutDiscardingOtherWindow()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimits":{"primary":{"usedPercent":101,"windowDurationMins":300},"secondary":{"usedPercent":73,"windowDurationMins":10080}}}
            """);

        var snapshot = CodexRateLimitResponseParser.Parse(document.RootElement);

        Assert.Null(snapshot.Primary);
        Assert.Equal(73, snapshot.Secondary!.UsedPercent);
    }

    [Fact]
    public void HudSnapshotsUseAtomicDurationPrefixesAndCentralUnavailableMarker()
    {
        var metrics = CodexRateLimitMetricProvider.BuildMetricSnapshots(new CodexRateLimitSnapshot(
            new CodexRateLimitWindow(300, 48, null), new CodexRateLimitWindow(10080, 73, null)));

        var primary = new MetricItemViewModel(metrics.Single(metric => metric.Id == MetricId.CodexPrimaryRateLimit));
        var secondary = new MetricItemViewModel(metrics.Single(metric => metric.Id == MetricId.CodexSecondaryRateLimit));
        var unavailable = new MetricItemViewModel(CodexRateLimitMetricProvider.BuildMetricSnapshots(null).Single());

        Assert.Equal("CX 5H", primary.Prefix);
        Assert.Equal("52%", primary.ValueText);
        Assert.Equal("7D", secondary.Prefix);
        Assert.Equal("27%", secondary.ValueText);
        Assert.Equal("CX N/A", unavailable.Text);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 99)]
    [InlineData(48, 52)]
    [InlineData(81, 19)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(101, 0)]
    public void QuotaPresentationConvertsUsedToRemainingSafely(int used, int expectedRemaining) =>
        Assert.Equal(expectedRemaining, QuotaPercentage.ToRemaining(used));

    [Fact]
    public async Task ProviderStartsOneOwnedClientOnlyWhenAiIsVisibleAndCodexIsEnabled()
    {
        var fake = new FakeClient(new CodexRateLimitSnapshot(new CodexRateLimitWindow(300, 48, null), null));
        var provider = new CodexRateLimitMetricProvider(OverlaySettings.CreateDefault(), () => fake);
        var enabled = OverlaySettings.CreateDefault() with
        {
            EnabledGroups = new HashSet<MetricCategory>(OverlaySettings.CreateDefault().EnabledGroups) { MetricCategory.AiUsage },
            EnabledMetrics = new HashSet<MetricId>(OverlaySettings.CreateDefault().EnabledMetrics)
            {
                MetricId.CodexPrimaryRateLimit, MetricId.CodexSecondaryRateLimit
            }
        };

        await provider.ConfigureAsync(OverlaySettings.CreateDefault() with
        {
            EnabledMetrics = new HashSet<MetricId>(enabled.EnabledMetrics)
        });
        Assert.Equal(0, fake.StartCalls);

        await provider.ConfigureAsync(enabled);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fake.StartCalls);

        await provider.ConfigureAsync(enabled with
        {
            EnabledGroups = new HashSet<MetricCategory>(enabled.EnabledGroups.Where(group => group != MetricCategory.AiUsage))
        });
        Assert.True(fake.Disposed);
        await provider.DisposeAsync();
    }

    [Fact]
    public void ExistingSettingsAppendAiAndKeepCodexDisabledUnlessExplicitlyOptedIn()
    {
        var migrated = OverlaySettings.NormalizeGroupOrder(
            [MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Frame, MetricCategory.Gpu, MetricCategory.Storage]);
        var defaults = OverlaySettings.CreateDefault();

        Assert.Equal(MetricCategory.AiUsage, migrated[^2]);
        Assert.Equal(MetricCategory.PeripheralBattery, migrated[^1]);
        Assert.DoesNotContain(MetricCategory.AiUsage, defaults.EnabledGroups);
        Assert.DoesNotContain(MetricId.CodexPrimaryRateLimit, defaults.EnabledMetrics);
    }

    [Fact]
    public async Task ExplicitRecheck_RerunsDiscoveryWithoutStartingAppServerWhenCodexIsDisabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DevOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var environment = new CodexResolverEnvironment(
                directory, null, null, ".EXE", "C:\\Windows\\System32\\cmd.exe");
            await using var provider = new CodexRateLimitMetricProvider(OverlaySettings.CreateDefault(), null,
                new CodexExecutableResolver(environment));

            await provider.RecheckAsync();
            Assert.Equal(CodexRateLimitState.CliNotFound, provider.State);

            File.WriteAllText(Path.Combine(directory, "codex.exe"), string.Empty);
            await provider.RecheckAsync();
            Assert.Equal(CodexRateLimitState.CliFound, provider.State);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CodexOptInRoundTripsWithoutChangingOtherGroupOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DevOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var before = OverlaySettings.CreateDefault() with
            {
                GroupOrder = [MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Frame,
                    MetricCategory.Latency, MetricCategory.Storage, MetricCategory.AiUsage, MetricCategory.PeripheralBattery],
                EnabledGroups = new HashSet<MetricCategory>(OverlaySettings.CreateDefault().EnabledGroups) { MetricCategory.AiUsage },
                EnabledMetrics = new HashSet<MetricId>(OverlaySettings.CreateDefault().EnabledMetrics)
                {
                    MetricId.CodexPrimaryRateLimit, MetricId.CodexSecondaryRateLimit
                }
            };
            var store = new OverlaySettingsStore(path);
            store.Save(before);
            var after = store.Load();

            Assert.Equal(before.GroupOrder, after.GroupOrder);
            Assert.Contains(MetricCategory.AiUsage, after.EnabledGroups);
            Assert.Contains(MetricId.CodexPrimaryRateLimit, after.EnabledMetrics);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class FakeClient(CodexRateLimitSnapshot snapshot) : ICodexAppServerClient
    {
        private readonly TaskCompletionSource<CodexRefreshReason> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCalls { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource<CodexRefreshReason> Started => _started;
        public event Action? RateLimitsUpdated { add { } remove { } }
        public event Action? Exited { add { } remove { } }

        public Task<CodexRateLimitSnapshot> StartAndReadAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            _started.TrySetResult(CodexRefreshReason.PollInterval);
            return Task.FromResult(snapshot);
        }

        public Task<CodexRateLimitSnapshot> ReadRateLimitsAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);

        public async Task<CodexRefreshReason> WaitForRefreshAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CodexRefreshReason.PollInterval;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
