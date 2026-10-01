using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DevOverlay.Configuration;
using DevOverlay.Platform.Windows;

namespace DevOverlay.Metrics.Windows;

internal readonly record struct FpsProcess(uint Pid, string Identity, long StartedAtTicks = 0);

internal interface IFpsProcessSource
{
    FpsProcess? GetForeground();
    FpsProcess? FindSpecific(string identity);
    bool IsRunning(uint pid);
    bool IsRunning(FpsProcess process) => IsRunning(process.Pid);
    IReadOnlyCollection<DeviceDescriptor> ListRunning();
}

internal sealed class WindowsFpsProcessSource : IFpsProcessSource
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "DevOverlay", "DevOverlay.SensorHost", "DevOverlay.SensorService", "PresentMon",
        "PresentMonService", "dwm", "explorer", "ShellExperienceHost", "SearchHost"
    };

    public FpsProcess? GetForeground()
    {
        var window = GetForegroundWindow();
        if (window == 0) return null;
        GetWindowThreadProcessId(window, out var pid);
        return Describe(pid);
    }

    public FpsProcess? FindSpecific(string identity)
    {
        var imageName = Path.GetFileNameWithoutExtension(identity);
        if (string.IsNullOrWhiteSpace(imageName)) return null;
        using var processes = new ProcessCollection(Process.GetProcessesByName(imageName));
        return processes.Items.Select(process => Describe(process))
            .Where(process => process is not null &&
                (string.Equals(process.Value.Identity, identity, StringComparison.OrdinalIgnoreCase) ||
                 (!Path.IsPathRooted(identity) &&
                  string.Equals(Path.GetFileName(process.Value.Identity), identity, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(process => process!.Value.Pid).FirstOrDefault();
    }

    public bool IsRunning(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OverflowException)
        {
            return false;
        }
    }

    public bool IsRunning(FpsProcess target)
    {
        if (!IsRunning(target.Pid)) return false;
        if (target.StartedAtTicks == 0) return true;
        try
        {
            using var process = Process.GetProcessById(checked((int)target.Pid));
            return process.StartTime.ToUniversalTime().Ticks == target.StartedAtTicks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    public IReadOnlyCollection<DeviceDescriptor> ListRunning()
    {
        using var processes = new ProcessCollection(Process.GetProcesses());
        return processes.Items.Select(Describe).Where(process => process is not null)
            .Select(process => process!.Value.Identity).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase)
            .Select(identity => new DeviceDescriptor(identity, Path.GetFileName(identity))).ToArray();
    }

    private static FpsProcess? Describe(uint pid)
    {
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            return process.HasExited || Excluded.Contains(process.ProcessName)
                ? null : new FpsProcess(pid, process.ProcessName + ".exe", StartTicks(process));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OverflowException) { return null; }
    }

    private static FpsProcess? Describe(Process process)
    {
        try
        {
            if (process.HasExited || Excluded.Contains(process.ProcessName)) return null;
            var imageName = process.ProcessName + ".exe";
            string? path;
            try { path = process.MainModule?.FileName; }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            { path = null; }
            return new FpsProcess((uint)process.Id, string.IsNullOrWhiteSpace(path) ? imageName : path, StartTicks(process));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return null; }
    }

    private static long StartTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { return 0; }
    }

    private sealed class ProcessCollection(Process[] items) : IDisposable
    {
        public Process[] Items { get; } = items;
        public void Dispose() { foreach (var process in Items) process.Dispose(); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

/// <summary>One low-rate, background aggregate reader; it never streams frame events to WPF.</summary>
internal sealed class FpsMetricProvider : IMetricProvider, IAsyncDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TargetCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowWarmup = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Only advancing raw Present QPC refreshes freshness; a successful aggregate query alone cannot do so.
    /// Identical FPS with new frames remains fresh. No source progression for this interval invalidates the target
    /// and all its cached frame metrics. Known process exit invalidates immediately. 1% updates only on fresh frames.
    /// </summary>
    internal static readonly TimeSpan FreshnessTimeout = TimeSpan.FromMilliseconds(1500);
    private readonly Func<IPresentMonClient> _clientFactory;
    private readonly IFpsProcessSource _processes;
    private readonly TimeProvider _clock;
    private OverlaySettings _settings;
    private IPresentMonClient? _client;
    private FpsProcess? _target;
    private FpsProcess? _candidate;
    private DateTimeOffset _nextConnect;
    private DateTimeOffset _nextTargetCheck;
    private DateTimeOffset _nextSlowPoll;
    private DateTimeOffset _targetSince;
    private double? _lastLow;
    private DateTimeOffset _lastValidSampleAt;
    private ulong _lastSourceQpc;
    private ulong _invalidatedSourceQpc;
    private DeviceSelection _appliedSelection;
    private bool _connectionFailureReported;
    private bool _lowReported;
    private bool _unavailablePublished;
#if DEBUG
    private string? _diagnosticState;
    private int _diagnosticAvailability = -1;
#endif
    private int _unavailableMask = -1;

    public FpsMetricProvider(OverlaySettings settings, Func<IPresentMonClient>? clientFactory = null,
        IFpsProcessSource? processes = null, TimeProvider? clock = null)
    {
        _settings = settings;
        _appliedSelection = settings.FpsTargetSelection;
        _clientFactory = clientFactory ?? (() => new PresentMonClient());
        _processes = processes ?? new WindowsFpsProcessSource();
        _clock = clock ?? TimeProvider.System;
    }

    public string Name => "PresentMon FPS";
    public TimeSpan RefreshInterval => TimeSpan.FromMilliseconds(250);
    internal FpsProcess? CurrentTarget => _target;
    internal bool HasConnection => _client is not null;

    public void Configure(OverlaySettings settings)
    {
        Volatile.Write(ref _settings, settings);
    }

    public Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = Volatile.Read(ref _settings);
#if DEBUG
        var state = $"FpsVisible={WantsFramePresentation(settings)} LatencyVisible={WantsLatencyPresentation(settings)} CaptureRequired={ShouldCapture(settings)} Connected={HasConnection} TargetPid={_target?.Pid}";
        if (_diagnosticState != state)
        {
            _diagnosticState = state;
            RuntimeDiagnostics.Write($"[PresentMon] {state}");
        }
#endif
        if (!ShouldCapture(settings))
        {
            Disconnect();
            _unavailablePublished = false;
            return Task.FromResult<IReadOnlyCollection<MetricSnapshot>>([]);
        }

        var now = _clock.GetUtcNow();
        try
        {
            if (_appliedSelection != settings.FpsTargetSelection)
            {
                ClearCandidate();
                ClearTarget();
                _appliedSelection = settings.FpsTargetSelection;
                _nextTargetCheck = DateTimeOffset.MinValue;
            }
            if (_client is null)
            {
                if (now < _nextConnect) return Task.FromResult(NoData(settings));
                _nextConnect = now + RetryInterval;
                var client = _clientFactory();
                if (!client.Connect())
                {
                    client.Dispose();
                    if (!_connectionFailureReported)
                    {
                        Debug.WriteLine("PresentMon API unavailable; FPS will retry in the background.");
                        _connectionFailureReported = true;
                    }
                    return Task.FromResult(NoData(settings));
                }
                _client = client;
                _connectionFailureReported = false;
                Debug.WriteLine("PresentMon API connected; aggregate queries register on the first target.");
                _nextTargetCheck = DateTimeOffset.MinValue;
            }

            if (_target is { } target && !_processes.IsRunning(target))
            {
                RuntimeDiagnostics.Write($"[PresentMon] TargetExit Pid={target.Pid} Generation={target.StartedAtTicks}");
                ClearTarget();
            }
            if (now >= _nextTargetCheck)
            {
                _nextTargetCheck = now + TargetCheckInterval;
                FindTarget(settings.FpsTargetSelection);
            }
            if (_candidate is { } candidate)
            {
                var probe = _client.Poll(candidate.Pid, false, WantsLatencyPresentation(settings));
                TraceSource(candidate, probe, now, "Candidate");
                if (HasTargetSample(probe) && probe.SourceQpc > _invalidatedSourceQpc)
                {
                    if (_target is { } old) _client.StopTracking(old.Pid);
                    _target = candidate;
                    _candidate = null;
                    _targetSince = now;
                    _lastValidSampleAt = now;
                    _lastSourceQpc = probe.SourceQpc;
                    _lastLow = null;
                    _lowReported = false;
                    _nextSlowPoll = now + SlowPollInterval;
                    Debug.WriteLine($"PresentMon FPS target selected: {candidate.Identity} (PID {candidate.Pid}).");
                    return Task.FromResult(Publish(probe, null, settings));
                }
            }
            if (_target is not { } active) return Task.FromResult(NoData(settings));
            var pollSlow = WantsOnePercentLow(settings) && now >= _nextSlowPoll && now - _targetSince >= SlowWarmup;
            var values = _client.Poll(active.Pid, pollSlow, WantsLatencyPresentation(settings));
            TraceSource(active, values, now, "Active");
            if (!HasTargetSample(values) || values.SourceQpc <= _lastSourceQpc)
            {
                // No valid result: freshness is not refreshed. Values already shown may stay briefly (jitter),
                // but never past the timeout.
                if (now - _lastValidSampleAt <= FreshnessTimeout) return Task.FromResult<IReadOnlyCollection<MetricSnapshot>>([]);
                RuntimeDiagnostics.Write($"[PresentMon] SourceTimeout Pid={active.Pid} LastQpc={_lastSourceQpc} AgeMs={(now - _lastValidSampleAt).TotalMilliseconds:0}");
                // Re-track so the service's slow window cannot reuse pre-pause frames.
                ClearTarget();
                _nextTargetCheck = DateTimeOffset.MinValue;
                return Task.FromResult(NoData(settings));
            }
            _lastValidSampleAt = now;
            _lastSourceQpc = values.SourceQpc;
            if (pollSlow)
            {
                _nextSlowPoll = now + SlowPollInterval;
                _lastLow = values.OnePercentLow;
                if (!_lowReported && Valid(_lastLow))
                {
                    Debug.WriteLine("PresentMon 1% Low became available after warm-up.");
                    _lowReported = true;
                }
            }
            return Task.FromResult(Publish(values, _lastLow, settings));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // API/service failure: invalidate immediately. Any exception type must do so; previously an unlisted type
            // escaped and left the last numbers on the HUD.
            RuntimeDiagnostics.Write($"[PresentMon] Capture disconnected: {exception}");
            Disconnect();
            _nextConnect = now + RetryInterval;
            return Task.FromResult(NoData(settings));
        }
    }

    [Conditional("DEBUG")]
    private void TraceSource(FpsProcess target, PresentMonValues values, DateTimeOffset now, string stage)
    {
        // A per-frame capture must not be perturbed by this per-poll synchronous log append.
        if (FrameCaptureOptions.IsRequested) return;
        if (Environment.GetEnvironmentVariable("DEVOVERLAY_PRESENTMON_TRACE") != "1" &&
            Environment.GetEnvironmentVariable("DEVOVERLAY_LOW_TRACE") != "1") return;
        RuntimeDiagnostics.Write($"[PresentTrace] {stage} Pid={target.Pid} Generation={target.StartedAtTicks} Source={values.SourceQpc} Previous={_lastSourceQpc} Advanced={values.SourceQpc > _lastSourceQpc} LastFresh={_lastValidSampleAt:O} Valid={HasTargetSample(values)} WarmupSeconds={(now - _targetSince).TotalSeconds:F1}");
    }

    private void FindTarget(DeviceSelection selection)
    {
        if (selection is SpecificDeviceSelection && _target is not null) return;
        FpsProcess? desired = selection is SpecificDeviceSelection specific
            ? _processes.FindSpecific(specific.DeviceId) : _processes.GetForeground();
        if (desired is null)
        {
            if (selection is SpecificDeviceSelection) ClearTarget();
            ClearCandidate();
            return;
        }
        if (_target?.Pid == desired.Value.Pid) { ClearCandidate(); return; }
        if (_candidate?.Pid == desired.Value.Pid) return;
        ClearCandidate();
        if (_client!.Track(desired.Value.Pid))
        {
            _candidate = desired;
            Debug.WriteLine($"PresentMon target tracking started: {desired.Value.Identity} (PID {desired.Value.Pid}).");
        }
        if (selection is SpecificDeviceSelection) ClearTarget();
    }

    private void ClearCandidate()
    {
        if (_candidate is { } candidate) _client?.StopTracking(candidate.Pid);
        _candidate = null;
    }

    private void ClearTarget()
    {
        if (_target is { } old) RuntimeDiagnostics.Write($"[PresentMon] TargetReset Pid={old.Pid} LastSourceQpc={_lastSourceQpc}");
        _invalidatedSourceQpc = Math.Max(_invalidatedSourceQpc, _lastSourceQpc);
        if (_target is { } target) _client?.StopTracking(target.Pid);
        _target = null;
        _lastLow = null;
        _lastSourceQpc = 0;
        _lastValidSampleAt = DateTimeOffset.MinValue;
        _lowReported = false;
    }

    private void Disconnect()
    {
        if (_client is not null) Debug.WriteLine("PresentMon FPS client disconnected.");
        ClearCandidate();
        ClearTarget();
        _client?.Dispose();
        _client = null;
    }

    // HUD visibility is presentation-only: keep PresentMon warm while the user hides the overlay.
    private static bool ShouldCapture(OverlaySettings settings) =>
        WantsFramePresentation(settings) || WantsLatencyPresentation(settings);

    private static bool WantsFramePresentation(OverlaySettings settings) =>
        settings.EnabledGroups.Contains(MetricCategory.Frame) &&
        (settings.EnabledMetrics.Contains(MetricId.FramesPerSecond) ||
         settings.EnabledMetrics.Contains(MetricId.OnePercentLow) ||
         settings.EnabledMetrics.Contains(MetricId.FrameTime));

    private static bool WantsLatencyPresentation(OverlaySettings settings) =>
        settings.EnabledGroups.Contains(MetricCategory.Latency) &&
        settings.EnabledMetrics.Contains(MetricId.Latency);

    private static bool WantsOnePercentLow(OverlaySettings settings) =>
        settings.EnabledGroups.Contains(MetricCategory.Frame) && settings.EnabledMetrics.Contains(MetricId.OnePercentLow);

    private static bool Valid(double? value) => value is > 0 && double.IsFinite(value.Value);
    private static bool ValidLatency(double? value) => value is >= 0 && double.IsFinite(value.Value);
    private static bool HasTargetSample(PresentMonValues values) =>
        values.SourceQpc != 0 && Valid(values.FramesPerSecond);

    private IReadOnlyCollection<MetricSnapshot> NoData(OverlaySettings settings)
    {
        var mask = PresentationMask(settings);
        if (_unavailablePublished && _unavailableMask == mask) return [];
        _unavailableMask = mask;
        _unavailablePublished = true;
        var snapshots = Unavailable(settings);
        LogAvailability(snapshots);
        return snapshots;
    }

    private IReadOnlyCollection<MetricSnapshot> Publish(PresentMonValues values, double? low, OverlaySettings settings)
    {
        _unavailablePublished = false;
        var snapshots = Snapshots(values, low, settings);
        LogAvailability(snapshots);
        return snapshots;
    }

    private static int PresentationMask(OverlaySettings settings) =>
        (settings.EnabledGroups.Contains(MetricCategory.Frame)
            ? (settings.EnabledMetrics.Contains(MetricId.FramesPerSecond) ? 1 : 0) |
              (settings.EnabledMetrics.Contains(MetricId.OnePercentLow) ? 2 : 0) |
              (settings.EnabledMetrics.Contains(MetricId.FrameTime) ? 4 : 0) : 0) |
        (WantsLatencyPresentation(settings) ? 8 : 0);

    [Conditional("DEBUG")]
    private void LogAvailability(IReadOnlyCollection<MetricSnapshot> snapshots)
    {
#if DEBUG
        var mask = 0;
        foreach (var sample in snapshots)
            if (sample.IsAvailable) mask |= sample.Id switch
            {
                MetricId.FramesPerSecond => 1, MetricId.OnePercentLow => 2,
                MetricId.FrameTime => 4, MetricId.Latency => 8, _ => 0
            };
        if (_diagnosticAvailability == mask) return;
        _diagnosticAvailability = mask;
        RuntimeDiagnostics.Write($"[Metrics] FPS={(mask & 1) != 0} OnePercentLow={(mask & 2) != 0} FrameTime={(mask & 4) != 0} RenderLatency={(mask & 8) != 0}");
#endif
    }

    private static IReadOnlyCollection<MetricSnapshot> Snapshots(PresentMonValues values, double? low, OverlaySettings settings)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new List<MetricSnapshot>(4);
        if (settings.EnabledGroups.Contains(MetricCategory.Frame))
        {
            if (settings.EnabledMetrics.Contains(MetricId.FramesPerSecond))
                snapshots.Add(Snapshot(MetricId.FramesPerSecond, MetricCategory.Frame, "FPS", values.FramesPerSecond, "", now));
            if (settings.EnabledMetrics.Contains(MetricId.OnePercentLow))
                snapshots.Add(Snapshot(MetricId.OnePercentLow, MetricCategory.Frame, "1% Low", low, "", now));
            if (settings.EnabledMetrics.Contains(MetricId.FrameTime))
                snapshots.Add(Snapshot(MetricId.FrameTime, MetricCategory.Frame, "Frame Time", values.FrameTimeMs, "ms", now));
        }
        if (WantsLatencyPresentation(settings))
            snapshots.Add(Snapshot(MetricId.Latency, MetricCategory.Latency, "Render Latency", values.RenderLatencyMs, "ms", now));
        return snapshots;
    }

    private static MetricSnapshot Snapshot(MetricId id, MetricCategory category, string name, double? value, string unit,
        DateTimeOffset now)
    {
        var isAvailable = id == MetricId.Latency ? ValidLatency(value) : Valid(value);
        return new MetricSnapshot(id, category, name, isAvailable ? value : null, unit, isAvailable, now);
    }

    private static IReadOnlyCollection<MetricSnapshot> Unavailable(OverlaySettings settings) =>
        Snapshots(default, null, settings);

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }
}
