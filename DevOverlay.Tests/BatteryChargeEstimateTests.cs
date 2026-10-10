using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

// Estimated CHG / EstimatedDerived FULL. 모든 시간은 가짜 clock이며 sleep이 없다.
public sealed class BatteryChargeEstimateTests
{
    [Fact]
    public void IncreasingCapacityWhileChargingPublishesEstimatedChg()
    {
        var (_, _, source, last) = Charging();
        using (source)
        {
            Assert.Equal(BatteryFlow.Charging, last.Flow);
            Assert.Equal(36, last.Watts!.Value, 8);
            Assert.Equal(BatteryPowerSource.Estimated, last.PowerSource);
        }
    }

    [Fact]
    public void DecreasingCapacityWhileChargingPublishesNegativeEstimateWithoutFullOrLeft()
    {
        var clock = new Clock(); var native = new Native();
        using var source = new SystemBatterySource(native, clock);
        SystemBatteryReading? value = null;
        for (var t = 0; t <= 20; t += 5) { clock.Seconds = t; native.Capacity = (uint)(42000 - t * 10); value = source.Read(); }
        Assert.Equal(BatteryFlow.Charging, value!.Flow);
        Assert.Equal(-36, value.Watts!.Value, 8);
        Assert.Equal(BatteryPowerSource.Estimated, value.PowerSource);
        Assert.Null(value.TimeToFullSeconds); Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
        Assert.Null(value.RemainingSeconds); Assert.Equal(BatteryLeftSource.Unavailable, value.LeftSource); // AC 연결 상태의 음수 W는 LEFT가 아니다.
        var power = SystemBatteryMetricProvider.CreateSnapshots(value).Single(metric => metric.Id == MetricId.BatteryPower);
        Assert.Equal("-36.0W", MetricTextFormatter.Format(power));
    }

    [Fact]
    public void PositiveToNegativeClearsFullAndNegativeToPositiveRestoresItOnlyWithNewEvidence()
    {
        var (clock, native, source, positive) = Charging();
        using (source)
        {
            Assert.Equal(BatteryFullSource.EstimatedDerived, positive.FullSource);
            // 같은 충전 상태에서 용량이 줄기 시작한다: 기존 양수 평균은 즉시 사라지거나 음수로 바뀌고 FULL은 남지 않는다.
            SystemBatteryReading? value = null;
            for (var t = 20; t <= 60; t += 5)
            {
                clock.Seconds = t; native.Capacity = (uint)(42150 - (t - 15) * 10); value = source.Read();
                Assert.True(value.Watts is null or > 0 or < 0);
                if (value.Watts is < 0) Assert.Null(value.TimeToFullSeconds);
                if (value.Watts is not > 0) Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
            }
            Assert.True(value!.Watts < 0);
            // 다시 증가: 새로운 증가 근거가 충분히 쌓이기 전에는 FULL이 없다.
            clock.Seconds = 65; native.Capacity = value.Watts < 0 ? 41700u : 0u; source.Read();
            var sawPositive = false; var last = 41700u;
            for (var t = 70; t <= 130; t += 5)
            {
                clock.Seconds = t; last += 60; native.Capacity = last; var next = source.Read();
                if (next.Watts is > 0) { sawPositive = true; Assert.Equal(BatteryFullSource.EstimatedDerived, next.FullSource); Assert.NotNull(next.TimeToFullSeconds); }
                else Assert.Null(next.TimeToFullSeconds);
            }
            Assert.True(sawPositive);
        }
    }

    [Fact]
    public void NativeNegativeWhileChargingKeepsItsSignAndNeverProducesFull()
    {
        var clock = new Clock(); var native = new Native { Rate = -6200 };
        using var source = new SystemBatterySource(native, clock);
        var value = source.Read();
        Assert.Equal(BatteryFlow.Charging, value.Flow);
        Assert.Equal(-6.2, value.Watts!.Value, 8); Assert.Equal(BatteryPowerSource.Native, value.PowerSource);
        Assert.Null(value.TimeToFullSeconds); Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
        Assert.Null(value.RemainingSeconds); Assert.Equal(BatteryLeftSource.Unavailable, value.LeftSource);
    }

