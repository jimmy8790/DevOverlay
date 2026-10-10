namespace DevOverlay.Metrics.Windows;

internal enum BatteryFlow { Unknown, Charging, Discharging, Ac }
internal enum BatteryLeftSource { Unavailable, Windows, NativeDerived, EstimatedDerived }
// Windows는 충전 완료 시간을 직접 보고하지 않으므로 FULL은 항상 용량/전력 파생이다.
internal enum BatteryFullSource { Unavailable, NativeDerived, EstimatedDerived }

internal sealed record SystemBatteryReading(
    bool IsPresent, double? Percentage, BatteryFlow Flow, double? Watts, double? RemainingSeconds,
    double? TimeToFullSeconds = null, BatteryPowerSource PowerSource = BatteryPowerSource.Unavailable,
    BatteryLeftSource LeftSource = BatteryLeftSource.Unavailable, BatteryFullSource FullSource = BatteryFullSource.Unavailable);

internal readonly record struct BatteryPowerStatus(byte AcLineStatus, byte BatteryFlag, byte Percentage, uint LifeSeconds);
internal readonly record struct BatteryDeviceStatus(uint PowerState, uint Capacity, int Rate);
internal readonly record struct BatteryDeviceReading(uint Capabilities, uint FullCapacity, BatteryDeviceStatus? Status);
internal sealed record BatteryNativeReading(BatteryPowerStatus? PowerStatus, IReadOnlyList<BatteryDeviceReading> Devices, bool EnumerationComplete);

internal static class SystemBatteryInterpreter
{
    public const uint SystemBattery = 0x80000000;
    public const uint RelativeCapacity = 0x40000000;
    public const uint ShortTerm = 0x20000000;
    public const int UnknownRate = int.MinValue;
    public const uint UnknownCapacity = uint.MaxValue;
    public const double MaximumRemainingSeconds = 359940; // 99h 59m: HUD의 대표 폭과 동일한 상한.

