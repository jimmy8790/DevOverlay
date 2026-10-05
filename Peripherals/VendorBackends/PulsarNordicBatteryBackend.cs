using System.ComponentModel;

namespace DevOverlay.Peripherals.VendorBackends;

internal enum PulsarReadCommand : byte { LinkStatus = 0x03, Battery = 0x04 }

// Protocol facts independently implemented, not vendor driver code.
// https://bbb.pulsar.gg/cfg.json routes 3710:5502 and 5406 to /cMouse.
// /cMouse/js/app.6842ab2c.js: BatteryLevel=4, DeviceOnLine=3, reportId=8;
// input payload offsets 5/6 are level/charging (native reports include reportId at byte 0).
// https://github.com/packerlschupfer/pulsar-mouse-linux/blob/main/docs/protocol-x2-crazylight.md
internal static class PulsarNordicProtocol
{
    internal const int ReportLength = 17;
    internal static bool Matches(HidInterfaceMetadata info) => info.Accessible && info.VendorId == 0x3710 &&
        info.ProductId is 0x5502 or 0x5406 && info.UsagePage == 0xFF02 && info.UsageId == 2 &&
        info.InputLength == ReportLength && info.OutputLength == ReportLength && info.FeatureLength == 0 &&
        info.InputReportId == 8 && info.OutputReportId == 8;

    internal static byte[] Request(PulsarReadCommand command)
    {
        if (command is not (PulsarReadCommand.LinkStatus or PulsarReadCommand.Battery))
            throw new ArgumentOutOfRangeException(nameof(command));
        var report = new byte[ReportLength];
        report[0] = 8; report[1] = (byte)command;
        report[^1] = unchecked((byte)(0x55 - report[0] - report[1]));
        return report;
    }
    internal static bool IsReply(ReadOnlySpan<byte> report, PulsarReadCommand command)
    {
        if (report.Length != ReportLength || report[0] != 8 || report[1] != (byte)command ||
            report[2] != 0 || report[3] != 0 || report[4] != 0 ||
            report[5] != 0 && report[5] != (command == PulsarReadCommand.LinkStatus ? 1 : 2)) return false;
        var sum = 0;
        foreach (var value in report) sum += value;
        return (sum & 0xFF) == 0x55;
    }
    internal static bool? Connected(ReadOnlySpan<byte> report) => IsReply(report, PulsarReadCommand.LinkStatus) &&
        report[6] <= 1 && report[10] == 0 ? report[6] == 1 : null;
    // Battery reply payload: [6] firmware level 0-100, [7] charging flag, [8..9] cell voltage in mV (big-endian).
    // The official cMouse driver (app.6842ab2c.js and the cMouse desktop app) shows the voltage-derived level whenever
    // the voltage field is non-zero and falls back to the level byte only when it is 0. Measured on a 5502 receiver:
    // level byte 95, 4105 mV -> 99, which is the value the Pulsar app showed. The vendor's display smoothing is not copied.
    private static readonly int[] VoltageAtEachFivePercent =
        [3050, 3420, 3480, 3540, 3600, 3660, 3720, 3760, 3800, 3840, 3880, 3920, 3940, 3960, 3980, 4000, 4020, 4040, 4060, 4080, 4110];
    private const int MinPlausibleMillivolts = 2500;
    private const int MaxPlausibleMillivolts = 4500;

    internal static int? Millivolts(ReadOnlySpan<byte> report) =>
        IsReply(report, PulsarReadCommand.Battery) ? report[8] << 8 | report[9] : null;

    internal static (int Percentage, bool Charging)? Battery(ReadOnlySpan<byte> report)
    {
        if (!IsReply(report, PulsarReadCommand.Battery) || report[6] > 100 || report[7] > 1) return null;
        var charging = report[7] == 1;
        var millivolts = report[8] << 8 | report[9];
        if (millivolts == 0) return (report[6], charging);
        if (millivolts is < MinPlausibleMillivolts or > MaxPlausibleMillivolts) return null;
        return (PercentFromMillivolts(millivolts, charging), charging);
    }

    internal static int PercentFromMillivolts(int millivolts, bool charging)
    {
        var table = VoltageAtEachFivePercent;
        // A charging cell reads high, so a full-voltage reading while charging is not reported as 100.
        if (millivolts >= table[^1]) return charging ? 99 : 100;
        if (millivolts < table[0]) return 0;
        var upper = 1;
        while (millivolts >= table[upper]) upper++;
        var percent = 5.0 * (upper - 1) + 5.0 * (millivolts - table[upper - 1]) / (table[upper] - table[upper - 1]);
        return (int)Math.Round(percent, MidpointRounding.AwayFromZero);
    }
}

internal sealed class PulsarNordicBatteryBackend : IPeripheralBatteryBackend
{
    private readonly Func<IReadOnlyList<PeripheralHidInterface>> _inventory;
    private readonly IHidBatteryTransport _transport;
    internal PulsarNordicBatteryBackend(WindowsBatteryPropertyBackend windows)
        : this(() => WindowsHidTransport.Inventory(windows.HidDevices), new WindowsHidTransport()) { }
    internal PulsarNordicBatteryBackend(Func<IReadOnlyList<PeripheralHidInterface>> inventory, IHidBatteryTransport transport)
    { _inventory = inventory; _transport = transport; }
    public string Name => "Pulsar Nordic read-only battery";
    public async Task<IReadOnlyList<PeripheralObservation>> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new List<PeripheralObservation>();
        foreach (var device in _inventory().Where(item => PulsarNordicProtocol.Matches(item.Metadata))
            .GroupBy(item => item.Identity).Select(group => group.OrderBy(item => item.Path, StringComparer.Ordinal).First()))
        {
            if (cancellationToken.IsCancellationRequested) break;
            var connected = false;
            (int Percentage, bool Charging)? battery = null;
            var status = "Pulsar mouse disconnected or sleeping";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var link = PulsarNordicProtocol.Connected(await _transport.QueryAsync(device, PulsarReadCommand.LinkStatus, timeout.Token).ConfigureAwait(false));
                connected = link == true;
                if (connected)
                {
                    var reply = await _transport.QueryAsync(device, PulsarReadCommand.Battery, timeout.Token).ConfigureAwait(false);
                    battery = PulsarNordicProtocol.Battery(reply);
                    status = !battery.HasValue ? "Invalid Pulsar battery reply"
                        : PulsarNordicProtocol.Millivolts(reply) is > 0 ? "Pulsar Nordic battery ready (voltage curve)"
                        : "Pulsar Nordic battery ready (level byte; no voltage)";
                }
                else if (!link.HasValue) status = "Invalid or busy Pulsar link reply";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { status = "Pulsar battery timeout"; }
            catch (Win32Exception exception) { status = $"Pulsar HID access failed (Win32 {exception.NativeErrorCode})"; }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { status = $"Pulsar battery unavailable ({exception.GetType().Name})"; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            result.Add(new(device.Path, device.ContainerId, "Pulsar wireless mouse (" + device.FriendlyName + ")",
                PeripheralType.Mouse, connected, battery?.Percentage, battery?.Charging,
                PeripheralBatterySource.Vendor, DateTimeOffset.UtcNow, status, AuthoritativeType: true));
        }
        return result;
    }
}
