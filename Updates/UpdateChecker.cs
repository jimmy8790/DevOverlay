namespace DevOverlay.Updates;

internal enum UpdateState { Checking, UpToDate, UpdateAvailable, Unavailable }

internal sealed record UpdateStatus(UpdateState State, ReleaseVersion? Latest = null, string? ReleaseUrl = null,
    UpdateFailure Failure = UpdateFailure.None, int? StatusCode = null, bool BuildIsNewer = false)
{
    internal static UpdateStatus Checking { get; } = new(UpdateState.Checking);
}

// Independent of every telemetry provider: runs only when Settings asks, never on a timer, and keeps one small in-memory result.
internal sealed class UpdateChecker
{
    internal static readonly TimeSpan SuccessCacheLifetime = TimeSpan.FromHours(6);
    internal static readonly TimeSpan FailureCacheLifetime = TimeSpan.FromMinutes(10);
    // Rapid manual clicks within this window reuse the request that is already running instead of starting another.
    internal static readonly TimeSpan ManualRequestSpacing = TimeSpan.FromSeconds(3);

    private readonly IReleaseSource _source;
    private readonly ReleaseVersion? _running;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private UpdateStatus? _cached;
    private DateTimeOffset _cachedAt;
    private Task<UpdateStatus>? _inFlight;
    private CancellationTokenSource? _inFlightCancel;
    private DateTimeOffset _inFlightStartedAt;
    private long _generation;

    internal UpdateChecker(IReleaseSource source, ReleaseVersion? running, Func<DateTimeOffset>? clock = null)
    { _source = source; _running = running; _clock = clock ?? (() => DateTimeOffset.UtcNow); }

    // Raised with Checking when a request starts and with the final status of the newest request only.
    internal event Action<UpdateStatus>? StatusChanged;

    internal UpdateStatus? Cached { get { lock (_gate) return _cached; } }

    internal Task<UpdateStatus> CheckAsync(bool force, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? supersede;
        CancellationTokenSource cancel;
        TaskCompletionSource<UpdateStatus> completion;
        long generation;
        lock (_gate)
        {
            var now = _clock();
            if (!force && _cached is not null && now - _cachedAt < CacheLifetime(_cached)) return Task.FromResult(_cached);
            if (_inFlight is not null && (!force || now - _inFlightStartedAt < ManualRequestSpacing)) return _inFlight;
            supersede = _inFlightCancel;
            generation = ++_generation;
            cancel = new CancellationTokenSource();
            completion = new TaskCompletionSource<UpdateStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightCancel = cancel;
            _inFlightStartedAt = now;
            _inFlight = completion.Task;
        }
        supersede?.Cancel();
        // Checking is raised before the request starts so a very fast answer can never be overwritten by it.
        StatusChanged?.Invoke(UpdateStatus.Checking);
        _ = RunAsync(generation, cancel, completion, cancellationToken);
        return completion.Task;
    }

    private async Task RunAsync(long generation, CancellationTokenSource cancel, TaskCompletionSource<UpdateStatus> completion, CancellationToken outer)
    {
        UpdateStatus status;
        var publish = true;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, outer);
            var fetched = await _source.FetchAsync(linked.Token).ConfigureAwait(false);
            status = Evaluate(fetched, _running);
        }
        catch (OperationCanceledException) { status = new UpdateStatus(UpdateState.Unavailable, Failure: UpdateFailure.Timeout); publish = false; }
        catch (Exception) { status = new UpdateStatus(UpdateState.Unavailable, Failure: UpdateFailure.MalformedResponse); }
        try { completion.SetResult(Finish(generation, cancel, status, publish)); }
        catch (Exception exception) { completion.SetException(exception); }
    }

    private UpdateStatus Finish(long generation, CancellationTokenSource cancel, UpdateStatus status, bool publish)
    {
        bool current;
        lock (_gate)
        {
            current = generation == _generation;
            if (current)
            {
                _inFlight = null; _inFlightCancel = null;
                if (publish) { _cached = status; _cachedAt = _clock(); }
            }
        }
        cancel.Dispose();
        if (current && publish) StatusChanged?.Invoke(status);
        else if (current) StatusChanged?.Invoke(_cached ?? status);
        return current ? status : _cached ?? status;
    }

    private static TimeSpan CacheLifetime(UpdateStatus status) =>
        status.State == UpdateState.Unavailable ? FailureCacheLifetime : SuccessCacheLifetime;

    internal static UpdateStatus Evaluate(ReleaseFetchResult fetched, ReleaseVersion? running)
    {
        if (fetched.Failure != UpdateFailure.None) return new(UpdateState.Unavailable, Failure: fetched.Failure, StatusCode: fetched.StatusCode);
        if (fetched.Releases.Count == 0) return new(UpdateState.Unavailable, Failure: UpdateFailure.NoRelease);
        if (GitHubReleaseSource.LatestStable(fetched.Releases) is not { } latest)
            return new(UpdateState.Unavailable, Failure: UpdateFailure.NoRelease);
        if (running is not { } current) return new(UpdateState.Unavailable, latest, Failure: UpdateFailure.UnknownInstalledVersion);
        var url = AppLinks.ReleasePageUrl(latest);
        return latest > current
            ? new UpdateStatus(UpdateState.UpdateAvailable, latest, url)
            : new UpdateStatus(UpdateState.UpToDate, latest, url, BuildIsNewer: current > latest);
    }
}
