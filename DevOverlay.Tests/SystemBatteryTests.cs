using System.Runtime.InteropServices;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class SystemBatteryTests
{
    private const uint System = SystemBatteryInterpreter.SystemBattery;
    private static BatteryDeviceReading Device(int rate = -14200, uint capacity = 50000, uint flags = 2,
        uint capabilities = System, uint full = 64000) => new(capabilities, full, new(flags, capacity, rate));
    private static SystemBatteryReading Read(byte ac = 0, byte flag = 1, byte percentage = 78,
        uint seconds = uint.MaxValue, params BatteryDeviceReading[] devices) =>
        SystemBatteryInterpreter.Interpret(new(new(ac, flag, percentage, seconds), devices, true));

    [Fact]
    public void NoBatteryIsNotZeroAndHasNoApplicableSnapshots()
    {
        var result = Read(flag: 128, percentage: 255);
        Assert.False(result.IsPresent); Assert.Null(result.Percentage); Assert.Null(result.Watts);
        Assert.Null(result.RemainingSeconds);
        Assert.All(SystemBatteryMetricProvider.CreateSnapshots(result), metric =>
        { Assert.False(metric.IsApplicable); Assert.False(metric.IsAvailable); Assert.Null(metric.Value); });
    }

    [Theory]
    [InlineData(0)] [InlineData(57)] [InlineData(100)]
    public void ValidPercentageWorksWithoutDeviceRate(byte percentage)
    { var reading = Read(percentage: percentage); Assert.True(reading.IsPresent); Assert.Equal(percentage, reading.Percentage); Assert.Null(reading.Watts); }

    [Theory]
    [InlineData(101)] [InlineData(254)] [InlineData(255)]
    public void InvalidPercentageIsUnavailable(byte percentage) => Assert.Null(Read(percentage: percentage).Percentage);

    [Theory]
    [InlineData(1, 8, 4, 28823, (int)BatteryFlow.Charging, 28.823)]
    [InlineData(0, 1, 2, -14200, (int)BatteryFlow.Discharging, -14.2)]
    [InlineData(1, 1, 2, -7000, (int)BatteryFlow.Discharging, -7)]
    public void NativeMilliwattsKeepTheirSign(byte ac, byte flag, uint state, int rate, int flow, double watts)
    {
        var reading = Read(ac, flag, devices: [Device(rate, flags: state)]);
        Assert.Equal((BatteryFlow)flow, reading.Flow); Assert.Equal(watts, reading.Watts);
    }

    [Theory]
    [InlineData(int.MinValue)] [InlineData(0)] [InlineData(int.MaxValue)] [InlineData(-1000000)]
    public void UnknownZeroOrImplausibleRateDoesNotBecomeZeroWatts(int rate)
    {
        var reading = Read(seconds: 3000, devices: [Device(rate)]);
        Assert.Null(reading.Watts); Assert.Equal(78, reading.Percentage); Assert.Equal(3000, reading.RemainingSeconds);
    }

    [Fact]
    public void SubWattDischargingIsValid() => Assert.Equal(-.2, Read(devices: [Device(-200)]).Watts);

    [Fact]
    public void FullOnAcHasNeitherWattsNorUsableRuntime()
    {
        var result = Read(1, 1, 100, 9999, Device(0, flags: 1));
        Assert.Equal(BatteryFlow.Ac, result.Flow); Assert.Null(result.Watts); Assert.Null(result.RemainingSeconds);
    }

    [Theory]
    [InlineData(0)] [InlineData(3480)] [InlineData(13320)]
    public void OsRuntimeTakesPriorityOverCapacityEstimate(uint seconds) =>
        Assert.Equal(seconds, Read(seconds: seconds, devices: [Device()]).RemainingSeconds);

    [Fact]
    public void KnownUnitsPermitCapacityRateFallback() =>
        Assert.Equal(50000d / 14200 * 3600, Read(devices: [Device()]).RemainingSeconds!.Value, 6);

    [Fact]
    public void UnknownRuntimeWithoutRateStaysUnknown() => Assert.Null(Read(devices: [Device(int.MinValue)]).RemainingSeconds);

    [Theory]
    [InlineData(uint.MaxValue)] [InlineData(359941)]
    public void UnknownOrOversizedOsTimeDoesNotOverflow(uint seconds) => Assert.Null(Read(seconds: seconds).RemainingSeconds);

    [Theory]
    [InlineData(0)] [InlineData(uint.MaxValue)]
    public void InvalidFullCapacityBlocksOnlyCapacityFallback(uint full)
    {
        var result = Read(percentage: 255, devices: [Device(full: full)]);
        Assert.Null(result.Percentage); Assert.Null(result.RemainingSeconds); Assert.Equal(-14.2, result.Watts);
    }

    [Fact]
    public void UnknownCapacityBlocksEstimateButNotPower()
    {
        var result = Read(percentage: 255, devices: [Device(capacity: uint.MaxValue)]);
        Assert.Null(result.Percentage); Assert.Null(result.RemainingSeconds); Assert.Equal(-14.2, result.Watts);
    }

    [Fact]
    public void RelativeUnitsNeverBecomeWattsOrCapacityEstimate()
    {
        var result = Read(seconds: 3480, devices: [Device(capabilities: System | SystemBatteryInterpreter.RelativeCapacity)]);
        Assert.Equal(78, result.Percentage); Assert.Null(result.Watts); Assert.Equal(3480, result.RemainingSeconds);
    }

    [Theory]
    [InlineData(0u)] [InlineData(System | SystemBatteryInterpreter.ShortTerm)]
    public void NonSystemAndUpsAreExcluded(uint capabilities) => Assert.False(Read(devices: [Device(capabilities: capabilities)]).IsPresent);

    [Fact]
    public void CompatibleMultipleBatteriesAggregateAbsoluteUnits()
    {
        var result = Read(percentage: 255, devices: [Device(-10000, 20000, full: 40000), Device(-5000, 30000, full: 60000)]);
        Assert.Equal(50, result.Percentage); Assert.Equal(-15, result.Watts);
        Assert.Equal(12000, result.RemainingSeconds);
        Assert.Equal(78, Read(devices: [Device(), Device()]).Percentage); // OS 집계가 우선.
    }

    [Fact]
    public void PartialMultipleBatteryFailureNeverPublishesPartialPower()
    {
        var result = Read(seconds: 4000, devices: [Device(), new(System, 64000, null)]);
        Assert.Null(result.Watts); Assert.Equal(78, result.Percentage); Assert.Equal(4000, result.RemainingSeconds);
    }

    [Fact]
    public void MixedRelativeAndAbsoluteDevicesDoNotSumPower() =>
        Assert.Null(Read(devices: [Device(), Device(capabilities: System | SystemBatteryInterpreter.RelativeCapacity)]).Watts);

    [Theory]
    [InlineData(4u, (int)BatteryFlow.Charging)] [InlineData(2u, (int)BatteryFlow.Discharging)] [InlineData(0u, (int)BatteryFlow.Unknown)]
    public void UnknownAcUsesUnambiguousDeviceFlags(uint flags, int expected) =>
        Assert.Equal((BatteryFlow)expected, Read(255, 255, devices: [Device(flags: flags)]).Flow);

    [Fact]
    public void ChargingDoesNotExposeOsRuntime() => Assert.Null(Read(1, 8, seconds: 3000, devices: [Device(28823, flags: 4)]).RemainingSeconds);

    [Fact]
    public void HeaderConstantsAndNativeLayoutsMatchWindowsSdk()
    {
        Assert.Equal(0x294040u, WindowsBatteryNative.QueryTag);
        Assert.Equal(0x294044u, WindowsBatteryNative.QueryInformation);
        Assert.Equal(0x29404cu, WindowsBatteryNative.QueryStatus);
        Assert.Equal(12, Marshal.SizeOf<WindowsBatteryNative.SystemPowerStatus>());
        Assert.Equal(36, Marshal.SizeOf<WindowsBatteryNative.BatteryInformation>());
        Assert.Equal(12, Marshal.SizeOf<WindowsBatteryNative.BatteryQuery>());
        Assert.Equal(20, Marshal.SizeOf<WindowsBatteryNative.BatteryWaitStatus>());
        Assert.Equal(16, Marshal.SizeOf<WindowsBatteryNative.BatteryStatus>());
        Assert.Equal(12, Marshal.OffsetOf<WindowsBatteryNative.BatteryStatus>(nameof(WindowsBatteryNative.BatteryStatus.Rate)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<WindowsBatteryNative.BatteryInformation>(nameof(WindowsBatteryNative.BatteryInformation.FullChargedCapacity)).ToInt32());
    }

    [Fact]
    public void SourceCachesDevicesAndRefreshesStaleTagOnce()
    {
        var native = new FakeNative(); var device = new FakeDevice { FailedReads = 1 }; native.Devices = [device];
        using var source = new SystemBatterySource(native);
        Assert.Equal(-14.2, source.Read().Watts); Assert.Equal(2, device.InformationReads);
        Assert.Equal(-14.2, source.Read().Watts); Assert.Equal(1, native.Enumerations);
        Assert.Equal(3, device.StatusReads);
    }

    [Fact]
    public void DisappearanceClearsPowerAndRecoveryReenumeratesAfterBackoff()
    {
        var time = new FakeTime(); var native = new FakeNative(); var first = new FakeDevice(); native.Devices = [first];
        using var source = new SystemBatterySource(native, time);
        Assert.Equal(-14.2, source.Read().Watts);
        first.FailedReads = 100;
        Assert.Null(source.Read().Watts); Assert.Equal(78, source.Read().Percentage);
        time.Advance(4); Assert.Null(source.Read().Watts); Assert.Equal(1, native.Enumerations);
        native.Devices = [new FakeDevice()]; time.Advance(1);
        Assert.Equal(-14.2, source.Read().Watts); Assert.True(first.Disposed); Assert.Equal(2, native.Enumerations);
    }

    [Fact]
    public void NoBatteryEnumeratesOncePerMinuteAndRecoversWhenDeviceAppears()
    {
        var time = new FakeTime(); var native = new FakeNative { Power = new(1, 128, 255, uint.MaxValue), Devices = [] };
        using var source = new SystemBatterySource(native, time);
        Assert.False(source.Read().IsPresent);
        time.Advance(59); Assert.False(source.Read().IsPresent); Assert.Equal(1, native.Enumerations);
        native.Power = new(0, 1, 78, 3000); native.Devices = [new FakeDevice()]; time.Advance(1);
        Assert.Equal(-14.2, source.Read().Watts); Assert.Equal(2, native.Enumerations);
    }

    [Fact]
    public void InformationFailuresDisposeEveryCandidateAndRetryWithoutLosingOsData()
    {
        var time = new FakeTime(); var bad = new FakeDevice { ThrowInformation = true }; var native = new FakeNative { Devices = [bad] };
        using var source = new SystemBatterySource(native, time);
        Assert.Equal(78, source.Read().Percentage); Assert.True(bad.Disposed);
        native.Devices = [new FakeDevice()]; time.Advance(10);
        Assert.Equal(-14.2, source.Read().Watts);
    }

    [Fact]
    public void AcTransitionsReenumerateImmediatelyWithoutWaitingForMinuteTimer()
    {
        var device = new FakeDevice(); var native = new FakeNative { Devices = [device] };
        using var source = new SystemBatterySource(native);
        Assert.Equal(BatteryFlow.Discharging, source.Read().Flow);
        native.Power = new(1, 8, 79, uint.MaxValue); device.Sample = new(4, 50500, 28823);
        Assert.Equal(BatteryFlow.Charging, source.Read().Flow); Assert.Null(source.Read().RemainingSeconds);
        native.Power = new(1, 1, 100, uint.MaxValue); device.Sample = new(1, 64000, 0);
        Assert.Equal(BatteryFlow.Ac, source.Read().Flow); Assert.Equal(3, native.Enumerations);
        native.Power = new(0, 1, 100, 3600); device.Sample = new(2, 64000, -14200);
        Assert.Equal(BatteryFlow.Discharging, source.Read().Flow);
    }

    [Fact]
    public async Task ProviderLazilyCreatesSourceUsesFixedCadenceAndDisposesIt()
    {
        var fake = new FakeSource(); var creations = 0;
        var provider = new SystemBatteryMetricProvider(() => { creations++; return fake; });
        Assert.Equal(0, creations); Assert.True(provider.UsesFixedRefreshInterval); Assert.Equal(TimeSpan.FromSeconds(2), provider.RefreshInterval);
        Assert.Equal(3, (await provider.CollectAsync(default)).Count);
        await provider.CollectAsync(default); Assert.Equal(1, creations);
        await provider.DisposeAsync(); Assert.True(fake.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.CollectAsync(default));
    }

    [Fact]
    public async Task FailedProviderRecoversWithoutRestartingOtherProviders()
    {
        var source = new FakeSource { Fail = true };
        await using var provider = new SystemBatteryMetricProvider(() => source);
        Assert.All(await provider.CollectAsync(default), metric => Assert.False(metric.IsAvailable));
        source.Fail = false;
        Assert.Equal(78, (await provider.CollectAsync(default)).Single(metric => metric.Id == MetricId.BatteryCharge).Value);
    }

    [Fact]
    public async Task CancellationDoesNotOpenNativeDevices()
    {
        var creations = 0;
        await using var provider = new SystemBatteryMetricProvider(() => { creations++; return new FakeSource(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CollectAsync(new CancellationToken(true)));
        Assert.Equal(0, creations);
    }

    private sealed class FakeNative : ISystemBatteryNative
    {
        public BatteryPowerStatus Power = new(0, 1, 78, uint.MaxValue);
        public IReadOnlyList<ISystemBatteryDevice> Devices = [];
        public int Enumerations;
        public BatteryPowerStatus? ReadPowerStatus() => Power;
        public IReadOnlyList<ISystemBatteryDevice> EnumerateDevices() { Enumerations++; return Devices; }
    }
    private sealed class FakeDevice : ISystemBatteryDevice
    {
        public BatteryDeviceStatus Sample = new(2, 50000, -14200);
        public int FailedReads, InformationReads, StatusReads;
        public bool Disposed, ThrowInformation;
        public bool RefreshInformation(out uint capabilities, out uint full)
        {
            InformationReads++; capabilities = System; full = 64000;
            if (ThrowInformation) throw new InvalidOperationException("fake information failure");
            return true;
        }
        public bool ReadStatus(out BatteryDeviceStatus status)
        { StatusReads++; status = Sample; if (FailedReads > 0) { FailedReads--; return false; } return true; }
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private sealed class FakeSource : ISystemBatterySource
    {
        public bool Fail, Disposed;
        public SystemBatteryReading Read() => Fail ? throw new InvalidOperationException("fake read failure") : new(true, 78, BatteryFlow.Discharging, 14.2, 13320);
        public void Dispose() => Disposed = true;
    }
}
