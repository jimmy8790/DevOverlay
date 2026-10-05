using System.ComponentModel;

namespace DevOverlay.Peripherals.VendorBackends;

internal enum RazerPaReadCommand : byte { Connection = 0x20, Battery = 0x21, Charging = 0x2A }

// Protocol facts only; no third-party code copied.
// https://github.com/nanomad/openbarracuda/blob/main/docs/protocol.md
// https://github.com/nanomad/openbarracuda/issues/1 explicitly reports 1532:0550 battery success.
// PA report 2 is NOT interchangeable with older report-1 Barracuda or 90-byte mouse feature protocols.
// Measured on this 0550 receiver (2026-10-05): sub-frames start with 0x02 instead of the documented 0x00, e.g.
// 02 0E 50 49 08 xx xx xx xx 02 04 00 20 01 01 01 (register 20, live 1.1, value 1). Registers 01 and 20 answer;
// 21 (battery) and 2A (charging) never answered within 5 s, with or without Remote Mode and while audio played.
internal static class RazerBarracudaPaProtocol
{
    internal static bool Matches(HidInterfaceMetadata info) => info.Accessible && info.VendorId == 0x1532 &&
        info.ProductId == 0x0550 && info.UsagePage == 0xFF00 && info.UsageId == 1 &&
        info.InputLength == 64 && info.OutputLength == 64 && info.FeatureLength == 0 &&
        info.InputReportId == 2 && info.OutputReportId == 2;
    internal static byte[] Request(RazerPaReadCommand command)
    {
        if (command is not (RazerPaReadCommand.Connection or RazerPaReadCommand.Battery or RazerPaReadCommand.Charging))
            throw new ArgumentOutOfRangeException(nameof(command));
        var result = new byte[64];
        result[0] = 2; result[1] = 0x80; result[2] = 8; result[5] = 0x50; result[6] = 0x41;
        result[7] = 8; result[9] = 3; result[10] = (byte)command;
        // class 03만 읽는다. Remote Mode(E1), 설정 class 04, audio wake도 보내지 않는다.
        return result;
    }
    internal static int? Value(ReadOnlySpan<byte> report, RazerPaReadCommand command)
    {
        if (report.Length != 64 || report[0] != 2 || report[2] != 0x50 || report[3] != 0x49 || report[4] != 8) return null;
        int? result = null;
        for (var index = 5; index + 6 < report.Length;)
        {
            if (report[index] is not (0x00 or 0x02) || report[index + 1] < 4 || report[index + 2] != 0) { index++; continue; }
            var length = report[index + 1];
            var end = index + 3 + length;
            if (end > report.Length) return null;
            // live version 1.x의 정확히 한 byte 응답만 수용한다. static/cached 2.x tail은 무시한다.
            if (report[index + 3] == (byte)command && report[index + 4] == 1 && length == 4)
            {
                var value = report[index + 6];
                var maximum = command == RazerPaReadCommand.Battery ? 100 : 1;
                if (value > maximum || result.HasValue && result.Value != value) return null;
                result = value;
            }
            index = end;
        }
        return result;
    }
}

internal sealed class RazerBarracudaBatteryBackend : IPeripheralBatteryBackend
{
    private readonly Func<IReadOnlyList<PeripheralHidInterface>> _inventory;
    private readonly IRazerBatteryTransport _transport;
    internal RazerBarracudaBatteryBackend(WindowsBatteryPropertyBackend windows)
        : this(() => WindowsHidTransport.Inventory(windows.HidDevices), new WindowsHidTransport()) { }
    internal RazerBarracudaBatteryBackend(Func<IReadOnlyList<PeripheralHidInterface>> inventory, IRazerBatteryTransport transport)
    { _inventory = inventory; _transport = transport; }
    public string Name => "Razer Barracuda X PA read-only battery";

    // A register the receiver does not answer is unavailable, not a failure of the whole device reading.
    private async Task<int?> ReadAsync(PeripheralHidInterface device, RazerPaReadCommand command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
        try { return RazerBarracudaPaProtocol.Value(await _transport.QueryAsync(device, command, timeout.Token).ConfigureAwait(false), command); }
        catch (OperationCanceledException) when (command != RazerPaReadCommand.Connection && !cancellationToken.IsCancellationRequested) { return null; }
    }
    public async Task<IReadOnlyList<PeripheralObservation>> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new List<PeripheralObservation>();
        foreach (var device in _inventory().Where(item => RazerBarracudaPaProtocol.Matches(item.Metadata))
            .GroupBy(item => item.Identity).Select(group => group.OrderBy(item => item.Path, StringComparer.Ordinal).First()))
        {
            if (cancellationToken.IsCancellationRequested) break;
            var connected = false; int? percentage = null; bool? charging = null;
            var status = "Razer headset disconnected";
            try
            {
                var link = await ReadAsync(device, RazerPaReadCommand.Connection, cancellationToken).ConfigureAwait(false);
                connected = link == 1;
                if (connected)
                {
                    percentage = await ReadAsync(device, RazerPaReadCommand.Battery, cancellationToken).ConfigureAwait(false);
                    if (percentage.HasValue)
                    {
                        var charge = await ReadAsync(device, RazerPaReadCommand.Charging, cancellationToken).ConfigureAwait(false);
                        charging = charge.HasValue ? charge == 1 : null;
                        status = "Razer Barracuda PA battery ready";
                    }
                    else status = "Razer headset connected; receiver did not answer the battery register";
                }
                else if (!link.HasValue) status = "Invalid Razer connection reply";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { status = "Razer battery timeout (read-only; no remote-mode/wake commands)"; }
            catch (Win32Exception exception) { status = $"Razer HID access failed (Win32 {exception.NativeErrorCode})"; }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { status = $"Razer battery unavailable ({exception.GetType().Name})"; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            result.Add(new(device.Path, device.ContainerId, device.FriendlyName, PeripheralType.Headset, connected,
                percentage, charging, PeripheralBatterySource.Vendor, DateTimeOffset.UtcNow, status, AuthoritativeType: true));
        }
        return result;
    }
}
