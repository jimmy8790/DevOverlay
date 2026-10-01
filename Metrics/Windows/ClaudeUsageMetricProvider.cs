using DevOverlay.Configuration;
using System.IO;
using DevOverlay.Platform.Windows;

namespace DevOverlay.Metrics.Windows;

public enum ClaudeUsageState { Disabled, CliNotFound, Querying, Fresh, Cached, QueryFailed, ParseFailed, Error }

/// <summary>Read-only account quota from Claude Code's local /usage command, with statusLine retained only as a fallback.</summary>
public sealed class ClaudeUsageMetricProvider : IMetricProvider
{
    private static readonly TimeSpan StatusLineFreshness = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UnknownResetFreshness = TimeSpan.FromMinutes(5);
    private readonly ClaudeStatusLineStateStore _statusLineStore;
    private readonly ClaudeAccountQuotaStateStore _quotaStore;
    private readonly CodexExecutableResolver _resolver;
    private readonly IClaudeUsageCommandClient _client;
    private readonly SemaphoreSlim _queryGate = new(1, 1);
    private bool _enabled;
    private ClaudeAccountQuotaState? _cachedQuota;

    public ClaudeUsageMetricProvider(OverlaySettings settings) : this(settings, new ClaudeStatusLineStateStore(), new ClaudeAccountQuotaStateStore(), new CodexExecutableResolver("claude"), null) { }
    internal ClaudeUsageMetricProvider(OverlaySettings settings, ClaudeStatusLineStateStore statusLineStore, CodexExecutableResolver resolver)
        : this(settings, statusLineStore, new ClaudeAccountQuotaStateStore(), resolver, null) { }
    internal ClaudeUsageMetricProvider(OverlaySettings settings, ClaudeStatusLineStateStore statusLineStore, ClaudeAccountQuotaStateStore quotaStore,
        CodexExecutableResolver resolver, IClaudeUsageCommandClient? client)
    {
        _statusLineStore = statusLineStore;
        _quotaStore = quotaStore;
        _resolver = resolver;
        _client = client ?? new ClaudeUsageCommandClient(resolver);
        Configure(settings);
    }

    public string Name => "Claude Code usage";
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(60);
    public bool UsesFixedRefreshInterval => true;
    public ClaudeUsageState State { get; private set; }
    public string Detail { get; private set; } = "Claude usage is disabled.";
    public event Action<ClaudeUsageState, string>? StateChanged;

    public void Configure(OverlaySettings settings)
    {
        _enabled = settings.EnabledGroups.Contains(MetricCategory.AiUsage) && settings.EnabledMetrics.Contains(MetricId.ClaudePrimaryRateLimit);
        _cachedQuota = _quotaStore.Read();
        if (!_enabled) SetState(ClaudeUsageState.Disabled, "Claude usage is disabled.");
    }

