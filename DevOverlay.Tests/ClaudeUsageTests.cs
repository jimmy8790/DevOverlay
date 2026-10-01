using System.Text.Json;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class ClaudeUsageTests
{
    [Fact]
    public void OfficialStatusLineFixtureIsSanitizedToOnlyUsageValues()
    {
        using var document = JsonDocument.Parse("""{"session_id":"private","cwd":"C:/secret","context_window":{"remaining_percentage":70},"rate_limits":{"five_hour":{"used_percentage":48},"seven_day":{"used_percentage":81}}}""");
        Assert.True(ClaudeStatusLineBridge.TryCreateState(document.RootElement, out var state));
        Assert.Equal(70, state.ContextRemainingPercent);
        Assert.Equal(48, state.FiveHourUsedPercent);
        Assert.Equal(81, state.SevenDayUsedPercent);
    }

    [Fact]
    public void ProviderShowsRemainingQuotaWithoutDisplayingContext()
    {
        var now = DateTimeOffset.UtcNow;
        var quota = new ClaudeAccountQuotaState(1, now,
            new ClaudeQuotaWindow(48, now.AddHours(1)), new ClaudeQuotaWindow(81, now.AddDays(1)));
        var metrics = ClaudeUsageMetricProvider.BuildMetricSnapshots(quota, new ClaudeStatusLineState(1, now, 70, null, null), now);
        Assert.Equal(52, metrics.Single(metric => metric.Id == MetricId.ClaudePrimaryRateLimit).Value);
        Assert.Equal(19, metrics.Single(metric => metric.Id == MetricId.ClaudeSecondaryRateLimit).Value);
        Assert.DoesNotContain(metrics, metric => metric.Id == MetricId.ClaudeContextRemaining);
    }

    [Fact]
    public void ExistingCustomStatusLineIsPreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DevOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """{"unrelated":true,"statusLine":{"type":"command","command":"custom.exe"}}""");
            var result = new ClaudeStatusLineConfiguration(path).Configure("C:\\DevOverlay.exe");
            Assert.Equal(ClaudeStatusLineConfigurationResult.ExistingCustomStatusLine, result);
            Assert.Contains("custom.exe", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DisabledClaudeDoesNotReadOrPublishTelemetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DevOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ClaudeStatusLineStateStore(Path.Combine(directory, "state.json"));
            store.Write(new ClaudeStatusLineState(1, DateTimeOffset.UtcNow, 70, 48, null));
            var provider = new ClaudeUsageMetricProvider(OverlaySettings.CreateDefault(), store,
                new CodexExecutableResolver("claude", new CodexResolverEnvironment(directory, null, null, ".EXE", "cmd.exe")));
            var metrics = await provider.CollectAsync(CancellationToken.None);
            Assert.Equal(ClaudeUsageState.Disabled, provider.State);
            Assert.All(metrics, metric => Assert.False(metric.IsAvailable));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UsageParserAcceptsExpectedResultAndKeepsValuesWhenResetParsingFails()
    {
        const string json = """{"is_error":false,"local_command":"usage","result":"Current session: 48% used · resets Oct 2, 8:00pm (Asia/Seoul)\nCurrent week (all models): 81% used · resets unreadable"}""";
        Assert.True(ClaudeUsageCommandParser.TryParse(json, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), out var result));
        Assert.Equal(48, result.FiveHour!.UsedPercent);
        Assert.NotNull(result.FiveHour.ResetAtUtc);
        Assert.Equal(81, result.SevenDay!.UsedPercent);
        Assert.Null(result.SevenDay.ResetAtUtc);
    }

    [Fact]
    public void VerifiedUsageFixtureConvertsZeroAndUsedWeekToRemainingPercentages()
    {
        const string json = """{"is_error":false,"num_turns":0,"local_command":"usage","type":"result","result":"Current session: 0% used · resets Oct 1, 8pm (Asia/Seoul)\r\nCurrent week (all models): 53% used · resets Oct 5, 10am (Asia/Seoul)"}""";
        var now = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        Assert.True(ClaudeUsageCommandParser.TryParse(json, now, out var result));
        var snapshots = ClaudeUsageMetricProvider.BuildMetricSnapshots(new ClaudeAccountQuotaState(1, now, result.FiveHour, result.SevenDay), null, now);
        Assert.Equal(100, snapshots.Single(metric => metric.Id == MetricId.ClaudePrimaryRateLimit).Value);
        Assert.Equal(47, snapshots.Single(metric => metric.Id == MetricId.ClaudeSecondaryRateLimit).Value);
    }

    [Fact]
    public void CompactAmPmResetTimeUsesTheCurrentYearAndDoesNotImmediatelyExpireQuota()
    {
        const string json = """{"is_error":false,"num_turns":0,"local_command":"usage","result":"Current session: 0% used · resets Oct 1, 8pm (Asia/Seoul)\nCurrent week (all models): 53% used · resets Oct 5, 10am (Asia/Seoul)"}""";
        var now = new DateTimeOffset(2026, 10, 1, 16, 0, 0, TimeSpan.FromHours(9));
        Assert.True(ClaudeUsageCommandParser.TryParse(json, now, out var result));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 20, 0, 0, TimeSpan.FromHours(9)), result.FiveHour!.ResetAtUtc);
        var snapshots = ClaudeUsageMetricProvider.BuildMetricSnapshots(new ClaudeAccountQuotaState(1, now, result.FiveHour, result.SevenDay), null, now);
        Assert.All(snapshots, snapshot => Assert.True(snapshot.IsAvailable));
    }

    [Fact]
    public void UsageParserRejectsUnexpectedOrInvalidOutput()
    {
        Assert.False(ClaudeUsageCommandParser.TryParse("""{"is_error":false,"local_command":"usage","result":"Current session: 101% used"}""", DateTimeOffset.UtcNow, out _));
        Assert.False(ClaudeUsageCommandParser.TryParse("""{"is_error":false,"local_command":"other","result":"Current session: 10% used"}""", DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public async Task ProviderUsesCachedQuotaWhenTheCommandFailsAndDoesNotPublishContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DevOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var statusStore = new ClaudeStatusLineStateStore(Path.Combine(directory, "status.json"));
            statusStore.Write(new ClaudeStatusLineState(1, now, 64, null, null));
            var quotaStore = new ClaudeAccountQuotaStateStore(Path.Combine(directory, "quota.json"));
            quotaStore.Write(new ClaudeAccountQuotaState(1, now, new ClaudeQuotaWindow(20, now.AddHours(1)), new ClaudeQuotaWindow(30, now.AddDays(1))));
            var executable = Path.Combine(directory, "claude.exe");
            File.WriteAllText(executable, "test");
            var settings = OverlaySettings.CreateForCurrentFeatures(false, false, false, false, DeviceSelection.Auto, DeviceSelection.Auto, DeviceSelection.SystemDrive,
                aiUsageEnabled: true, claudeUsageEnabled: true);
            var provider = new ClaudeUsageMetricProvider(settings, statusStore, quotaStore,
                new CodexExecutableResolver("claude", new CodexResolverEnvironment(directory, null, null, ".EXE", "cmd.exe")), new FakeClaudeClient(null));

            var metrics = await provider.CollectAsync(CancellationToken.None);
            Assert.Equal(ClaudeUsageState.Cached, provider.State);
            Assert.Equal(80, metrics.Single(metric => metric.Id == MetricId.ClaudePrimaryRateLimit).Value);
            Assert.Equal(70, metrics.Single(metric => metric.Id == MetricId.ClaudeSecondaryRateLimit).Value);
            Assert.DoesNotContain(metrics, metric => metric.Id == MetricId.ClaudeContextRemaining);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ProviderPersistsFreshCommandQuota()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DevOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "claude.exe");
            File.WriteAllText(executable, "test");
            var settings = OverlaySettings.CreateForCurrentFeatures(false, false, false, false, DeviceSelection.Auto, DeviceSelection.Auto, DeviceSelection.SystemDrive,
                aiUsageEnabled: true, claudeUsageEnabled: true);
            var quotaPath = Path.Combine(directory, "quota.json");
            var provider = new ClaudeUsageMetricProvider(settings, new ClaudeStatusLineStateStore(Path.Combine(directory, "status.json")),
                new ClaudeAccountQuotaStateStore(quotaPath), new CodexExecutableResolver("claude", new CodexResolverEnvironment(directory, null, null, ".EXE", "cmd.exe")),
                new FakeClaudeClient(new ClaudeUsageCommandResult(new ClaudeQuotaWindow(40, DateTimeOffset.UtcNow.AddHours(1)), new ClaudeQuotaWindow(60, DateTimeOffset.UtcNow.AddDays(1)))));

            var metrics = await provider.CollectAsync(CancellationToken.None);
            Assert.Equal(ClaudeUsageState.Fresh, provider.State);
            Assert.Equal(60, metrics.Single(metric => metric.Id == MetricId.ClaudePrimaryRateLimit).Value);
            Assert.Equal(40, metrics.Single(metric => metric.Id == MetricId.ClaudeSecondaryRateLimit).Value);
            Assert.NotNull(new ClaudeAccountQuotaStateStore(quotaPath).Read());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ProviderQueriesImmediatelyAndSerializesConcurrentRechecks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DevOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "claude.exe"), "test");
            var settings = OverlaySettings.CreateForCurrentFeatures(false, false, false, false, DeviceSelection.Auto, DeviceSelection.Auto, DeviceSelection.SystemDrive,
                aiUsageEnabled: true, claudeUsageEnabled: true);
            var client = new BlockingClaudeClient(new ClaudeUsageCommandResult(
                new ClaudeQuotaWindow(0, DateTimeOffset.UtcNow.AddHours(1)), new ClaudeQuotaWindow(53, DateTimeOffset.UtcNow.AddDays(1))));
            var provider = new ClaudeUsageMetricProvider(settings, new ClaudeStatusLineStateStore(Path.Combine(directory, "status.json")),
                new ClaudeAccountQuotaStateStore(Path.Combine(directory, "quota.json")), new CodexExecutableResolver("claude",
                    new CodexResolverEnvironment(directory, null, null, ".EXE", "cmd.exe")), client);

            var first = provider.RecheckAsync();
            await client.WaitForFirstCallAsync();
            var second = provider.RecheckAsync();
            await Task.Delay(20);
            Assert.Equal(1, client.CallCount);
            client.Release();
            await Task.WhenAll(first, second);
            Assert.Equal(2, client.CallCount);
            Assert.Equal(1, client.MaximumConcurrentCalls);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FakeClaudeClient(ClaudeUsageCommandResult? result, ClaudeUsageQueryFailure failure = ClaudeUsageQueryFailure.Process) : IClaudeUsageCommandClient
    {
        public ClaudeUsageQueryFailure LastFailure => result is null ? failure : ClaudeUsageQueryFailure.None;
        public Task<ClaudeUsageCommandResult?> QueryAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class BlockingClaudeClient(ClaudeUsageCommandResult result) : IClaudeUsageCommandClient
    {
        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _concurrent;
        public ClaudeUsageQueryFailure LastFailure => ClaudeUsageQueryFailure.None;
        public int CallCount => Volatile.Read(ref _calls);
        public int MaximumConcurrentCalls { get; private set; }
        public async Task<ClaudeUsageCommandResult?> QueryAsync(CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, concurrent);
            Interlocked.Increment(ref _calls);
            _firstCall.TrySetResult();
            try { await _release.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref _concurrent); }
            return result;
        }
        public Task WaitForFirstCallAsync() => _firstCall.Task;
        public void Release() => _release.TrySetResult();
    }
}