    [Fact]
    public void NativePositiveKeepsPlusSignAndNativeDerivedFull()
    {
        var clock = new Clock(); var native = new Native { Rate = 18400 };
        using var source = new SystemBatterySource(native, clock);
        var value = source.Read();
        Assert.Equal(18.4, value.Watts!.Value, 8); Assert.Equal(BatteryFullSource.NativeDerived, value.FullSource);
        var power = SystemBatteryMetricProvider.CreateSnapshots(value).Single(metric => metric.Id == MetricId.BatteryPower);
        Assert.Equal("+18.4W", MetricTextFormatter.Format(power));
    }

    [Fact]
    public void NativeNegativeWhileDischargingStillDerivesLeftAndShowsMinus()
    {
        var clock = new Clock(); var native = new Native { Ac = 0, Flag = 1, State = 2, Rate = -19900, Capacity = 62340, Full = 90010 };
        using var source = new SystemBatterySource(native, clock);
        var value = source.Read();
        Assert.Equal(-19.9, value.Watts!.Value, 8);
        Assert.Equal(BatteryLeftSource.NativeDerived, value.LeftSource);
        Assert.Equal(62.34 / 19.9 * 3600, value.RemainingSeconds!.Value, 8);
        var snapshots = SystemBatteryMetricProvider.CreateSnapshots(value);
        Assert.Equal("-19.9W", MetricTextFormatter.Format(snapshots.Single(metric => metric.Id == MetricId.BatteryPower)));
        Assert.Equal("LEFT 3h 7m", MetricTextFormatter.Format(snapshots.Single(metric => metric.Id == MetricId.BatteryRemaining)));
    }

    [Fact]
    public void NativePositiveRateWhileDischargingIsPreservedButNeverDerivesLeft()
    {
        var native = new Native { Ac = 0, Flag = 1, State = 2, Rate = 5000, Full = 90010 };
        using var source = new SystemBatterySource(native, new Clock());
        var value = source.Read();
        Assert.Equal(5, value.Watts!.Value, 8); Assert.Equal(BatteryPowerSource.Native, value.PowerSource);
        Assert.Null(value.RemainingSeconds); Assert.Equal(BatteryLeftSource.Unavailable, value.LeftSource);
        Assert.Null(value.TimeToFullSeconds); Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
        Assert.Equal("+5.0W", MetricTextFormatter.Format(SystemBatteryMetricProvider.CreateSnapshots(value).Single(metric => metric.Id == MetricId.BatteryPower)));
    }

    [Fact]
    public void DischargingWithIncreasingCapacityPublishesPositiveEstimateWithoutLeftOrFull()
    {
        var clock = new Clock(); var native = new Native { Ac = 0, Flag = 1, State = 2 };
        using var source = new SystemBatterySource(native, clock);
        SystemBatteryReading? value = null;
        for (var t = 0; t <= 15; t += 5) { clock.Seconds = t; native.Capacity = (uint)(42000 + t * 10); value = source.Read(); }
        Assert.Equal(BatteryFlow.Discharging, value!.Flow);
        Assert.Equal(36, value.Watts!.Value, 8); Assert.Equal(BatteryPowerSource.Estimated, value.PowerSource);
        Assert.Null(value.RemainingSeconds); Assert.Equal(BatteryLeftSource.Unavailable, value.LeftSource);
        Assert.Null(value.TimeToFullSeconds); Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
    }

