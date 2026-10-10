using System.Diagnostics;
using DevOverlay.Metrics.Windows;

namespace DevOverlay.Platform.Windows;

internal interface ISystemBatterySource : IDisposable
{
    SystemBatteryReading Read();
    TimeSpan RefreshInterval => TimeSpan.FromSeconds(2);
    void ResetEstimate() { }
}

internal interface ISystemBatteryDevice : IDisposable
{
    bool RefreshInformation(out uint capabilities, out uint fullCapacity);
    bool ReadStatus(out BatteryDeviceStatus status);
    int LastError => 0;
    string? Identity => null;
    uint Tag => 0;
}

internal interface ISystemBatteryNative
{
    BatteryPowerStatus? ReadPowerStatus();
    IReadOnlyList<ISystemBatteryDevice> EnumerateDevices();
}

/// <summary>Owns cached devices; native acquisition and value interpretation remain independent.</summary>
internal sealed class SystemBatterySource(ISystemBatteryNative native, TimeProvider? timeProvider = null) : ISystemBatterySource
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly List<CachedDevice> _devices = [];
    private DateTimeOffset _nextEnumeration = DateTimeOffset.MinValue;
    private bool _enumerationComplete;
    private bool _disposed;
    private BatteryPowerStatus? _previousPower;
    private BatteryFlow? _previousFlow;
    private DateTimeOffset _recoveryUntil = DateTimeOffset.MinValue;
    private bool _recovering;
    private bool _failureWindowUsed;
    private readonly BatteryPowerEstimator _estimator = new();
    private int _resumeReset;
    public void ResetEstimate() => Interlocked.Exchange(ref _resumeReset, 1);
    public BatteryPowerEstimate LastEstimate => _estimator.Last;
    internal static readonly TimeSpan RecoveryWindow = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RecoveryDiscoveryInterval = TimeSpan.FromSeconds(5);
    public TimeSpan RefreshInterval => _recovering && _time.GetUtcNow() < _recoveryUntil
        ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(2);
    public BatteryNativeReading LastNativeReading { get; private set; } = new(null, [], false);
    public int[] LastDeviceErrors => _devices.Select(device => device.Device.LastError).ToArray();

    public SystemBatterySource() : this(new WindowsBatteryNative()) { }

    public SystemBatteryReading Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = _time.GetUtcNow();
        if (Interlocked.Exchange(ref _resumeReset, 0) != 0) _estimator.Reset("SuspendResume");
        BatteryPowerStatus? power = null;
        try { power = native.ReadPowerStatus(); }
        catch (Exception exception) { Debug.WriteLine($"Battery power status unavailable: {exception}"); }
        var changed = HasPowerTransition(_previousPower, power);
        if (changed)
        {
            _estimator.Reset("PowerTransition");
            BeginRecovery(now);
            _nextEnumeration = now; // 정상적인 60초 탐색 타이머를 전환 시 우회한다.
        }
        if (power is not null) _previousPower = power;
        if (now >= _nextEnumeration) Enumerate(now);
        var readings = new List<BatteryDeviceReading>();
        foreach (var cached in _devices)
        {
            BatteryDeviceStatus? status = null;
            try
            {
                if (cached.Device.ReadStatus(out var sample)) status = sample;
                // API 성공이어도 unknown/방향 불일치는 전력 복구 대상이다.
                if ((status is null || NeedsRateRecovery(cached.Capabilities, status, power)) &&
                    cached.Device.RefreshInformation(out var capabilities, out var fullCapacity))
                {
                    // 태그가 바뀐 장치는 정보를 갱신한 뒤 한 번만 다시 조회한다.
                    cached.Capabilities = capabilities; cached.FullCapacity = fullCapacity;
                    status = null; // 새 태그 재조회 실패를 이전 태그의 용량 표본으로 추정하지 않는다.
                    if (cached.Device.ReadStatus(out sample)) status = sample;
                }
            }
            catch (Exception exception) { Debug.WriteLine($"Battery device status unavailable: {exception}"); }
            readings.Add(new(cached.Capabilities, cached.FullCapacity, status));
        }
        LastNativeReading = new(power, readings, _enumerationComplete);
        var reading = SystemBatteryInterpreter.Interpret(LastNativeReading);
        var flowChanged = _previousFlow is { } previous && previous != BatteryFlow.Unknown &&
            reading.Flow != BatteryFlow.Unknown && previous != reading.Flow;
        _previousFlow = reading.Flow;
        if (flowChanged && !changed) BeginRecovery(now);
        var rateMissing = readings.Any(device => NeedsRateRecovery(device.Capabilities, device.Status, power));
        var unhealthy = !_enumerationComplete || rateMissing || (reading.IsPresent && readings.Count == 0);
        if (unhealthy && !_failureWindowUsed) BeginRecovery(now);
        if (!unhealthy)
        {
            _recovering = false; _failureWindowUsed = false;
        }
        else
        {
            if (now >= _recoveryUntil) _recovering = false;
            var next = now + (_recovering ? RecoveryDiscoveryInterval : TimeSpan.FromSeconds(10));
            if (_nextEnumeration > next) _nextEnumeration = next;
        }
        var eligible = _devices.Where(device => (device.Capabilities & SystemBatteryInterpreter.SystemBattery) != 0 &&
            (device.Capabilities & SystemBatteryInterpreter.ShortTerm) == 0).ToArray();
        BatteryPowerEstimate estimate;
        uint estimateFullCapacity = 0;
        if (!reading.IsPresent || !_enumerationComplete || eligible.Length != 1)
            estimate = _estimator.Reset("UnsupportedBatterySet");
        else
        {
            var device = eligible[0];
            var sample = readings.Single(item => item.Capabilities == device.Capabilities);
            if ((device.Capabilities & SystemBatteryInterpreter.RelativeCapacity) != 0 ||
                device.Device.Identity is not { } identity || device.Device.Tag == 0 ||
                sample.Status is not { Capacity: not uint.MaxValue } status ||
                device.FullCapacity is 0 or uint.MaxValue || status.Capacity > device.FullCapacity)
                estimate = _estimator.Reset("InvalidCapacityOrUnits");
            else if (reading.Flow == BatteryFlow.Discharging && (status.PowerState & 6) != 2)
                estimate = _estimator.Reset("ContradictoryDischargeState");
            else if (reading.Flow == BatteryFlow.Charging && (status.PowerState & 6) != 4)
                estimate = _estimator.Reset("ContradictoryChargeState");
            else
            {
                estimate = _estimator.Observe(now, $"{identity}/{device.Device.Tag}/{device.FullCapacity}", reading.Flow, status.Capacity);
                estimateFullCapacity = device.FullCapacity;
            }
        }
        if (reading.Watts.HasValue || !estimate.Watts.HasValue) return reading;
        reading = reading with { Watts = estimate.Watts, PowerSource = BatteryPowerSource.Estimated };
        // 유효 estimate의 끝 용량은 이번 Read에서 확인한 같은 identity/tag의 절대 mWh다.
        // 매번 재계산하므로 reset/stale/방향 전환 이후 이전 LEFT/FULL이 남지 않는다.
        if (reading.Flow == BatteryFlow.Discharging && reading.RemainingSeconds is null && estimate.EndCapacity is { } capacity)
        {
            var seconds = estimate.Watts.Value < 0 ? SystemBatteryInterpreter.DeriveLeft(capacity, -estimate.Watts.Value) : null;
            if (seconds.HasValue) reading = reading with { RemainingSeconds = seconds, LeftSource = BatteryLeftSource.EstimatedDerived };
        }
        else if (reading.Flow == BatteryFlow.Charging && reading.TimeToFullSeconds is null && estimate.Watts.Value > 0 && estimate.EndCapacity is { } charged)
        {
            var seconds = SystemBatteryInterpreter.DeriveFull(charged, estimateFullCapacity, estimate.Watts.Value);
            if (seconds.HasValue) reading = reading with { TimeToFullSeconds = seconds, FullSource = BatteryFullSource.EstimatedDerived };
        }
        return reading;
    }

    private void BeginRecovery(DateTimeOffset now)
    {
        _recovering = true; _failureWindowUsed = true; _recoveryUntil = now + RecoveryWindow;
    }

    private static bool HasPowerTransition(BatteryPowerStatus? previous, BatteryPowerStatus? current) =>
        previous is { } before && current is { } after &&
        ((before.AcLineStatus <= 1 && after.AcLineStatus <= 1 && before.AcLineStatus != after.AcLineStatus) ||
        (before.BatteryFlag != 255 && after.BatteryFlag != 255 && (before.BatteryFlag & 8) != (after.BatteryFlag & 8)));

    private static bool NeedsRateRecovery(uint capabilities, BatteryDeviceStatus? status, BatteryPowerStatus? power)
    {
        if ((capabilities & SystemBatteryInterpreter.SystemBattery) == 0 || (capabilities & SystemBatteryInterpreter.ShortTerm) != 0)
            return false;
        if (status is not { } sample) return true;
        var discharging = power?.AcLineStatus == 0 || (sample.PowerState & 2) != 0;
        var charging = power is { AcLineStatus: 1, BatteryFlag: not 255 } os && (os.BatteryFlag & 8) != 0 || (sample.PowerState & 4) != 0;
        if (!charging && !discharging) return false; // 완충/충전 일시 중지는 rate 0/unknown이어도 정상 AC.
        return sample.Rate == SystemBatteryInterpreter.UnknownRate || sample.Rate == 0 ||
            ((capabilities & SystemBatteryInterpreter.RelativeCapacity) == 0 && Math.Abs((double)sample.Rate) > 999900) ||
            (power?.AcLineStatus == 0 && sample.Rate > 0) ||
            (discharging && !charging && sample.Rate > 0);
    }

    private void Enumerate(DateTimeOffset now)
    {
        CloseDevices();
        _enumerationComplete = false;
        _nextEnumeration = now.AddSeconds(60);
        try
        {
            var candidates = native.EnumerateDevices();
            var allRead = true;
            foreach (var device in candidates)
            {
                var retained = false;
                try
                {
                    if (device.RefreshInformation(out var capabilities, out var fullCapacity))
                    { _devices.Add(new(device, capabilities, fullCapacity)); retained = true; }
                    else allRead = false;
                }
                catch (Exception exception)
                { allRead = false; Debug.WriteLine($"Battery information unavailable: {exception}"); }
                finally { if (!retained) device.Dispose(); }
            }
            _enumerationComplete = allRead;
        }
        catch (Exception exception) { Debug.WriteLine($"Battery enumeration unavailable: {exception}"); }
        // 실패/분리 후 재탐색을 늦춰 장치 없는 PC에서도 비싼 실패 루프를 피한다.
        if (!_enumerationComplete) _nextEnumeration = now.AddSeconds(10);
    }

    private void CloseDevices()
    {
        foreach (var cached in _devices) cached.Device.Dispose();
        _devices.Clear();
    }

    public void Dispose() { if (_disposed) return; _disposed = true; CloseDevices(); }
    private sealed class CachedDevice(ISystemBatteryDevice device, uint capabilities, uint fullCapacity)
    {
        public ISystemBatteryDevice Device { get; } = device;
        public uint Capabilities { get; set; } = capabilities;
        public uint FullCapacity { get; set; } = fullCapacity;
    }
}
