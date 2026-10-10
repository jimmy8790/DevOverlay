using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class SystemBatteryRecoveryTests
{
    private static BatteryDeviceReading Device(int rate, uint capacity = 40000, uint full = 60000,
        uint flags = 4, uint extra = 0) => new(SystemBatteryInterpreter.SystemBattery | extra, full, new(flags, capacity, rate));
    private static SystemBatteryReading Charge(params BatteryDeviceReading[] devices) =>
        SystemBatteryInterpreter.Interpret(new(new(1, 8, 67, uint.MaxValue), devices, true));

    [Fact]
    public void ChargingEstimateUsesActualMissingCapacityAndRate()
    {
        var value = Charge(Device(30000));
        Assert.Equal(2400, value.TimeToFullSeconds); Assert.Equal(30, value.Watts);
        Assert.Null(value.RemainingSeconds);
        var metric = SystemBatteryMetricProvider.CreateSnapshots(value).Last();
        Assert.Equal(MetricId.BatteryRemaining, metric.Id);
        Assert.Equal("FULL", MetricTextFormatter.GetPrefix(metric));
        Assert.Equal("40m", MetricTextFormatter.FormatValue(metric));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(int.MinValue)] [InlineData(1000000)]
    public void InvalidChargingRateCannotProduceEta(int rate) => Assert.Null(Charge(Device(rate)).TimeToFullSeconds);

    [Theory]
    [InlineData(60000u, 60000u)] [InlineData(60001u, 60000u)] [InlineData(uint.MaxValue, 60000u)]
    [InlineData(40000u, 0u)] [InlineData(40000u, uint.MaxValue)]
    public void InvalidOrFullCapacityCannotProduceEta(uint capacity, uint full) =>
        Assert.Null(Charge(Device(30000, capacity, full)).TimeToFullSeconds);

    [Fact]
    public void RelativeUnitsSupportSameDeviceEtaButNotWatts()
    {
        var value = Charge(Device(20, 40, 60, extra: SystemBatteryInterpreter.RelativeCapacity));
        Assert.Equal(3600, value.TimeToFullSeconds); Assert.Null(value.Watts);
    }

    [Fact]
    public void MultipleBatteryEstimateUsesLongestIndividualEtaAndRequiresAllValid()
    {
        Assert.Equal(7200, Charge(Device(10000), Device(20000)).TimeToFullSeconds);
        Assert.Null(Charge(Device(10000), Device(0)).TimeToFullSeconds);
    }

    [Fact]
    public void EtaRequiresChargingFlagAndRejectsExcessiveDurationButAcceptsLowValidRate()
    {
        Assert.Null(Charge(Device(30000, flags: 1)).TimeToFullSeconds);
        Assert.Null(Charge(Device(1)).TimeToFullSeconds);
        Assert.Equal(3600, Charge(Device(1, 59999)).TimeToFullSeconds);
    }

    [Fact]
    public void UnknownSuccessfulStatusRetriesTagAndReopensWithBoundedFastCadence()
    {
        var time = new Clock(); var native = new Native { Rate = int.MinValue };
        using var source = new SystemBatterySource(native, time);
        Assert.Null(source.Read().Watts); Assert.Equal(2, native.Last!.InformationReads);
        Assert.Equal(TimeSpan.FromSeconds(1), source.RefreshInterval);
        time.Advance(4); source.Read(); Assert.Equal(1, native.Enumerations);
        var old = native.Last; time.Advance(1); source.Read();
        Assert.True(old!.Disposed); Assert.Equal(2, native.Enumerations);
        time.Advance(25); source.Read(); Assert.Equal(TimeSpan.FromSeconds(2), source.RefreshInterval);
        time.Advance(1); source.Read(); Assert.Equal(TimeSpan.FromSeconds(2), source.RefreshInterval);
        native.Rate = -12000; Assert.Equal(-12, source.Read().Watts);
        Assert.Equal(TimeSpan.FromSeconds(2), source.RefreshInterval);
    }

    [Fact]
    public void AcTransitionClearsOldWattsAndEtaImmediatelyAndRecoversWithoutProviderRestart()
    {
        var time = new Clock(); var native = new Native();
        using var source = new SystemBatterySource(native, time);
        Assert.Equal(-12, source.Read().Watts);
        native.Power = new(1, 8, 67, uint.MaxValue); native.Flags = 4; native.Rate = int.MinValue;
        var missing = source.Read();
        Assert.Equal(2, native.Enumerations); Assert.Equal(67, missing.Percentage);
        Assert.Null(missing.Watts); Assert.Null(missing.TimeToFullSeconds); Assert.Null(missing.RemainingSeconds);
        native.Rate = 30000;
        var restored = source.Read(); Assert.Equal(30, restored.Watts); Assert.Equal(2400, restored.TimeToFullSeconds);
        native.Power = new(0, 1, 67, 3000); native.Flags = 2; native.Rate = int.MinValue;
        var unplugged = source.Read(); Assert.Null(unplugged.Watts); Assert.Null(unplugged.TimeToFullSeconds);
        Assert.Equal(3000, unplugged.RemainingSeconds);
    }

    [Fact]
    public async Task ProviderUsesSourceRecoveryCadenceAndDoesNotRetainPreviousValues()
    {
        var native = new Native(); using var source = new SystemBatterySource(native, new Clock());
        await using var provider = new SystemBatteryMetricProvider(() => source);
        Assert.True((await provider.CollectAsync(default)).Single(x => x.Id == MetricId.BatteryPower).IsAvailable);
        native.Rate = int.MinValue;
        Assert.False((await provider.CollectAsync(default)).Single(x => x.Id == MetricId.BatteryPower).IsAvailable);
        Assert.Equal(TimeSpan.FromSeconds(1), provider.RefreshInterval);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private sealed class Native : ISystemBatteryNative
    {
        public BatteryPowerStatus Power = new(0, 1, 67, uint.MaxValue);
        public int Rate = -12000, Enumerations;
        public uint Flags = 2;
        public DeviceHandle? Last;
        public BatteryPowerStatus? ReadPowerStatus() => Power;
        public IReadOnlyList<ISystemBatteryDevice> EnumerateDevices() { Enumerations++; Last = new(this); return [Last]; }
    }
    private sealed class DeviceHandle(Native owner) : ISystemBatteryDevice
    {
        public int InformationReads;
        public bool Disposed;
        public bool RefreshInformation(out uint capabilities, out uint full)
        { InformationReads++; capabilities = SystemBatteryInterpreter.SystemBattery; full = 60000; return true; }
        public bool ReadStatus(out BatteryDeviceStatus status) { status = new(owner.Flags, 40000, owner.Rate); return true; }
        public void Dispose() => Disposed = true;
    }
}