    public async Task RecheckAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabled) { SetState(ClaudeUsageState.Disabled, "Claude usage is disabled."); return; }
        await _queryGate.WaitAsync(cancellationToken);
        try { await RecheckCoreAsync(cancellationToken); }
        finally { _queryGate.Release(); }
    }

    private async Task RecheckCoreAsync(CancellationToken cancellationToken)
    {
        _cachedQuota ??= _quotaStore.Read();
        if (_resolver.Resolve() is null)
        {
            var cached = HasCachedQuota(DateTimeOffset.UtcNow);
            SetState(cached ? ClaudeUsageState.Cached : ClaudeUsageState.CliNotFound,
                cached ? "Claude CLI unavailable; showing the last valid quota." : "Claude CLI was not found.");
            return;
        }

        SetState(ClaudeUsageState.Querying, "Refreshing account quota from the local Claude CLI.");
        var result = await _client.QueryAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (result is not null && (result.FiveHour is not null || result.SevenDay is not null))
        {
            _cachedQuota = new ClaudeAccountQuotaState(1, now,
                result.FiveHour ?? _cachedQuota?.FiveHour,
                result.SevenDay ?? _cachedQuota?.SevenDay);
            try { _quotaStore.Write(_cachedQuota); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { RuntimeDiagnostics.Write($"[Claude] Could not persist quota cache: {exception.GetType().Name}"); }
            SetState(ClaudeUsageState.Fresh, "Account quota refreshed from the local Claude CLI.");
            return;
        }

        var fallback = HasCachedQuota(now);
        var parseFailure = _client.LastFailure == ClaudeUsageQueryFailure.Parse;
        SetState(fallback ? ClaudeUsageState.Cached : parseFailure ? ClaudeUsageState.ParseFailed : ClaudeUsageState.QueryFailed,
            fallback ? "Claude quota refresh failed; showing the last valid quota." : parseFailure ? "Claude quota response could not be parsed." : "Claude quota query failed.");
    }

    public async Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            SetState(ClaudeUsageState.Disabled, "Claude usage is disabled.");
            return BuildMetricSnapshots(null, null, DateTimeOffset.UtcNow);
        }
        await RecheckAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var statusLine = _statusLineStore.Read();
        var freshStatusLine = statusLine is not null && now - statusLine.UpdatedAtUtc <= StatusLineFreshness ? statusLine : null;
        var snapshots = BuildMetricSnapshots(UsableQuota(now), freshStatusLine, now);
        RuntimeDiagnostics.Write($"[Claude] Stage=Publish 5H={snapshots.Any(metric => metric.Id == MetricId.ClaudePrimaryRateLimit && metric.IsAvailable)} 7D={snapshots.Any(metric => metric.Id == MetricId.ClaudeSecondaryRateLimit && metric.IsAvailable)} Source={(State == ClaudeUsageState.Fresh ? "Cli" : State == ClaudeUsageState.Cached ? "Cache" : "FallbackOrUnavailable")}");
        return snapshots;
    }

    internal static IReadOnlyCollection<MetricSnapshot> BuildMetricSnapshots(ClaudeAccountQuotaState? quota, ClaudeStatusLineState? statusLine, DateTimeOffset now)
    {
        var fiveHour = quota?.FiveHour?.UsedPercent ?? statusLine?.FiveHourUsedPercent;
        var sevenDay = quota?.SevenDay?.UsedPercent ?? statusLine?.SevenDayUsedPercent;
        return
        [
            Snapshot(MetricId.ClaudePrimaryRateLimit, "CL 5H", fiveHour is { } five ? QuotaPercentage.ToRemaining(five) : null, now),
            Snapshot(MetricId.ClaudeSecondaryRateLimit, "7D", sevenDay is { } seven ? QuotaPercentage.ToRemaining(seven) : null, now)
        ];
    }

    private ClaudeAccountQuotaState? UsableQuota(DateTimeOffset now)
    {
        if (_cachedQuota is null) return null;
        var fiveHour = IsUsable(_cachedQuota.FiveHour, _cachedQuota.LastSuccessfulQueryUtc, now) ? _cachedQuota.FiveHour : null;
        var sevenDay = IsUsable(_cachedQuota.SevenDay, _cachedQuota.LastSuccessfulQueryUtc, now) ? _cachedQuota.SevenDay : null;
        return fiveHour is null && sevenDay is null ? null : _cachedQuota with { FiveHour = fiveHour, SevenDay = sevenDay };
    }

    private bool HasCachedQuota(DateTimeOffset now) => UsableQuota(now) is not null;
    private static bool IsUsable(ClaudeQuotaWindow? window, DateTimeOffset queryAt, DateTimeOffset now) => window is not null &&
        (window.ResetAtUtc is { } reset ? reset > now : now - queryAt <= UnknownResetFreshness);
    private static MetricSnapshot Snapshot(MetricId id, string name, int? value, DateTimeOffset timestamp) => value is { } available
        ? new MetricSnapshot(id, MetricCategory.AiUsage, name, available, "%", true, timestamp)
        : MetricSnapshot.Unavailable(id, MetricCategory.AiUsage, name, "%");
    private void SetState(ClaudeUsageState state, string detail)
    {
        if (State == state && Detail == detail) return;
        State = state;
        Detail = detail;
        StateChanged?.Invoke(state, detail);
    }
}
