using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class BatteryPowerEstimatorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static BatteryPowerEstimate Add(BatteryPowerEstimator estimator, int seconds, uint capacity,
        BatteryFlow flow = BatteryFlow.Discharging, string identity = "battery/1") => estimator.Observe(Start.AddSeconds(seconds), identity, flow, capacity);

    [Fact]
    public void NoHistoryAndOneSampleAreUnavailable()
    { var e = new BatteryPowerEstimator(); Assert.Null(e.Last.Watts); Assert.Null(Add(e, 0, 50000).Watts); }

    [Fact]
    public void FlatCapacityIsNeverZeroWatts()
    { var e = new BatteryPowerEstimator(); for (var t = 0; t <= 90; t += 5) Assert.Null(Add(e, t, 50000).Watts); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ValidDirectionalAverageUsesMwhNotPercent(bool charging)
    {
        var e = new BatteryPowerEstimator(); BatteryPowerEstimate? result = null;
        for (var t = 0; t <= 15; t += 5) result = Add(e, t, (uint)(50000 + (charging ? t : -t) * 10), charging ? BatteryFlow.Charging : BatteryFlow.Discharging);
        Assert.Equal(charging ? 36 : -36, result!.Watts); Assert.Equal(15, result.ElapsedSeconds);
    }

    [Fact]
    public void StepwiseHistoryDoesNotAlternateBetweenZeroAndSpikes()
    {
        var e = new BatteryPowerEstimator(); var published = new List<double>();
        for (var t = 0; t <= 120; t++)
        {
            var result = Add(e, t, (uint)(50000 - t / 5 * 50));
            if (t >= 15) { Assert.NotNull(result.Watts); published.Add(result.Watts!.Value); }
            Assert.True(result.ElapsedSeconds <= 60);
        }
        Assert.All(published, watts => Assert.InRange(watts, -36, -28));
        Assert.Equal(-36, published.Last());
    }

    [Fact]
    public void OneChangeIsNotEnoughEvenAfterMinimumInterval()
    { var e = new BatteryPowerEstimator(); Add(e, 0, 50000); for (var t = 5; t <= 20; t += 5) Assert.Null(Add(e, t, 49900).Watts); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectionTransitionClearsOldEstimate(bool toCharging)
    {
        var e = new BatteryPowerEstimator(); var old = toCharging ? BatteryFlow.Discharging : BatteryFlow.Charging;
        for (var t = 0; t <= 15; t += 5) Add(e, t, (uint)(50000 + (toCharging ? -t : t) * 10), old);
        Assert.NotNull(e.Last.Watts);
        Assert.Null(Add(e, 20, 50000, toCharging ? BatteryFlow.Charging : BatteryFlow.Discharging).Watts);
    }

    [Theory]
    [InlineData("replacement/1")] [InlineData("battery/2")]
    public void IdentityOrTagChangeResetsHistory(string identity)
    { var e = Ready(); Assert.Null(Add(e, 20, 49700, identity: identity).Watts); }

    [Theory]
    [InlineData(15)] [InlineData(10)] [InlineData(1000)]
    public void ZeroBackwardsOrLongTimeDiscontinuityResets(int seconds)
    { var e = Ready(); var result = Add(e, seconds, 49800); Assert.Null(result.Watts); Assert.Equal("TimeDiscontinuity", result.ResetReason); }

    [Fact]
    public void ExplicitResumeResetCannotReuseHistory()
    { var e = Ready(); e.Reset("SuspendResume"); Assert.Null(Add(e, 16, 49800).Watts); Assert.Equal("SuspendResume", e.Last.ResetReason); }

    [Theory]
    [InlineData(40000u, "ImplausibleCapacityJump")] [InlineData(60000u, "ImplausibleCapacityJump")]
    public void MassiveCapacityChangeInEitherDirectionIsRejected(uint capacity, string reason)
    { var e = new BatteryPowerEstimator(); Add(e, 0, 50000); Assert.Null(Add(e, 5, capacity).Watts); Assert.Equal(reason, e.Last.Reason); }

    [Fact]
    public void ChargingCapacityDecreaseIsNegativeNetPowerNotChargePower()
    {
        // 논리 상태가 충전이어도 저장 에너지가 줄면 부호 있는 음수 전력이다. 양수 CHG로 바꾸지도, 억제하지도 않는다.
        var e = new BatteryPowerEstimator(); BatteryPowerEstimate? result = null;
        for (var t = 0; t <= 15; t += 5) result = Add(e, t, (uint)(50000 - t * 10), BatteryFlow.Charging);
        Assert.Equal(-36, result!.Watts); Assert.Equal("Ready", result.Reason);
    }

    [Fact]
    public void DischargingCapacityIncreaseIsPositiveNetPower()
    {
        var e = new BatteryPowerEstimator(); BatteryPowerEstimate? result = null;
        for (var t = 0; t <= 15; t += 5) result = Add(e, t, (uint)(50000 + t * 10), BatteryFlow.Discharging);
        Assert.Equal(36, result!.Watts); Assert.Equal("Ready", result.Reason);
    }

    [Fact]
    public void UnknownCapacityResetsHistory()
    { var e = Ready(); Assert.Null(Add(e, 20, uint.MaxValue).Watts); Assert.Equal("UnknownCapacity", e.Last.Reason); }

    [Theory]
    [InlineData(0u)] [InlineData(2u)]
    public void MissingTagOrChangingTagCannotReuseEstimate(uint tag)
    {
        var clock = new Clock(); var native = new Native(); using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 15; t += 5) { clock.Seconds = t; native.Capacity = (uint)(50000 - t * 10); source.Read(); }
        native.Tag = tag; clock.Seconds = 20; native.Capacity = 49800;
        Assert.Null(source.Read().Watts);
    }

    [Fact]
    public void ThirtySecondsWithoutMeaningfulCapacityChangeExpiresEstimate()
    {
        var e = Ready(); for (var t = 20; t < 45; t += 5) Assert.NotNull(Add(e, t, 49850).Watts);
        Assert.Null(Add(e, 45, 49850).Watts); Assert.Equal("StaleCapacity", e.Last.Reason);
    }

    [Fact]
    public void NativeAlwaysOverridesShadowEstimateAndRecoveryIsImmediate()
    {
        var clock = new Clock(); var native = new Native { Rate = -2000 }; using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 15; t += 5) { clock.Seconds = t; native.Capacity = (uint)(50000 - t * 10); Assert.Equal(BatteryPowerSource.Native, source.Read().PowerSource); }
        Assert.Equal(-36, source.LastEstimate.Watts);
        native.Rate = int.MinValue; clock.Seconds = 20; native.Capacity = 49800;
        var fallback = source.Read(); Assert.Equal(-36, fallback.Watts); Assert.Equal(BatteryPowerSource.Estimated, fallback.PowerSource);
        native.Rate = -3000; clock.Seconds = 25; native.Capacity = 49750;
        var restored = source.Read(); Assert.Equal(-3, restored.Watts); Assert.Equal(BatteryPowerSource.Native, restored.PowerSource);
    }

    [Theory]
    [InlineData(0u, 50000u)] [InlineData(0u, uint.MaxValue)] [InlineData(0u, 60001u)]
    [InlineData(0x40000000u, 50000u)]
    public void MissingInvalidRelativeCapacityNeverProducesEstimatedWatts(uint extra, uint capacity)
    {
        var clock = new Clock(); var native = new Native { Extra = extra, Capacity = capacity }; using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 20; t += 5) { clock.Seconds = t; Assert.Null(source.Read().Watts); }
        Assert.NotEqual(BatteryPowerSource.Estimated, source.Read().PowerSource);
    }

    [Fact]
    public void MultipleBatteriesDisableOnlyEstimateAndNoBatteryStillHides()
    {
        var clock = new Clock(); var native = new Native { Count = 2 }; using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 20; t += 5) { clock.Seconds = t; native.Capacity -= 50; Assert.Null(source.Read().Watts); }
        Assert.Equal("UnsupportedBatterySet", source.LastEstimate.Reason);
        native.Power = new(1, 128, 255, uint.MaxValue); Assert.False(source.Read().IsPresent);
    }

    [Fact]
    public void EstimatedChargeDerivesFullButNeverReplacesIndependentWindowsLeft()
    {
        var clock = new Clock(); var native = new Native { Power = new(1, 8, 67, uint.MaxValue), Flags = 4 }; using var source = new SystemBatterySource(native, clock);
        SystemBatteryReading? charge = null;
        for (var t = 0; t <= 15; t += 5) { clock.Seconds = t; native.Capacity = (uint)(50000 + t * 10); charge = source.Read(); }
        Assert.Equal(BatteryPowerSource.Estimated, charge!.PowerSource);
        Assert.Equal((60000d - 50150) / 1000 / 36 * 3600, charge.TimeToFullSeconds!.Value, 8);
        Assert.Equal(BatteryFullSource.EstimatedDerived, charge.FullSource);
        clock.Seconds = 20; native.Power = new(0, 1, 67, 3000); native.Flags = 2; native.Capacity = 50000;
        var transition = source.Read(); Assert.Null(transition.Watts); Assert.Null(transition.TimeToFullSeconds); Assert.Equal(3000, transition.RemainingSeconds);
        for (var t = 25; t <= 35; t += 5) { clock.Seconds = t; native.Capacity = (uint)(50000 - (t - 20) * 10); source.Read(); }
        Assert.Equal(-36, source.LastEstimate.Watts);
        clock.Seconds = 40; native.Capacity = 49800; var discharge = source.Read(); Assert.Equal(3000, discharge.RemainingSeconds);
        native.Power = new(0, 1, 67, uint.MaxValue); clock.Seconds = 45; native.Capacity = 49750;
        var derived = source.Read();
        Assert.Equal(49750d / 1000 / 36 * 3600, derived.RemainingSeconds);
        Assert.Equal(-36, derived.Watts);
        Assert.Equal(BatteryLeftSource.EstimatedDerived, derived.LeftSource);
    }

    [Fact]
    public void SourceResumeRequestAndTagChangeDiscardOldPower()
    {
        var clock = new Clock(); var native = new Native(); using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 15; t += 5) { clock.Seconds = t; native.Capacity = (uint)(50000 - t * 10); source.Read(); }
        source.ResetEstimate(); clock.Seconds = 16; native.Capacity = 49840; Assert.Null(source.Read().Watts);
        native.Tag++; clock.Seconds = 20; native.Capacity = 49800; Assert.Null(source.Read().Watts);
    }

    [Fact]
    public void FailedStatusAfterTagRefreshCannotUseEarlierCapacityForEstimate()
    {
        var clock = new Clock(); var native = new Native { FailRetries = true }; using var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 20; t += 5)
        { clock.Seconds = t; native.Capacity = (uint)(50000 - t * 10); Assert.Null(source.Read().Watts); }
        Assert.Equal("InvalidCapacityOrUnits", source.LastEstimate.Reason);
    }

    private static BatteryPowerEstimator Ready()
    { var e = new BatteryPowerEstimator(); for (var t = 0; t <= 15; t += 5) Add(e, t, (uint)(50000 - t * 10)); return e; }
    private sealed class Clock : TimeProvider { public int Seconds; public override DateTimeOffset GetUtcNow() => Start.AddSeconds(Seconds); }
    private sealed class Native : ISystemBatteryNative
    {
        public BatteryPowerStatus Power = new(0, 1, 67, uint.MaxValue);
        public uint Capacity = 50000, Tag = 1, Flags = 2, Extra;
        public int Rate = int.MinValue, Count = 1;
        public bool FailRetries;
        public BatteryPowerStatus? ReadPowerStatus() => Power;
        public IReadOnlyList<ISystemBatteryDevice> EnumerateDevices() => Enumerable.Range(0, Count).Select(i => (ISystemBatteryDevice)new Device(this, i)).ToArray();
    }
    private sealed class Device(Native owner, int index) : ISystemBatteryDevice
    {
        private int _reads;
        public string Identity => $"battery-{index}";
        public uint Tag => owner.Tag;
        public bool RefreshInformation(out uint capabilities, out uint full) { capabilities = SystemBatteryInterpreter.SystemBattery | owner.Extra; full = 60000; return true; }
        public bool ReadStatus(out BatteryDeviceStatus status)
        { status = new(owner.Flags, owner.Capacity, owner.Rate); return !owner.FailRetries || ++_reads % 2 != 0; }
        public void Dispose() { }
    }
}
