using System.Text.Json;
using DevOverlay.Configuration;

namespace DevOverlay.Metrics.Windows;

/// <summary>Read-only account rate-limit state exposed by the locally installed Codex App Server.</summary>
public enum CodexRateLimitState
{
    Disabled,
    Starting,
    CliFound,
    Connected,
    CliNotFound,
    ProtocolUnsupported,
    Unavailable,
    Error
}

public sealed record CodexRateLimitWindow(int DurationMinutes, int UsedPercent, DateTimeOffset? ResetsAt);

public sealed record CodexRateLimitSnapshot(CodexRateLimitWindow? Primary, CodexRateLimitWindow? Secondary)
{
    public bool HasAnyWindow => Primary is not null || Secondary is not null;
}

/// <summary>
/// Owns the opt-in Codex monitoring lifecycle. It never reads Codex auth, transcript, or rollout files;
/// the only integration boundary is a child process running the local App Server stdio protocol.
/// </summary>
public sealed class CodexRateLimitMetricProvider : IMetricProvider, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] ReconnectDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private readonly Func<ICodexAppServerClient> _clientFactory;
    private readonly CodexExecutableResolver _resolver;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private ICodexAppServerClient? _client;
    private CodexRateLimitSnapshot? _snapshot;
    private bool _hasPublishedSecondary;
    private bool _required;
    private bool _disposed;

    public CodexRateLimitMetricProvider(OverlaySettings settings, Func<ICodexAppServerClient>? clientFactory = null)
        : this(settings, clientFactory, new CodexExecutableResolver()) { }

    internal CodexRateLimitMetricProvider(OverlaySettings settings, Func<ICodexAppServerClient>? clientFactory,
        CodexExecutableResolver resolver)
    {
        _required = IsRequired(settings);
        _resolver = resolver;
        _clientFactory = clientFactory ?? (() => new CodexAppServerClient(_resolver));
    }

    public string Name => "Codex account rate limits";
    public TimeSpan RefreshInterval => PollInterval;
    public bool UsesFixedRefreshInterval => true;
    public CodexRateLimitState State { get; private set; } = CodexRateLimitState.Disabled;
    public string? Detail { get; private set; }
    public event Action<IReadOnlyCollection<MetricSnapshot>>? MetricsUpdated;
    public event Action<CodexRateLimitState, string?>? StateChanged;

    public async Task ConfigureAsync(OverlaySettings settings)
    {
        var required = IsRequired(settings);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_required == required && (_monitorTask is not null || !required)) return;
            _required = required;
            if (!required)
            {
                await StopMonitorLockedAsync().ConfigureAwait(false);
                _snapshot = null;
                SetState(CodexRateLimitState.Disabled, null);
                return;
            }

            _monitorCancellation = new CancellationTokenSource();
            _monitorTask = MonitorAsync(_monitorCancellation.Token);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        if (_required && _monitorTask is null)
        {
            // Startup uses this path before the first Settings interaction. It returns immediately; the monitor does I/O.
            await ConfigureAsync(new OverlaySettings
            {
                EnabledGroups = new HashSet<MetricCategory> { MetricCategory.AiUsage },
                EnabledMetrics = new HashSet<MetricId> { MetricId.CodexPrimaryRateLimit, MetricId.CodexSecondaryRateLimit }
            }).ConfigureAwait(false);
        }

        return BuildPublishedMetricSnapshots();
    }

    /// <summary>Explicit Settings action: re-reads command discovery, and restarts only this provider when it is enabled.</summary>
    public async Task RecheckAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_required)
            {
                var command = _resolver.Resolve();
                SetState(command is null ? CodexRateLimitState.CliNotFound : CodexRateLimitState.CliFound, null);
                return;
            }

            await StopMonitorLockedAsync().ConfigureAwait(false);
            _monitorCancellation = new CancellationTokenSource();
            _monitorTask = MonitorAsync(_monitorCancellation.Token);
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var retryIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ICodexAppServerClient? client = null;
            try
            {
                SetState(CodexRateLimitState.Starting, null);
                client = _clientFactory();
                client.RateLimitsUpdated += OnRateLimitsUpdated;
                client.Exited += OnClientExited;
                _client = client;
                var snapshot = await client.StartAndReadAsync(cancellationToken).ConfigureAwait(false);
                _snapshot = snapshot;
                retryIndex = 0;
                SetState(CodexRateLimitState.Connected, null);
                PublishSnapshot();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var refreshReason = await client.WaitForRefreshAsync(PollInterval, cancellationToken).ConfigureAwait(false);
                    if (refreshReason == CodexRefreshReason.ProcessExited)
                        throw new CodexAppServerException(CodexAppServerFailure.Connection, "App Server exited.");
                    if (refreshReason == CodexRefreshReason.Notification)
                    {
                        // The protocol notification is sparse. A full read is safer than merging an unknown partial shape.
                        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
                    }
                    _snapshot = await client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
                    PublishSnapshot();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (CodexAppServerException exception)
            {
                _snapshot = null;
                SetState(ToState(exception.Failure), null);
                PublishSnapshot();
                _client = null;
                var delay = ReconnectDelays[Math.Min(retryIndex++, ReconnectDelays.Length - 1)];
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _snapshot = null;
                SetState(CodexRateLimitState.Error, null);
                PublishSnapshot();
                _client = null;
                var delay = ReconnectDelays[Math.Min(retryIndex++, ReconnectDelays.Length - 1)];
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (client is not null)
                {
                    client.RateLimitsUpdated -= OnRateLimitsUpdated;
                    client.Exited -= OnClientExited;
                }
                await DisposeClientAsync(client).ConfigureAwait(false);
                if (ReferenceEquals(_client, client)) _client = null;
            }
        }
    }

    private void OnRateLimitsUpdated() { /* WaitForRefreshAsync observes the coalesced notification. */ }
    private void OnClientExited() { /* WaitForRefreshAsync observes the process-exit signal. */ }

    private static CodexRateLimitState ToState(CodexAppServerFailure failure) => failure switch
    {
        CodexAppServerFailure.CliNotFound => CodexRateLimitState.CliNotFound,
        CodexAppServerFailure.ProtocolUnsupported => CodexRateLimitState.ProtocolUnsupported,
        CodexAppServerFailure.Connection or CodexAppServerFailure.Timeout => CodexRateLimitState.Unavailable,
        _ => CodexRateLimitState.Error
    };

    private void PublishSnapshot() => MetricsUpdated?.Invoke(BuildPublishedMetricSnapshots());

    private IReadOnlyCollection<MetricSnapshot> BuildPublishedMetricSnapshots()
    {
        var metrics = BuildMetricSnapshots(_snapshot).ToList();
        if (_snapshot?.Secondary is not null) _hasPublishedSecondary = true;
        if (_hasPublishedSecondary && metrics.All(metric => metric.Id != MetricId.CodexSecondaryRateLimit))
            metrics.Add(MetricSnapshot.Unavailable(MetricId.CodexSecondaryRateLimit, MetricCategory.AiUsage, string.Empty, "%"));
        return metrics;
    }

    public static IReadOnlyCollection<MetricSnapshot> BuildMetricSnapshots(CodexRateLimitSnapshot? snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        if (snapshot is null || !snapshot.HasAnyWindow)
            return [MetricSnapshot.Unavailable(MetricId.CodexPrimaryRateLimit, MetricCategory.AiUsage, "CX", "%")];

        var metrics = new List<MetricSnapshot>(2);
        if (snapshot.Primary is { } primary)
            metrics.Add(Available(MetricId.CodexPrimaryRateLimit, $"CX {CodexRateLimitFormatter.FormatDuration(primary.DurationMinutes)}", primary, now));
        else
            metrics.Add(MetricSnapshot.Unavailable(MetricId.CodexPrimaryRateLimit, MetricCategory.AiUsage, "CX", "%"));

        if (snapshot.Secondary is { } secondary)
            metrics.Add(Available(MetricId.CodexSecondaryRateLimit, CodexRateLimitFormatter.FormatDuration(secondary.DurationMinutes), secondary, now));
        return metrics;
    }

    private static MetricSnapshot Available(MetricId id, string displayName, CodexRateLimitWindow window, DateTimeOffset now) =>
        new(id, MetricCategory.AiUsage, displayName, QuotaPercentage.ToRemaining(window.UsedPercent), "%", true, now);

    private async Task StopMonitorLockedAsync()
    {
        var cancellation = _monitorCancellation;
        var task = _monitorTask;
        _monitorCancellation = null;
        _monitorTask = null;
        cancellation?.Cancel();
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
    }

    private static async ValueTask DisposeClientAsync(ICodexAppServerClient? client)
    {
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsRequired(OverlaySettings settings) =>
        settings.EnabledGroups.Contains(MetricCategory.AiUsage) &&
        settings.EnabledMetrics.Contains(MetricId.CodexPrimaryRateLimit);

    private void SetState(CodexRateLimitState state, string? detail)
    {
        if (State == state && Detail == detail) return;
        State = state;
        Detail = detail;
        StateChanged?.Invoke(state, detail);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _required = false;
            await StopMonitorLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CodexRateLimitMetricProvider));
    }
}

public static class CodexRateLimitFormatter
{
    public static string FormatDuration(int minutes)
    {
        if (minutes > 0 && minutes % 1440 == 0) return $"{minutes / 1440}D";
        if (minutes > 0 && minutes % 60 == 0) return $"{minutes / 60}H";
        return $"{Math.Max(0, minutes)}m";
    }
}