    [Fact]
    public void StateTransitionResetsHistoryInBothDirections()
    {
        var clock = new Clock(); var native = new Native();
        using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 15; t += 5) { clock.Seconds = t; native.Capacity = (uint)(42000 + t * 10); source.Read(); }
        Assert.Equal(36, source.LastEstimate.Watts!.Value, 8);
        native.Ac = 0; native.Flag = 1; native.State = 2; clock.Seconds = 20; native.Capacity = 42200;
        Assert.Null(source.Read().Watts); Assert.Equal("PowerTransition", source.LastEstimate.ResetReason);
        for (var t = 25; t <= 35; t += 5) { clock.Seconds = t; native.Capacity = (uint)(42200 - (t - 20) * 10); source.Read(); }
        Assert.Equal(-36, source.LastEstimate.Watts!.Value, 8);
        native.Ac = 1; native.Flag = 8; native.State = 5; clock.Seconds = 40; native.Capacity = 42040;
        Assert.Null(source.Read().Watts);
    }

    [Fact]
    public void FlatCapacityStaysUnavailableUntilMeaningfulIncreaseEvidence()
    {
        var clock = new Clock(); var native = new Native();
        using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 60; t += 5) { clock.Seconds = t; Assert.Null(source.Read().Watts); }
        clock.Seconds = 65; native.Capacity = 42050;
        Assert.Null(source.Read().Watts); // 변화 1회는 충분한 근거가 아니다.
        clock.Seconds = 70; native.Capacity = 42100;
        var value = source.Read();
        Assert.Equal(100 * 3.6 / 60, value.Watts!.Value, 8); // 60초 창의 t=10 표본부터 평균한다.
        Assert.Equal(BatteryPowerSource.Estimated, value.PowerSource);
    }

    [Fact]
    public void InsufficientHistoryIsUnavailable()
    {
        var clock = new Clock(); var native = new Native();
        using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 10; t += 5)
        {
            clock.Seconds = t; native.Capacity = (uint)(42000 + t * 10);
            var value = source.Read(); Assert.Null(value.Watts); Assert.Null(value.TimeToFullSeconds);
        }
        Assert.Equal("InsufficientInterval", source.LastEstimate.Reason);
    }

    [Fact]
    public void EstimatedChgWattsAndFullAreDeterministic()
    {
        var clock = new Clock(); var native = new Native();
        using var source = new SystemBatterySource(native, clock);
        SystemBatteryReading? value = null;
        foreach (var (t, capacity) in new[] { (0, 42000u), (5, 42040u), (10, 42090u), (16, 42150u) })
        { clock.Seconds = t; native.Capacity = capacity; value = source.Read(); }
        // (42150 - 42000) mWh / 1000 / (16 s / 3600) = 33.75 W
        Assert.Equal(150d / 1000 / (16d / 3600), value!.Watts!.Value, 8);
        Assert.Equal(33.75, value.Watts.Value, 8);
        // (60000 - 42150) mWh / 1000 / 33.75 W 시간
        Assert.Equal((60000d - 42150) / 1000 / 33.75 * 3600, value.TimeToFullSeconds!.Value, 8);
        Assert.Equal(BatteryFullSource.EstimatedDerived, value.FullSource);
        var snapshots = SystemBatteryMetricProvider.CreateSnapshots(value);
        Assert.Equal("+33.8W", MetricTextFormatter.Format(snapshots.Single(metric => metric.Id == MetricId.BatteryPower)));
        Assert.Equal("FULL 31m", MetricTextFormatter.Format(snapshots.Single(metric => metric.Id == MetricId.BatteryRemaining)));
    }

    [Fact]
    public void NativeChgOverridesEstimatedChgAndFullImmediately()
    {
        var (clock, native, source, estimated) = Charging();
        using (source)
        {
            Assert.Equal(BatteryPowerSource.Estimated, estimated.PowerSource);
            clock.Seconds = 20; native.Capacity = 42200; native.Rate = 20000;
            var value = source.Read();
            Assert.Equal(20, value.Watts); Assert.Equal(BatteryPowerSource.Native, value.PowerSource);
            Assert.Equal((60000d - 42200) / 20000 * 3600, value.TimeToFullSeconds!.Value, 8);
            Assert.Equal(BatteryFullSource.NativeDerived, value.FullSource);
            Assert.Equal(36, source.LastEstimate.Watts!.Value, 8); // shadow만 유지하고 섞지 않는다.
        }
    }

    [Fact]
    public void NativeUnavailableFallsBackToEstimatedChg()
    {
        var clock = new Clock(); var native = new Native { Rate = 20000 };
        using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 15; t += 5)
        {
            clock.Seconds = t; native.Capacity = (uint)(42000 + t * 10);
            var value = source.Read();
            Assert.Equal(BatteryPowerSource.Native, value.PowerSource); Assert.Equal(BatteryFullSource.NativeDerived, value.FullSource);
        }
        native.Rate = int.MinValue; clock.Seconds = 20; native.Capacity = 42200;
        var fallback = source.Read();
        Assert.Equal(36, fallback.Watts!.Value, 8); Assert.Equal(BatteryPowerSource.Estimated, fallback.PowerSource);
        Assert.Equal((60000d - 42200) / 1000 / 36 * 3600, fallback.TimeToFullSeconds!.Value, 8);
        Assert.Equal(BatteryFullSource.EstimatedDerived, fallback.FullSource);
    }

    [Fact]
    public void ReconnectResetsPriorUseHistory()
    {
        var clock = new Clock(); var native = new Native { Ac = 0, Flag = 1, State = 2 };
        using var source = new SystemBatterySource(native, clock);
        SystemBatteryReading? use = null;
        for (var t = 0; t <= 15; t += 5) { clock.Seconds = t; native.Capacity = (uint)(42150 - t * 10); use = source.Read(); }
        Assert.Equal(BatteryPowerSource.Estimated, use!.PowerSource);
        native.Ac = 1; native.Flag = 8; native.State = 5;
        for (var t = 20; t <= 30; t += 5)
        {
            clock.Seconds = t; native.Capacity = (uint)(42000 + (t - 20) * 10);
            var value = source.Read();
            Assert.Equal(BatteryFlow.Charging, value.Flow);
            Assert.Null(value.Watts); Assert.Null(value.TimeToFullSeconds); Assert.Null(value.RemainingSeconds);
        }
        Assert.Equal("PowerTransition", source.LastEstimate.ResetReason);
        clock.Seconds = 35; native.Capacity = 42150;
        var charged = source.Read();
        Assert.Equal(36, charged.Watts!.Value, 8);
        Assert.Equal(42000u, source.LastEstimate.StartCapacity); // 재연결 첫 표본부터 새 이력이다.
    }

    [Fact]
    public void UseHistoryNeverBecomesChargeEstimate()
    {
        var estimator = new BatteryPowerEstimator(); var start = DateTimeOffset.UnixEpoch;
        for (var t = 0; t <= 15; t += 5) estimator.Observe(start.AddSeconds(t), "battery/1", BatteryFlow.Discharging, (uint)(50000 - t * 10));
        Assert.NotNull(estimator.Last.Watts);
        // 방전 표본(49850)보다 높은 용량을 충전 첫 표본으로 받아도 이전 방전 이력과 이어 계산하지 않는다.
        Assert.Null(estimator.Observe(start.AddSeconds(20), "battery/1", BatteryFlow.Charging, 49900).Watts);
        Assert.Equal("IdentityOrFlowChanged", estimator.Last.ResetReason);
        for (var t = 25; t <= 35; t += 5)
        {
            var result = estimator.Observe(start.AddSeconds(t), "battery/1", BatteryFlow.Charging, (uint)(49900 + (t - 20) * 10));
            Assert.Equal(49900u, result.StartCapacity);
        }
        Assert.Equal(36, estimator.Last.Watts!.Value, 8);
    }

    [Fact]
    public void StaleChargingEstimateExpiresTogetherWithFull()
    {
        var (clock, _, source, _) = Charging();
        using (source)
        {
            for (var t = 20; t <= 45; t += 5)
            {
                clock.Seconds = t;
                var value = source.Read();
                if (t < 45) Assert.Equal(BatteryFullSource.EstimatedDerived, value.FullSource);
                else
                {
                    Assert.Null(value.Watts); Assert.Null(value.TimeToFullSeconds);
                    Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
                    Assert.Equal("StaleCapacity", source.LastEstimate.Reason);
                }
            }
        }
    }

    [Theory]
    [InlineData("tag")] [InlineData("identity")] [InlineData("missingTag")]
    public void TagOrDeviceChangeResetsChargingEstimate(string change)
    {
        var (clock, native, source, _) = Charging();
        using (source)
        {
            clock.Seconds = 20; native.Capacity = 42200;
            if (change == "tag") native.Tag++;
            if (change == "missingTag") native.Tag = 0;
            if (change == "identity") native.Identity = "replacement";
            var value = source.Read();
            Assert.Null(value.Watts); Assert.Null(value.TimeToFullSeconds);
            Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
        }
    }

    [Theory]
    [InlineData("resume")] [InlineData("gap")] [InlineData("backwards")]
    public void SuspendOrTimeDiscontinuityResetsChargingEstimate(string reason)
    {
        var (clock, native, source, _) = Charging();
        using (source)
        {
            clock.Seconds = reason switch { "gap" => 100, "backwards" => 10, _ => 20 };
            if (reason == "resume") source.ResetEstimate();
            native.Capacity = 42200;
            var value = source.Read();
            Assert.Null(value.Watts); Assert.Null(value.TimeToFullSeconds);
            Assert.Equal(reason == "resume" ? "SuspendResume" : "TimeDiscontinuity", source.LastEstimate.ResetReason);
        }
    }

    [Fact]
    public void RelativeUnitBatteryNeverGetsEstimatedWattsOrFull()
    {
        var clock = new Clock(); var native = new Native { Extra = SystemBatteryInterpreter.RelativeCapacity, Full = 100, Capacity = 40 };
        using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 30; t += 5)
        {
            clock.Seconds = t; native.Capacity = (uint)(40 + t / 5);
            var value = source.Read();
            Assert.Null(value.Watts); Assert.Null(value.TimeToFullSeconds);
            Assert.NotEqual(BatteryPowerSource.Estimated, value.PowerSource);
        }
        Assert.Equal("InvalidCapacityOrUnits", source.LastEstimate.Reason);
    }

    [Theory]
    [InlineData(0)] [InlineData(-5)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(1000)]
    public void FullIsInvalidWithoutValidChargePower(double watts) => Assert.Null(SystemBatteryInterpreter.DeriveFull(42000, 60000, watts));

    [Fact]
    public void FullStaysUnavailableUntilEstimatedChgIsValid()
    {
        var clock = new Clock(); var native = new Native();
        using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 10; t += 5)
        {
            clock.Seconds = t; native.Capacity = (uint)(42000 + t * 10);
            var value = source.Read();
            Assert.Null(value.TimeToFullSeconds); Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
        }
        Assert.Equal("N/A", MetricTextFormatter.FormatValue(SystemBatteryMetricProvider.CreateSnapshots(source.Read()).Last()));
    }

    [Fact]
    public void NativeDerivedFullTakesPriorityWhenNativeRecovers()
    {
        var (clock, native, source, estimated) = Charging();
        using (source)
        {
            Assert.Equal(BatteryFullSource.EstimatedDerived, estimated.FullSource);
            clock.Seconds = 20; native.Capacity = 42200; native.Rate = 5000;
            var recovered = source.Read();
            Assert.Equal(BatteryFullSource.NativeDerived, recovered.FullSource);
            Assert.Equal((60000d - 42200) / 5000 * 3600, recovered.TimeToFullSeconds!.Value, 8);
        }
    }

    [Fact]
    public void ChargeToUseClearsEstimatedFullImmediately()
    {
        var (clock, native, source, _) = Charging();
        using (source)
        {
            clock.Seconds = 20; native.Ac = 0; native.Flag = 1; native.State = 2; native.Capacity = 42140;
            var value = source.Read();
            Assert.Equal(BatteryFlow.Discharging, value.Flow);
            Assert.Null(value.TimeToFullSeconds); Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
            Assert.Null(value.Watts); Assert.Null(value.RemainingSeconds);
        }
    }

    [Theory]
    [InlineData("dischargeBit")] [InlineData("bothBits")] [InlineData("unknownCapacity")]
    public void ContradictoryCapacityOrStateClearsFull(string contradiction)
    {
        var (clock, native, source, _) = Charging();
        using (source)
        {
            clock.Seconds = 20;
            native.Capacity = contradiction switch { "unknownCapacity" => uint.MaxValue, _ => 42200 };
            if (contradiction == "dischargeBit") native.State = 3;
            if (contradiction == "bothBits") native.State = 7;
            var value = source.Read();
            Assert.Null(value.Watts); Assert.Null(value.TimeToFullSeconds);
            Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
            // 다음 증가 표본도 폐기된 이력과 이어지지 않는다.
            clock.Seconds = 25; native.State = 5; native.Capacity = 42250;
            Assert.Null(source.Read().TimeToFullSeconds);
        }
    }

    [Fact]
    public void FullCapacityAtOrBelowRemainingProducesNoFull()
    {
        Assert.Null(SystemBatteryInterpreter.DeriveFull(60000, 60000, 30));
        Assert.Null(SystemBatteryInterpreter.DeriveFull(60001, 60000, 30));
        Assert.Null(SystemBatteryInterpreter.DeriveFull(42000, uint.MaxValue, 30));
        Assert.Null(SystemBatteryInterpreter.DeriveFull(-1, 60000, 30));
        var clock = new Clock(); var native = new Native { Full = 42100 };
        using var source = new SystemBatterySource(native, clock);
        SystemBatteryReading? value = null;
        foreach (var (t, capacity) in new[] { (0, 42000u), (5, 42030u), (10, 42060u), (15, 42100u) })
        { clock.Seconds = t; native.Capacity = capacity; value = source.Read(); }
        Assert.Equal(24, value!.Watts!.Value, 8);
        Assert.Null(value.TimeToFullSeconds); Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
    }

    [Fact]
    public void ChargingEstimateNeverProducesLeftAndLeftPriorityIsUnchanged()
    {
        var (_, _, source, charge) = Charging();
        using (source)
        {
            Assert.Null(charge.RemainingSeconds); Assert.Equal(BatteryLeftSource.Unavailable, charge.LeftSource);
        }
        var clock = new Clock(); var native = new Native { Ac = 0, Flag = 1, State = 2 };
        using var discharge = new SystemBatterySource(native, clock);
        SystemBatteryReading? value = null;
        for (var t = 0; t <= 15; t += 5) { clock.Seconds = t; native.Capacity = (uint)(42090 - t * 6); value = discharge.Read(); }
        Assert.Equal(BatteryLeftSource.EstimatedDerived, value!.LeftSource);
        Assert.Null(value.TimeToFullSeconds); Assert.Equal(BatteryFullSource.Unavailable, value.FullSource);
        clock.Seconds = 20; native.Capacity = 41970; native.Rate = -10000;
        Assert.Equal(BatteryLeftSource.NativeDerived, discharge.Read().LeftSource);
        clock.Seconds = 25; native.Capacity = 41940; native.Life = 1234;
        var windows = discharge.Read();
        Assert.Equal(BatteryLeftSource.Windows, windows.LeftSource); Assert.Equal(1234, windows.RemainingSeconds);
    }

    // 충전 중 +10 mWh/s(36 W) 이력. t=15에서 Capacity 42150, Estimated CHG가 유효하다.
    private static (Clock Clock, Native Native, SystemBatterySource Source, SystemBatteryReading Last) Charging()
    {
        var clock = new Clock(); var native = new Native();
        var source = new SystemBatterySource(native, clock);
        SystemBatteryReading? last = null;
        for (var t = 0; t <= 15; t += 5) { clock.Seconds = t; native.Capacity = (uint)(42000 + t * 10); last = source.Read(); }
        return (clock, native, source, last!);
    }
    private sealed class Clock : TimeProvider
    { public int Seconds; public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds); }
    private sealed class Native : ISystemBatteryNative
    {
        public byte Ac = 1, Flag = 8;
        public uint Capacity = 42000, Full = 60000, State = 5, Tag = 1, Extra, Life = uint.MaxValue;
        public int Rate = int.MinValue;
        public string Identity = "battery";
        public BatteryPowerStatus? ReadPowerStatus() => new(Ac, Flag, 67, Life);
        public IReadOnlyList<ISystemBatteryDevice> EnumerateDevices() => [new Device(this)];
    }
    private sealed class Device(Native owner) : ISystemBatteryDevice
    {
        public string Identity => owner.Identity;
        public uint Tag => owner.Tag;
        public bool RefreshInformation(out uint capabilities, out uint full)
        { capabilities = SystemBatteryInterpreter.SystemBattery | owner.Extra; full = owner.Full; return true; }
        public bool ReadStatus(out BatteryDeviceStatus status) { status = new(owner.State, owner.Capacity, owner.Rate); return true; }
        public void Dispose() { }
    }
}
