using System.Text.Json;
using DevOverlay.Peripherals;
using DevOverlay.Peripherals.VendorBackends;
using Xunit;

namespace DevOverlay.Tests;

public sealed class PeripheralDiagnosticsTests
{
    [Fact]
    public void ExportUsesExplicitSanitizedSchemaWithoutRawIdentifiersOrReports()
    {
        const string container = "11111111-1111-4111-8111-111111111111";
        const string path = @"\\?\HID#VID_3710&PID_5502#PRIVATE-SERIAL#raw-device-path";
        var metadata = new HidInterfaceMetadata(0x3710, 0x5502, 0xFF02, 2, 17, 17, 0, 0, 1, true, 0, 8, 8);
        var device = new PeripheralHidInterface(path, container, "Product AA:BB:CC:DD:EE:FF", metadata, 101);
        var observation = new PeripheralObservation(path, container, device.FriendlyName, PeripheralType.Mouse, true,
            95, false, PeripheralBatterySource.Vendor, DateTimeOffset.UtcNow, "Ready");
        var json = PeripheralDiagnostics.Serialize(PeripheralBatteryMerge.Merge([observation]), [observation], [device],
            [new("Pulsar Nordic read-only battery", "Completed", 1)]);
        Assert.DoesNotContain(container, json); Assert.DoesNotContain("PRIVATE-SERIAL", json);
        Assert.DoesNotContain("raw-device-path", json); Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", json);
        using var document = JsonDocument.Parse(json);
        var hid = Assert.Single(document.RootElement.GetProperty("HidInterfaces").EnumerateArray());
        Assert.Equal("3710", hid.GetProperty("VID").GetString()); Assert.Equal("5502", hid.GetProperty("PID").GetString());
        Assert.Equal("PulsarNordic", hid.GetProperty("VendorMatch").GetString());
        Assert.Equal(101, hid.GetProperty("WindowsBatteryProperty").GetDouble());
        Assert.Equal(64, hid.GetProperty("HashedInterface").GetString()!.Length);
        var reading = Assert.Single(document.RootElement.GetProperty("Devices").EnumerateArray());
        Assert.Equal(95, reading.GetProperty("Percentage").GetInt32()); Assert.Equal(1, reading.GetProperty("InterfaceCount").GetInt32());
        Assert.False(hid.TryGetProperty("Path", out _)); Assert.False(hid.TryGetProperty("Payload", out _));
    }
    [Fact]
    public void UnmatchedAndInaccessibleMetadataNeverPretendToMatchAndNanIsSafe()
    {
        var metadata = new HidInterfaceMetadata(0x1532, 0x0550, 0xFF00, 1, 64, 64, 0, 1, 0, false, 5, 2, 2);
        var json = PeripheralDiagnostics.Serialize([], [], [new("raw path", null, "Product", metadata, double.NaN)], []);
        using var document = JsonDocument.Parse(json);
        var hid = Assert.Single(document.RootElement.GetProperty("HidInterfaces").EnumerateArray());
        Assert.Equal("No verified battery protocol match", hid.GetProperty("VendorMatch").GetString());
        Assert.False(hid.GetProperty("MetadataAccessible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hid.GetProperty("WindowsBatteryProperty").ValueKind);
    }
    [Fact]
    public async Task ExportDoesNotRecollectBackendsAndContainsFailureAttempt()
    {
        var backend = new FailingBackend();
        await using var service = new PeripheralBatteryService([backend], _ => { });
        await service.RefreshAsync(default);
        using var document = JsonDocument.Parse(await service.ExportDiagnosticsAsync(default));
        Assert.Equal(1, backend.Calls);
        var attempt = Assert.Single(document.RootElement.GetProperty("BackendAttempts").EnumerateArray());
        Assert.Equal("InvalidOperationException", attempt.GetProperty("Result").GetString());
        Assert.Empty(document.RootElement.GetProperty("HidInterfaces").EnumerateArray());
    }
    [Fact]
    public async Task CancelledExportDoesNotStartHardwareWork()
    {
        await using var service = new PeripheralBatteryService([], _ => { });
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportDiagnosticsAsync(cancellation.Token));
    }
    private sealed class FailingBackend : IPeripheralBatteryBackend
    {
        public string Name => "Fake";
        internal int Calls { get; private set; }
        public Task<IReadOnlyList<PeripheralObservation>> CollectAsync(CancellationToken token)
        { Calls++; throw new InvalidOperationException("PRIVATE-RAW-ID must not export exception message"); }
    }
}
