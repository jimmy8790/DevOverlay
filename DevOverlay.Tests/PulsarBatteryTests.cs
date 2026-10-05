using DevOverlay.Configuration;
using DevOverlay.Peripherals;
using DevOverlay.Peripherals.VendorBackends;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class PulsarBatteryTests
{
    private static HidInterfaceMetadata Metadata => new(0x3710, 0x5502, 0xFF02, 2, 17, 17, 0, 0, 1, true, 0, 8, 8);
    private static PeripheralHidInterface Device => new("receiver-interface", "11111111-1111-4111-8111-111111111111", "Pulsar LED 8K Dongle", Metadata);
    private static byte[] Reply(PulsarReadCommand command, int percentage = 82, bool connected = true)
    {
        var report = PulsarNordicProtocol.Request(command);
        report[6] = (byte)(command == PulsarReadCommand.LinkStatus ? connected ? 1 : 0 : percentage);
        report[^1] = unchecked((byte)(0x55 - report.Take(16).Sum(value => value)));
        return report;
    }
    [Theory]
    [InlineData(0x5502, true)] [InlineData(0x5406, true)] [InlineData(0x5501, false)] [InlineData(0x3414, false)]
    public void OnlyVerifiedReceiverPids(int pid, bool expected) => Assert.Equal(expected,
        PulsarNordicProtocol.Matches(Metadata with { ProductId = (ushort)pid }));

    [Fact]
    public void WrongVendorCollectionLengthsReportIdsAndAccessAreRejected()
    {
        foreach (var metadata in new[] { Metadata with { VendorId = 0x1532 }, Metadata with { UsagePage = 1 },
            Metadata with { UsageId = 1 }, Metadata with { InputLength = 64 }, Metadata with { OutputLength = 0 },
            Metadata with { FeatureLength = 8 }, Metadata with { InputReportId = null },
            Metadata with { OutputReportId = 7 }, Metadata with { Accessible = false } })
            Assert.False(PulsarNordicProtocol.Matches(metadata));
    }
    [Fact]
    public void RequestsCannotContainMutationCommandsOrArguments()
    {
        foreach (var command in new[] { PulsarReadCommand.LinkStatus, PulsarReadCommand.Battery })
        {
            var report = PulsarNordicProtocol.Request(command);
            Assert.Equal(17, report.Length); Assert.Equal(8, report[0]); Assert.Equal((byte)command, report[1]);
            Assert.All(report.Skip(2).Take(14), value => Assert.Equal(0, value));
            Assert.Equal(0x55, report.Sum(value => value) & 255);
        }
        foreach (var command in new byte[] { 1, 2, 5, 7, 9, 13, 15, 255 })
            Assert.Throws<ArgumentOutOfRangeException>(() => PulsarNordicProtocol.Request((PulsarReadCommand)command));
    }
    [Theory]
    [InlineData(0)] [InlineData(82)] [InlineData(100)]
    public void BatteryRangeAndCharging(int level)
    {
        var report = Reply(PulsarReadCommand.Battery, level);
        Assert.Equal((level, false), PulsarNordicProtocol.Battery(report));
        report[7] = 1; report[^1]--;
        Assert.Equal((level, true), PulsarNordicProtocol.Battery(report));
    }
    [Theory]
    [InlineData(101)] [InlineData(255)]
    public void InvalidPercentageRejected(int level) => Assert.Null(PulsarNordicProtocol.Battery(Reply(PulsarReadCommand.Battery, level)));

    private static byte[] WithVoltage(byte[] report, int millivolts, bool charging = false)
    {
        report[7] = (byte)(charging ? 1 : 0); report[8] = (byte)(millivolts >> 8); report[9] = (byte)millivolts;
        report[^1] = unchecked((byte)(0x55 - report.Take(16).Sum(value => value)));
        return report;
    }

    [Fact]
    public void MeasuredLed8kReplyUsesTheVoltageFieldLikeTheOfficialDriver()
    {
        // Real 3710:5502 reply on 2026-10-05: field count 2, level byte 0x5F (95), not charging, 0x1009 = 4105 mV.
        // The Pulsar app showed 99 at the same time; 4105 mV falls between 4080 (95%) and 4110 (100%).
        byte[] measured = [8, 4, 0, 0, 0, 2, 0x5F, 0, 0x10, 0x09, 0, 0, 0, 0, 0, 0, 0];
        measured[^1] = unchecked((byte)(0x55 - measured.Take(16).Sum(value => value)));
        Assert.Equal(4105, PulsarNordicProtocol.Millivolts(measured));
        Assert.Equal((99, false), PulsarNordicProtocol.Battery(measured));
    }

    [Theory]
    [InlineData(2999, false, 0)] [InlineData(3050, false, 0)] [InlineData(3420, false, 5)]
    [InlineData(3540, false, 15)] [InlineData(3570, false, 18)] [InlineData(3860, false, 48)]
    [InlineData(4080, false, 95)] [InlineData(4105, false, 99)] [InlineData(4110, false, 100)]
    [InlineData(4300, false, 100)] [InlineData(4110, true, 99)] [InlineData(3900, true, 53)]
    public void VoltageCurveInterpolatesBetweenFivePercentPoints(int millivolts, bool charging, int expected) =>
        Assert.Equal((expected, charging),
            PulsarNordicProtocol.Battery(WithVoltage(Reply(PulsarReadCommand.Battery, 40), millivolts, charging)));

    [Fact]
    public void ZeroVoltageFallsBackToTheLevelByteAndImplausibleVoltageIsRejected()
    {
        Assert.Equal((40, false), PulsarNordicProtocol.Battery(WithVoltage(Reply(PulsarReadCommand.Battery, 40), 0)));
        Assert.Null(PulsarNordicProtocol.Battery(WithVoltage(Reply(PulsarReadCommand.Battery, 40), 1200)));
        Assert.Null(PulsarNordicProtocol.Battery(WithVoltage(Reply(PulsarReadCommand.Battery, 40), 0xFFFF)));
    }

    [Fact]
    public async Task BackendReportsTheVoltageDerivedPercentageAndItsBasis()
    {
        var transport = new FakeTransport { Millivolts = 4105, Level = 95 };
        var reading = Assert.Single(await new PulsarNordicBatteryBackend(() => [Device], transport).CollectAsync(default));
        Assert.Equal(99, reading.Percentage); Assert.Contains("voltage curve", reading.Status);
        transport.Millivolts = 0;
        reading = Assert.Single(await new PulsarNordicBatteryBackend(() => [Device], transport).CollectAsync(default));
        Assert.Equal(95, reading.Percentage); Assert.Contains("level byte", reading.Status);
    }
    [Fact]
    public void Led8kResponsesDeclareOneLinkOrTwoBatteryFields()
    {
        var link = Reply(PulsarReadCommand.LinkStatus); link[5] = 1; link[^1]--;
        Assert.True(PulsarNordicProtocol.Connected(link));
        var battery = Reply(PulsarReadCommand.Battery, 99); battery[5] = 2; battery[^1] -= 2;
        Assert.Equal((99, false), PulsarNordicProtocol.Battery(battery));
    }
    [Fact]
    public void MalformedTruncatedEventsFailureAndChecksumRejected()
    {
        var good = Reply(PulsarReadCommand.Battery);
        Assert.Null(PulsarNordicProtocol.Battery(good[..16]));
        Assert.Null(PulsarNordicProtocol.Battery([.. good, 0]));
        foreach (var index in new[] { 0, 1, 2, 3, 4, 5, 7, 16 })
        {
            var bad = good.ToArray(); bad[index] = 10;
            if (index != 16) bad[^1] = unchecked((byte)(0x55 - bad.Take(16).Sum(value => value)));
            Assert.Null(PulsarNordicProtocol.Battery(bad));
        }
        var busy = Reply(PulsarReadCommand.LinkStatus); busy[10] = 1; busy[^1]--;
        Assert.Null(PulsarNordicProtocol.Connected(busy));
        Assert.False(PulsarNordicProtocol.IsReply(Reply(PulsarReadCommand.LinkStatus), PulsarReadCommand.Battery));
    }
    [Fact]
    public async Task UnsupportedAndDuplicateCollectionsNeverTriggerExtraQueries()
    {
        var transport = new FakeTransport();
        var backend = new PulsarNordicBatteryBackend(() => [Device with { Metadata = Metadata with { ProductId = 0x5501 } }], transport);
        Assert.Empty(await backend.CollectAsync(default)); Assert.Empty(transport.Commands);
        backend = new(() => [Device, Device with { Path = "same-container-second-collection" }], transport);
        var reading = Assert.Single(await backend.CollectAsync(default));
        Assert.Equal(82, reading.Percentage); Assert.Equal(PeripheralType.Mouse, reading.Type);
        Assert.Equal(new[] { PulsarReadCommand.LinkStatus, PulsarReadCommand.Battery }, transport.Commands);
    }
    [Fact]
    public async Task DisconnectDoesNotReadCachedBatteryAndReconnectRecovers()
    {
        var transport = new FakeTransport { Connected = false };
        var backend = new PulsarNordicBatteryBackend(() => [Device], transport);
        var reading = Assert.Single(await backend.CollectAsync(default));
        Assert.False(reading.Connected); Assert.Null(reading.Percentage); Assert.Single(transport.Commands);
        transport.Connected = true;
        reading = Assert.Single(await backend.CollectAsync(default));
        Assert.True(reading.Connected); Assert.Equal(82, reading.Percentage);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task TimeoutAndAccessFailureReturnUnavailableWithoutCrashing(bool timeout)
    {
        var transport = new FakeTransport { Failure = timeout ? new OperationCanceledException() : new System.ComponentModel.Win32Exception(5) };
        var backend = new PulsarNordicBatteryBackend(() => [Device], transport);
        var reading = Assert.Single(await backend.CollectAsync(default));
        Assert.Null(reading.Percentage); Assert.Contains(timeout ? "timeout" : "Win32 5", reading.Status);
        transport.Failure = null;
        Assert.Equal(82, Assert.Single(await backend.CollectAsync(default)).Percentage);
    }
    [Fact]
    public async Task GenericPrecedenceVendorFallbackClassificationAndPreferencesStayAttached()
    {
        var vendor = Assert.Single(await new PulsarNordicBatteryBackend(() => [Device], new FakeTransport()).CollectAsync(default));
        var windows = vendor with { InterfaceId = "generic-node", Type = PeripheralType.Other, Source = PeripheralBatterySource.WindowsProperty,
            Percentage = 90, AuthoritativeType = false, FriendlyName = "Receiver", Status = "Ready" };
        var merged = Assert.Single(PeripheralBatteryMerge.Merge([windows, vendor]));
        Assert.Equal(90, merged.Percentage); Assert.Equal(PeripheralBatterySource.WindowsProperty, merged.Source);
        Assert.Equal(PeripheralType.Mouse, merged.Type);
        merged = Assert.Single(PeripheralBatteryMerge.Merge([windows with { Percentage = null }, vendor]));
        Assert.Equal(82, merged.Percentage); Assert.Equal(PeripheralBatterySource.Vendor, merged.Source);
        var preference = new PeripheralPreference(merged.Identity, "Receiver", PeripheralType.Other, "DEV", true, "MY MOUSE");
        var saved = PeripheralBatteryMerge.AddPreferences([preference], [merged]);
        var migrated = Assert.Single(saved);
        Assert.Equal("MSE", migrated.DefaultLabel); Assert.Equal("MY MOUSE", migrated.HudLabel); Assert.True(migrated.Show);
        Assert.Equal(preference.Identity, migrated.Identity);
        Assert.Equal(saved, PeripheralBatteryMerge.AddPreferences(saved, [merged with { Connected = false, Percentage = null }]));
        var model = new SettingsViewModel(OverlaySettings.CreateDefault());
        model.UpdatePeripheralDevices([], [preference]);
        model.UpdatePeripheralDevices([merged], saved);
        var row = Assert.Single(model.PeripheralDevices);
        Assert.Equal("MSE", row.DefaultLabel); Assert.Equal("MY MOUSE", row.CustomName);
        Assert.Contains("Mouse", row.StatusText);
        var disconnected = Assert.Single(PeripheralBatteryMerge.Merge([windows with { Percentage = null }, vendor with { Connected = false, Percentage = null }]));
        Assert.False(disconnected.Connected); Assert.Null(disconnected.ValidPercentage);
    }
    private sealed class FakeTransport : IHidBatteryTransport
    {
        internal List<PulsarReadCommand> Commands { get; } = [];
        internal bool Connected { get; set; } = true;
        internal Exception? Failure { get; set; }
        internal int Level { get; set; } = 82;
        internal int Millivolts { get; set; }
        public Task<byte[]> QueryAsync(PeripheralHidInterface device, PulsarReadCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Add(command);
            if (Failure is not null) return Task.FromException<byte[]>(Failure);
            var reply = Reply(command, Level, Connected);
            return Task.FromResult(command == PulsarReadCommand.Battery ? WithVoltage(reply, Millivolts) : reply);
        }
    }
}
