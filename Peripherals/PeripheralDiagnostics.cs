using System.Text.Json;
using System.Text.RegularExpressions;
using DevOverlay.Peripherals.VendorBackends;

namespace DevOverlay.Peripherals;

internal sealed record PeripheralBackendAttempt(string Backend, string Result, int ObservationCount);
internal static class PeripheralDiagnostics
{
    // 명시한 schema에 필요한 정보만 투영한다. 원본 Path/ContainerId/serial/report는 직렬화하지 않는다.
    internal static string Serialize(IReadOnlyList<PeripheralBatteryReading> readings,
        IReadOnlyList<PeripheralObservation> observations, IReadOnlyList<PeripheralHidInterface> interfaces,
        IReadOnlyList<PeripheralBackendAttempt> attempts)
    {
        var report = new
        {
            SchemaVersion = 1, ExportedAt = DateTimeOffset.UtcNow,
            Privacy = "Hashed identities; product names included. No raw device paths, container IDs, Bluetooth addresses, serial queries, custom names or HID payloads. Review product names before sharing.",
            BackendAttempts = attempts,
            Devices = readings.Select(reading => new
            {
                HashedIdentity = reading.Identity, ProductName = SafeText(reading.FriendlyName), Type = reading.Type.ToString(),
                reading.Connected, Percentage = reading.ValidPercentage, reading.Charging, Source = reading.Source.ToString(),
                reading.UpdatedAt, Status = SafeText(reading.Status),
                InterfaceCount = interfaces.Count(item => item.Identity == reading.Identity),
                BatteryResults = observations.Where(item => item.Identity == reading.Identity).Select(item => new
                {
                    Backend = item.Source.ToString(), item.Connected,
                    Percentage = PeripheralBatteryMerge.IsPercentage(item.Percentage) ? item.Percentage : null,
                    item.UpdatedAt, Status = SafeText(item.Status)
                }).Distinct().ToArray()
            }).ToArray(),
            HidInterfaces = interfaces.Select(item => new
            {
                HashedIdentity = item.Identity, HashedInterface = PeripheralObservation.StableIdentity(null, item.Path),
                ProductName = SafeText(item.FriendlyName),
                VID = item.Metadata.VendorId.ToString("X4"), PID = item.Metadata.ProductId.ToString("X4"),
                UsagePage = item.Metadata.UsagePage.ToString("X4"), UsageId = item.Metadata.UsageId.ToString("X4"),
                item.Metadata.InputLength, item.Metadata.OutputLength, item.Metadata.FeatureLength,
                item.Metadata.InputReportId, item.Metadata.OutputReportId, item.Metadata.InputValueCaps, item.Metadata.InputButtonCaps,
                MetadataAccessible = item.Metadata.Accessible, item.Metadata.Win32Error,
                WindowsBatteryProperty = item.WindowsBatteryProperty is >= 0 and <= 101 ? item.WindowsBatteryProperty : null,
                VendorMatch = PulsarNordicProtocol.Matches(item.Metadata) ? "PulsarNordic" :
                    RazerBarracudaPaProtocol.Matches(item.Metadata) ? "RazerBarracudaPA0550" : "No verified battery protocol match"
            }).ToArray()
        };
        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }
    private static string SafeText(string text)
    {
        var safe = string.Concat(text.Where(character => !char.IsControl(character))).Trim();
        safe = Regex.Replace(safe, @"(?i)(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}", "[redacted address]");
        // backend는 예외의 타입/코드만 반환한다. 혹시 포함된 원본 PnP path도 내보내지 않는다.
        safe = Regex.Replace(safe, @"(?i)(?:\\\\\?\\|USB\\|HID\\|BTHENUM\\|BTHLEDEVICE\\)\S+", "[redacted device path]");
        return safe.Length > 240 ? safe[..240] : safe;
    }
}
