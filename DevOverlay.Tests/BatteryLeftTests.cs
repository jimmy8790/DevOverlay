using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class BatteryLeftTests
{
    [Theory]
    [InlineData(-21300)] [InlineData(int.MinValue)]
    public void WindowsLifetimeAlwaysWins(int rate)
    {
        var (clock, native, source) = Ready(rate);
        using (source)
        {
            native.Life = 3000; clock.Seconds = 20; native.Capacity = 41870;
            var value = source.Read();
            Assert.Equal(3000, value.RemainingSeconds);
            Assert.Equal(BatteryLeftSource.Windows, value.LeftSource);
        }
    }

    [Fact]
    public void NativeDischargeDerivesLeftWithProvenance()
    {
        var (_, _, source) = Ready(-21300);
        using (source)
        {
            var value = source.Read();
            Assert.Equal(42d / 21.3 * 3600, value.RemainingSeconds!.Value, 8);
            Assert.Equal(BatteryLeftSource.NativeDerived, value.LeftSource);
        }
    }

    [Fact]
    public void EstimatedDischargeUsesExactLatestAbsoluteCapacity()
    {
        var (_, _, source) = Ready();
        using (source)
        {
            // 같은 시각 재조회는 추정을 초기화하므로 마지막 유효 표본을 확인한다.
            var estimate = source.LastEstimate;
            Assert.Equal(-21.6, estimate.Watts!.Value, 8);
            Assert.Equal(42000u, estimate.EndCapacity);
        }
        var (clock, native, nextSource) = Ready();
        using (nextSource)
        {
            clock.Seconds = 20; native.Capacity = 41970;
            var value = nextSource.Read();
            Assert.Equal(41.97 / 21.6 * 3600, value.RemainingSeconds!.Value, 8);
            Assert.Equal(BatteryLeftSource.EstimatedDerived, value.LeftSource);
            Assert.Equal(BatteryPowerSource.Estimated, value.PowerSource);
            Assert.Equal(-21.6, value.Watts!.Value, 8);
        }
    }

    [Fact]
    public void StaleEstimatedPowerImmediatelyClearsDerivedLeft()
    {
        var (clock, native, source) = Ready();
        using (source)
        {
            for (var t = 20; t <= 45; t += 5)
            {
                clock.Seconds = t; native.Capacity = 42000;
                var value = source.Read();
                if (t < 45) Assert.Equal(BatteryLeftSource.EstimatedDerived, value.LeftSource);
                else { Assert.Null(value.Watts); Assert.Null(value.RemainingSeconds); Assert.Equal(BatteryLeftSource.Unavailable, value.LeftSource); }
            }
        }
    }

    [Fact]
    public void NativeThenWindowsRecoveryReplacesDerivedImmediately()
    {
        var (clock, native, source) = Ready();
        using (source)
        {
            clock.Seconds = 20; native.Capacity = 41970;
            Assert.Equal(BatteryLeftSource.EstimatedDerived, source.Read().LeftSource);
            clock.Seconds = 25; native.Capacity = 41940; native.Rate = -10000;
            var recovered = source.Read();
            Assert.Equal(BatteryLeftSource.NativeDerived, recovered.LeftSource);
            Assert.Equal(41.94 / 10 * 3600, recovered.RemainingSeconds);
            clock.Seconds = 30; native.Life = 1234;
            var windows = source.Read();
            Assert.Equal(BatteryLeftSource.Windows, windows.LeftSource); Assert.Equal(1234, windows.RemainingSeconds);
        }
    }

    [Fact]
    public void ChargeToUseClearsFullAndWaitsForNewDischargeHistory()
    {
        var clock = new Clock(); var native = new Native { Ac = 1, Flag = 8, State = 4, Rate = 10000 };
        using var source = new SystemBatterySource(native, clock);
        var charging = source.Read(); Assert.NotNull(charging.TimeToFullSeconds); Assert.Null(charging.RemainingSeconds);
        native.Ac = 0; native.Flag = 1; native.State = 2; native.Rate = int.MinValue;
        for (var t = 5; t <= 20; t += 5)
        {
            clock.Seconds = t; native.Capacity = (uint)(42090 - (t - 5) * 6);
            var value = source.Read();
            Assert.Null(value.TimeToFullSeconds);
            if (t < 20) { Assert.Null(value.Watts); Assert.Null(value.RemainingSeconds); }
            else Assert.Equal(BatteryLeftSource.EstimatedDerived, value.LeftSource);
        }
    }

    [Fact]
    public void UseToChargeImmediatelyClearsLeftAndKeepsNativeFull()
    {
        var (clock, native, source) = Ready();
        using (source)
        {
            clock.Seconds = 20; native.Ac = 1; native.Flag = 8; native.State = 4; native.Rate = 10000;
            var charging = source.Read();
            Assert.Null(charging.RemainingSeconds); Assert.Equal(BatteryLeftSource.Unavailable, charging.LeftSource);
            Assert.NotNull(charging.TimeToFullSeconds);
        }
    }

    [Theory]
    [InlineData("tag")] [InlineData("identity")] [InlineData("resume")] [InlineData("gap")]
    public void ResetCannotRetainEstimatedLeft(string reason)
    {
        var (clock, native, source) = Ready();
        using (source)
        {
            clock.Seconds = reason == "gap" ? 100 : 20;
            if (reason == "tag") native.Tag++;
            if (reason == "identity") { native.Identity = "replacement"; native.Ac = 1; } // 전환으로 재열거도 실행한다.
            if (reason == "resume") source.ResetEstimate();
            native.Capacity = 41970;
            var value = source.Read(); Assert.Null(value.RemainingSeconds); Assert.Equal(BatteryLeftSource.Unavailable, value.LeftSource);
        }
    }

    [Theory]
    [InlineData(uint.MaxValue, 60000u, 0u)] [InlineData(60001u, 60000u, 0u)]
    [InlineData(42000u, 0u, 0u)] [InlineData(42000u, 60000u, 0x40000000u)]
    public void InvalidOrRelativeCapacityCannotDeriveLeft(uint capacity, uint full, uint extra)
    {
        var native = new Native { Capacity = capacity, Full = full, Extra = extra, Rate = -21300 };
        using var source = new SystemBatterySource(native, new Clock());
        var value = source.Read(); Assert.Null(value.RemainingSeconds); Assert.Equal(BatteryLeftSource.Unavailable, value.LeftSource);
    }

    [Theory]
    [InlineData(0)] [InlineData(int.MinValue)] [InlineData(21300)] [InlineData(int.MaxValue)]
    public void InvalidNativeRateWithoutHistoryHasNoLeft(int rate)
    {
        using var source = new SystemBatterySource(new Native { Rate = rate }, new Clock());
        Assert.Null(source.Read().RemainingSeconds);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(1000)]
    public void InvalidMagnitudeIsRejected(double watts) => Assert.Null(SystemBatteryInterpreter.DeriveLeft(42000, watts));

    [Fact]
    public void ExtremeDurationFallsBackToUnavailableButValidDerivedMayReplaceInvalidWindowsTime()
    {
        Assert.Null(SystemBatteryInterpreter.DeriveLeft(42000, .001));
        var native = new Native { Rate = -21300, Life = 359941 };
        using var source = new SystemBatterySource(native, new Clock());
        Assert.Equal(BatteryLeftSource.NativeDerived, source.Read().LeftSource);
    }

    [Theory]
    [InlineData(0u)] [InlineData(4u)] [InlineData(6u)]
    public void DeviceWithoutUnambiguousDischargeStateCannotDeriveLeft(uint state)
    {
        var clock = new Clock(); var native = new Native { State = state, Rate = -21300 };
        using var source = new SystemBatterySource(native, clock);
        Assert.Null(source.Read().RemainingSeconds);
        native.Rate = int.MinValue;
        for (var t = 5; t <= 25; t += 5)
        { clock.Seconds = t; native.Capacity -= 30; Assert.Null(source.Read().RemainingSeconds); }
    }

    [Theory]
    [InlineData(10000, 20, "LEFT 30m")] [InlineData(42000, 21.3, "LEFT 1h 58m")]
    public void DerivedFormatterKeepsExistingMinuteFloor(double capacity, double watts, string expected)
    {
        var value = new SystemBatteryReading(true, 67, BatteryFlow.Discharging, watts,
            SystemBatteryInterpreter.DeriveLeft(capacity, watts), LeftSource: BatteryLeftSource.EstimatedDerived);
        Assert.Equal(expected, MetricTextFormatter.Format(SystemBatteryMetricProvider.CreateSnapshots(value).Single(metric => metric.Id == MetricId.BatteryRemaining)));
    }

    private static (Clock Clock, Native Native, SystemBatterySource Source) Ready(int rate = int.MinValue)
    {
        var clock = new Clock(); var native = new Native { Rate = rate };
        var source = new SystemBatterySource(native, clock);
        for (var t = 0; t <= 15; t += 5) { clock.Seconds = t; native.Capacity = (uint)(42090 - t * 6); source.Read(); }
        return (clock, native, source);
    }
    private sealed class Clock : TimeProvider
    { public int Seconds; public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds); }
    private sealed class Native : ISystemBatteryNative
    {
        public byte Ac, Flag = 1;
        public uint Capacity = 42000, Full = 60000, State = 2, Tag = 1, Extra, Life = uint.MaxValue;
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
