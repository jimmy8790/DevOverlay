using DevOverlay.Peripherals;
using DevOverlay.Peripherals.VendorBackends;
using Xunit;

namespace DevOverlay.Tests;

public sealed class RazerBatteryTests
{
    private static HidInterfaceMetadata Metadata => new(0x1532, 0x0550, 0xFF00, 1, 64, 64, 0, 1, 0, true, 0, 2, 2);
    private static PeripheralHidInterface Device => new("razer-vendor", "22222222-2222-4222-8222-222222222222", "Razer Barracuda X 2.4", Metadata);
    internal static byte[] Reply(RazerPaReadCommand command, byte value)
    {
        var report = new byte[64]; report[0] = 2; report[1] = 13; report[2] = 0x50; report[3] = 0x49; report[4] = 8;
        report[6] = 4; report[8] = (byte)command; report[9] = 1; report[10] = 0; report[11] = value;
        return report;
    }
    [Fact]
    public void OnlyExactVerifiedPidAndCollectionMatch()
    {
        Assert.True(RazerBarracudaPaProtocol.Matches(Metadata));
        foreach (var wrong in new[] { Metadata with { VendorId = 0x3710 }, Metadata with { ProductId = 0x0552 },
            Metadata with { ProductId = 0x053C }, Metadata with { ProductId = 0x0584 }, Metadata with { InputReportId = 1 },
            Metadata with { OutputReportId = null }, Metadata with { UsagePage = 0x000C }, Metadata with { UsageId = 2 },
            Metadata with { InputLength = 91 }, Metadata with { OutputLength = 0 }, Metadata with { FeatureLength = 91 },
            Metadata with { Accessible = false } }) Assert.False(RazerBarracudaPaProtocol.Matches(wrong));
    }
    [Fact]
    public void RequestsAreOnlyThreeReadRegistersWithoutSessionOrSettingsMutation()
    {
        foreach (var command in new[] { RazerPaReadCommand.Connection, RazerPaReadCommand.Battery, RazerPaReadCommand.Charging })
        {
            var report = RazerBarracudaPaProtocol.Request(command);
            Assert.Equal(64, report.Length); Assert.Equal(2, report[0]); Assert.Equal(3, report[9]);
            Assert.Equal((byte)command, report[10]); Assert.All(report.Skip(11), value => Assert.Equal(0, value));
        }
        foreach (var command in new byte[] { 0, 1, 0xE1, 0x9E, 0xAC, 0x98 })
            Assert.Throws<ArgumentOutOfRangeException>(() => RazerBarracudaPaProtocol.Request((RazerPaReadCommand)command));
    }
    [Theory]
    [InlineData(0)] [InlineData(88)] [InlineData(100)]
    public void BatteryRange(int value) => Assert.Equal(value, RazerBarracudaPaProtocol.Value(Reply(RazerPaReadCommand.Battery, (byte)value), RazerPaReadCommand.Battery));
    [Fact]
    public void RejectInvalidTruncatedWrongHeaderWrongRegisterAndCachedVersion()
    {
        var good = Reply(RazerPaReadCommand.Battery, 88);
        Assert.Null(RazerBarracudaPaProtocol.Value(good[..12], RazerPaReadCommand.Battery));
        Assert.Null(RazerBarracudaPaProtocol.Value([.. good, 0], RazerPaReadCommand.Battery));
        foreach (var index in new[] { 0, 2, 3, 4, 6, 8, 9, 11 })
        {
            var malformed = good.ToArray(); malformed[index] = 255;
            Assert.Null(RazerBarracudaPaProtocol.Value(malformed, RazerPaReadCommand.Battery));
        }
        var cached = good.ToArray(); cached[9] = 2;
        Assert.Null(RazerBarracudaPaProtocol.Value(cached, RazerPaReadCommand.Battery));
        Assert.Null(RazerBarracudaPaProtocol.Value(Reply(RazerPaReadCommand.Connection, 2), RazerPaReadCommand.Connection));
        Assert.Null(RazerBarracudaPaProtocol.Value(Reply(RazerPaReadCommand.Charging, 2), RazerPaReadCommand.Charging));
    }
    [Fact]
    public void Measured0550FramingWithLeadingTwoIsAccepted()
    {
        // Real 1532:0550 reply (2026-10-05); bytes 5-8 vary per reply and carry no register data.
        var connection = Convert.FromHexString("020E504908DC2A424202040020010101").Concat(new byte[48]).ToArray();
        Assert.Equal(1, RazerBarracudaPaProtocol.Value(connection, RazerPaReadCommand.Connection));
        Assert.Null(RazerBarracudaPaProtocol.Value(connection, RazerPaReadCommand.Battery));
        var unknownLead = connection.ToArray(); unknownLead[9] = 0x05;
        Assert.Null(RazerBarracudaPaProtocol.Value(unknownLead, RazerPaReadCommand.Connection));
        var cached = connection.ToArray(); cached[13] = 2;
        Assert.Null(RazerBarracudaPaProtocol.Value(cached, RazerPaReadCommand.Connection));
    }
    [Fact]
    public async Task UnansweredBatteryRegisterKeepsTheHeadsetConnectedWithoutAPercentage()
    {
        var transport = new FakeTransport { SilentBattery = true };
        var row = Assert.Single(await new RazerBarracudaBatteryBackend(() => [Device], transport).CollectAsync(default));
        Assert.True(row.Connected); Assert.Null(row.Percentage); Assert.Null(row.Charging);
        Assert.Contains("did not answer the battery register", row.Status);
        Assert.Equal(new[] { RazerPaReadCommand.Connection, RazerPaReadCommand.Battery }, transport.Commands);
    }
    [Fact]
    public void ConflictingLiveFramesAreUnavailableButCachedTailDoesNotOverrideLive()
    {
        var report = Reply(RazerPaReadCommand.Battery, 88);
        Reply(RazerPaReadCommand.Battery, 50).AsSpan(5, 7).CopyTo(report.AsSpan(12));
        Assert.Null(RazerBarracudaPaProtocol.Value(report, RazerPaReadCommand.Battery));
        report[16] = 2;
        Assert.Equal(88, RazerBarracudaPaProtocol.Value(report, RazerPaReadCommand.Battery));
    }
    [Fact]
    public async Task UnsupportedNoQueriesSupportedHeadsetDuplicatesMergeAndReconnect()
    {
        var transport = new FakeTransport();
        var backend = new RazerBarracudaBatteryBackend(() => [Device with { Metadata = Metadata with { ProductId = 0x0552 } }], transport);
        Assert.Empty(await backend.CollectAsync(default)); Assert.Empty(transport.Commands);
        backend = new(() => [Device, Device with { Path = "duplicate-collection" }], transport);
        var row = Assert.Single(await backend.CollectAsync(default));
        Assert.Equal(88, row.Percentage); Assert.Equal(PeripheralType.Headset, row.Type); Assert.False(row.Charging);
        Assert.Equal(3, transport.Commands.Count);
        transport.Connected = false; transport.Commands.Clear();
        row = Assert.Single(await backend.CollectAsync(default));
        Assert.False(row.Connected); Assert.Null(row.Percentage); Assert.Single(transport.Commands);
        transport.Connected = true;
        Assert.Equal(88, Assert.Single(await backend.CollectAsync(default)).Percentage);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task TimeoutAndFailureAreIsolatedAndRecover(bool timeout)
    {
        var transport = new FakeTransport { Failure = timeout ? new OperationCanceledException() : new System.IO.IOException() };
        var backend = new RazerBarracudaBatteryBackend(() => [Device], transport);
        Assert.Null(Assert.Single(await backend.CollectAsync(default)).Percentage);
        transport.Failure = null;
        Assert.Equal(88, Assert.Single(await backend.CollectAsync(default)).Percentage);
    }
    private sealed class FakeTransport : IRazerBatteryTransport
    {
        internal List<RazerPaReadCommand> Commands { get; } = [];
        internal bool Connected { get; set; } = true;
        internal Exception? Failure { get; set; }
        internal bool SilentBattery { get; set; }
        public async Task<byte[]> QueryAsync(PeripheralHidInterface device, RazerPaReadCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Add(command);
            if (SilentBattery && command == RazerPaReadCommand.Battery) await Task.Delay(Timeout.Infinite, token);
            if (Failure is not null) throw Failure;
            return Reply(command, (byte)(command == RazerPaReadCommand.Battery ? 88 : command == RazerPaReadCommand.Connection && Connected ? 1 : 0));
        }
    }
}