    public static SystemBatteryReading Interpret(BatteryNativeReading raw)
    {
        var batteries = raw.Devices.Where(device =>
            (device.Capabilities & SystemBattery) != 0 && (device.Capabilities & ShortTerm) == 0).ToArray();
        var os = raw.PowerStatus;
        var knownNoBattery = os is { BatteryFlag: not 255 } status && (status.BatteryFlag & 128) != 0;
        // 실패한 열거는 '배터리 없음'의 증거가 아니다. 기본 %/시간은 장치 실패와 독립적이다.
        if (knownNoBattery || (raw.EnumerationComplete && raw.Devices.Count > 0 && batteries.Length == 0) ||
            (raw.EnumerationComplete && batteries.Length == 0 && os is null))
            return new(false, null, BatteryFlow.Unknown, null, null);

        var charging = batteries.Any(device => device.Status is { } value && (value.PowerState & 4) != 0);
        var discharging = batteries.Any(device => device.Status is { } value && (value.PowerState & 2) != 0);
        var osCharging = os is { BatteryFlag: not 255 } flags && (flags.BatteryFlag & 8) != 0;
        var flow = os?.AcLineStatus switch
        {
            0 => BatteryFlow.Discharging,
            1 when discharging && !charging && !osCharging => BatteryFlow.Discharging,
            1 when charging || osCharging => BatteryFlow.Charging,
            1 => BatteryFlow.Ac,
            _ when charging && !discharging => BatteryFlow.Charging,
            _ when discharging && !charging => BatteryFlow.Discharging,
            _ => BatteryFlow.Unknown
        };
        double? percentage = os is { Percentage: <= 100 } valid ? valid.Percentage : null;
        var absolute = batteries.Length > 0 && batteries.All(device => (device.Capabilities & RelativeCapacity) == 0);
        var capacitiesValid = absolute && batteries.All(device =>
            device.Status is { Capacity: not UnknownCapacity } && device.FullCapacity is > 0 and not UnknownCapacity &&
            device.Status.Value.Capacity <= device.FullCapacity);
        if (percentage is null && capacitiesValid)
            percentage = 100 * batteries.Sum(device => (double)device.Status!.Value.Capacity) /
                batteries.Sum(device => (double)device.FullCapacity);

        // 부호 있는 순 전력(+저장 증가 / -저장 감소). Native 부호를 그대로 보존한다.
        // 논리 상태와 rate 부호는 독립이다(충전 중 음수, 방전 중 양수 모두 보존). 0/unknown/크기 초과만 N/A.
        double? watts = null;
        if (absolute && batteries.All(device => device.Status is { Rate: not UnknownRate }))
        {
            // 여러 시스템 배터리는 같은 절대 mW 단위일 때만 순 전력으로 합산한다.
            var rate = batteries.Sum(device => (double)device.Status!.Value.Rate);
            var magnitude = Math.Abs(rate) / 1000;
            if (rate != 0 && magnitude <= 999.9 && flow is BatteryFlow.Charging or BatteryFlow.Discharging)
                watts = rate / 1000;
        }
        double? seconds = null;
        var leftSource = BatteryLeftSource.Unavailable;
        if (flow == BatteryFlow.Discharging)
        {
            if (os is { AcLineStatus: 0, LifeSeconds: <= (uint)MaximumRemainingSeconds } life)
            { seconds = life.LifeSeconds; leftSource = BatteryLeftSource.Windows; }
            else if (capacitiesValid && watts is < 0 && batteries.All(device =>
                (device.Status!.Value.PowerState & 6) == 2))
            {
                seconds = DeriveLeft(batteries.Sum(device => (double)device.Status!.Value.Capacity), -watts.Value);
                if (seconds.HasValue) leftSource = BatteryLeftSource.NativeDerived;
            }
        }
        double? timeToFull = null;
        if (flow == BatteryFlow.Charging && batteries.Length > 0 && watts is not < 0)
        {
            // 각 장치의 capacity / rate는 같은 단위 계약이다. relative는 서로 다른
            // 장치끼리 합산하지 않고, 모두 충전을 마칠 때까지의 가장 긴 ETA를 사용한다.
            var estimates = new List<double>();
            foreach (var device in batteries)
            {
                if (device.Status is not { } sample || device.FullCapacity is 0 or UnknownCapacity ||
                    sample.Capacity == UnknownCapacity || sample.Capacity >= device.FullCapacity ||
                    (sample.PowerState & 4) == 0 || sample.Rate <= 0 ||
                    ((device.Capabilities & RelativeCapacity) == 0 && sample.Rate > 999900))
                { estimates.Clear(); break; }
                var estimate = ((double)device.FullCapacity - sample.Capacity) / sample.Rate * 3600;
                if (!double.IsFinite(estimate) || estimate > MaximumRemainingSeconds)
                { estimates.Clear(); break; }
                estimates.Add(estimate);
            }
            if (estimates.Count == batteries.Length) timeToFull = estimates.Max();
        }
        return new(true, percentage, flow, watts, seconds, timeToFull,
            watts.HasValue ? BatteryPowerSource.Native : BatteryPowerSource.Unavailable, leftSource,
            timeToFull.HasValue ? BatteryFullSource.NativeDerived : BatteryFullSource.Unavailable);
    }

    // 호출자는 같은 장치의 절대 mWh 용량·충전 방향·현재 power 유효성을 확인한다.
    internal static double? DeriveFull(double capacityMwh, double fullCapacityMwh, double chargeWatts)
    {
        if (!double.IsFinite(capacityMwh) || !double.IsFinite(fullCapacityMwh) || capacityMwh < 0 ||
            fullCapacityMwh >= UnknownCapacity || fullCapacityMwh <= capacityMwh ||
            !double.IsFinite(chargeWatts) || chargeWatts is <= 0 or > 999.9) return null;
        var seconds = (fullCapacityMwh - capacityMwh) / 1000 / chargeWatts * 3600;
        return double.IsFinite(seconds) && seconds <= MaximumRemainingSeconds ? seconds : null;
    }

    // 호출자는 같은 장치의 절대 mWh 용량·방전 방향·현재 power 유효성을 확인한다.
    internal static double? DeriveLeft(double capacityMwh, double dischargeWatts)
    {
        if (!double.IsFinite(capacityMwh) || capacityMwh < 0 || capacityMwh >= UnknownCapacity ||
            !double.IsFinite(dischargeWatts) || dischargeWatts is <= 0 or > 999.9) return null;
        var seconds = capacityMwh / 1000 / dischargeWatts * 3600;
        return double.IsFinite(seconds) && seconds <= MaximumRemainingSeconds ? seconds : null;
    }
}
