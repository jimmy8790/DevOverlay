using DevOverlay.Platform.Windows;

namespace DevOverlay.Peripherals;

internal sealed class PeripheralBatteryService : IAsyncDisposable
{
    private readonly IReadOnlyList<IPeripheralBatteryBackend> _backends;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly Dictionary<string, PeripheralBatteryReading> _known = [];
    private readonly object _knownGate = new();
    private Task? _worker;
    private readonly Action<string> _log;
    internal PeripheralBatteryService(IReadOnlyList<IPeripheralBatteryBackend> backends, Action<string>? log = null)
    { _backends = backends; _log = log ?? (message => RuntimeDiagnostics.Write(message)); }
    internal event Action<IReadOnlyList<PeripheralBatteryReading>>? Updated;
    internal IReadOnlyList<PeripheralBatteryReading> Current { get; private set; } = [];
    // Drops only this process's memory of a device. If it is still enumerated it is simply rediscovered by the next refresh.
    internal void Forget(string identity)
    {
        lock (_knownGate)
        {
            if (!_known.Remove(identity)) return;
            Current = _known.Values.OrderBy(item => item.Type).ThenBy(item => item.Identity, StringComparer.Ordinal).ToArray();
        }
    }
    private IReadOnlyList<PeripheralObservation> _lastObservations = [];
    private IReadOnlyList<PeripheralBackendAttempt> _lastAttempts = [];
    internal Task<string> ExportDiagnosticsAsync(CancellationToken token)
    {
        var readings = Current; var observations = _lastObservations; var attempts = _lastAttempts;
        var interfaces = _backends.OfType<WindowsBatteryPropertyBackend>().FirstOrDefault()?.HidDevices ?? [];
        // 내보내기는 기록과 descriptor만 조사한다. 추가 배터리 query/기기 쓰기를 수행하지 않는다.
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var inventory = DevOverlay.Peripherals.VendorBackends.WindowsHidTransport.Inventory(interfaces);
            token.ThrowIfCancellationRequested();
            return PeripheralDiagnostics.Serialize(readings, observations, inventory, attempts);
        }, token);
    }
    internal void Start() => _worker ??= Task.Run(RunAsync);
    internal void RequestRefresh()
    {
        if (_stop.IsCancellationRequested) return;
        try { _changed.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var observations = new List<PeripheralObservation>();
        var attempts = new List<PeripheralBackendAttempt>();
        foreach (var backend in _backends)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var batch = await backend.CollectAsync(timeout.Token).ConfigureAwait(false);
                observations.AddRange(batch);
                attempts.Add(new(backend.Name, "Completed (availability is per device)", batch.Count));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { _log($"[Peripheral] Backend={backend.Name} Timeout"); attempts.Add(new(backend.Name, "Timeout", 0)); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            { _log($"[Peripheral] Backend={backend.Name} Failure={exception.GetType().Name}"); attempts.Add(new(backend.Name, exception.GetType().Name, 0)); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        _lastObservations = observations.ToArray(); _lastAttempts = attempts.ToArray();
        var merged = PeripheralBatteryMerge.Merge(observations);
        var present = merged.Select(item => item.Identity).ToHashSet();
        IReadOnlyList<PeripheralBatteryReading> snapshot;
        lock (_knownGate)
        {
            foreach (var id in _known.Keys.Except(present).ToArray())
                _known[id] = _known[id] with { Connected = false, Percentage = null, Charging = null, Status = "Disconnected or unavailable" };
            foreach (var item in merged)
            {
                if (!_known.TryGetValue(item.Identity, out var previous) ||
                    previous.Connected != item.Connected || previous.Source != item.Source || previous.Status != item.Status ||
                    previous.Percentage.HasValue != item.Percentage.HasValue)
                    _log($"[Peripheral] Device={item.Identity[..12]} Type={item.Type} Backend={item.Source} Connected={item.Connected} Available={item.Percentage.HasValue} Interfaces={observations.Count(row => row.Identity == item.Identity)} Status={item.Status}");
                _known[item.Identity] = item;
            }
            snapshot = Current = _known.Values.OrderBy(item => item.Type).ThenBy(item => item.Identity, StringComparer.Ordinal).ToArray();
        }
        Updated?.Invoke(snapshot);
    }
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await RefreshAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception exception) when (!_stop.IsCancellationRequested)
                {
                    _log($"[Peripheral] Refresh failed: {exception.GetType().Name}");
                    Current = Current.Select(item => item with { Percentage = null, Charging = null, Status = "Refresh unavailable" }).ToArray();
                    try { Updated?.Invoke(Current); } catch (Exception subscriberException)
                    { _log($"[Peripheral] Subscriber failed: {subscriberException.GetType().Name}"); }
                }
                await _changed.WaitAsync(TimeSpan.FromSeconds(45), _stop.Token).ConfigureAwait(false);
                // 도착 이벤트 폭주를 합치되 새 장치는 다음 짧은 지연 후 바로 읽는다.
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        foreach (var backend in _backends.OfType<IDisposable>()) backend.Dispose();
        _changed.Dispose(); _stop.Dispose();
    }
}
