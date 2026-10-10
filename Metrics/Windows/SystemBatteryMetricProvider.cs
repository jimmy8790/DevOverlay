using System.Diagnostics;
using DevOverlay.Platform.Windows;
using Microsoft.Win32;

namespace DevOverlay.Metrics.Windows;

public sealed class SystemBatteryMetricProvider : IMetricProvider, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<ISystemBatterySource> _factory;
    private ISystemBatterySource? _source;
    private bool _disposed;
    private readonly bool _observePowerEvents;
    public SystemBatteryMetricProvider() : this(() => new SystemBatterySource())
    { _observePowerEvents = true; }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    { if (args.Mode is PowerModes.Suspend or PowerModes.Resume) ResetEstimate(); }
    internal void ResetEstimate() { lock (_gate) { if (!_disposed) _source?.ResetEstimate(); } }
    internal SystemBatteryMetricProvider(Func<ISystemBatterySource> factory) => _factory = factory;
    public string Name => "Windows system battery";
    public TimeSpan RefreshInterval { get { lock (_gate) return _source?.RefreshInterval ?? TimeSpan.FromSeconds(2); } }
    public bool UsesFixedRefreshInterval => true;

    public Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SystemBatteryReading reading;
            try
            {
                if (_source is null)
                {
                    _source = _factory();
                    if (_observePowerEvents) SystemEvents.PowerModeChanged += OnPowerModeChanged;
                }
                reading = _source.Read();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"System battery unavailable: {exception}");
                reading = new(true, null, BatteryFlow.Unknown, null, null);
            }
            return Task.FromResult(CreateSnapshots(reading));
        }
    }

    internal static IReadOnlyCollection<MetricSnapshot> CreateSnapshots(SystemBatteryReading reading)
    {
        var now = DateTimeOffset.UtcNow;
        // 방향 라벨은 표시하지 않는다: 부호 있는 W(+유입/-유출)가 방향을 나타내고, 논리 상태는 내부 판단용이다.
        // 완충/유휴 AC만 전력 칸에 "AC"를 표시한다.
        var direction = reading.Flow == BatteryFlow.Ac ? "AC" : string.Empty;
        return [Create(MetricId.BatteryCharge, "Battery", reading.Percentage, "%"),
            Create(MetricId.BatteryPower, direction, reading.Watts, "W"),
            Create(MetricId.BatteryRemaining, reading.Flow switch
                { BatteryFlow.Discharging => "LEFT", BatteryFlow.Charging => "FULL", _ => string.Empty },
                reading.Flow == BatteryFlow.Charging ? reading.TimeToFullSeconds : reading.RemainingSeconds, "s")];
        MetricSnapshot Create(MetricId id, string name, double? value, string unit) =>
            new(id, MetricCategory.Battery, name, value, unit, value.HasValue, now, reading.IsPresent);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { if (!_disposed) { _disposed = true; SystemEvents.PowerModeChanged -= OnPowerModeChanged; _source?.Dispose(); } }
        return ValueTask.CompletedTask;
    }
}
