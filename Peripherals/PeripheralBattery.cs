using System.Security.Cryptography;
using System.Text;

namespace DevOverlay.Peripherals;

public enum PeripheralType { Mouse, Keyboard, Headset, Controller, Earbuds, Other }
public enum PeripheralBatterySource { WindowsProperty, BluetoothGatt, StandardHid, Vendor }
public sealed record PeripheralPreference(string Identity, string FriendlyName, PeripheralType Type,
    string DefaultLabel, bool Show = false, string CustomName = "")
{
    public const int MaximumNameLength = 12;
    public string HudLabel => NormalizeName(CustomName) is { Length: > 0 } name ? name : DefaultLabel;
    public static string NormalizeName(string? name) => string.Concat((name ?? "").Trim()
        .EnumerateRunes().Where(rune => !System.Text.Rune.IsControl(rune)).Take(MaximumNameLength));
}
public sealed record PeripheralBatteryReading(string Identity, string FriendlyName, PeripheralType Type,
    bool Connected, double? Percentage, bool? Charging, PeripheralBatterySource Source,
    DateTimeOffset UpdatedAt, string Status)
{
    public double? ValidPercentage => Connected && Percentage is >= 0 and <= 100 &&
        DateTimeOffset.UtcNow - UpdatedAt <= TimeSpan.FromMinutes(2) ? Percentage : null;
}
internal sealed record PeripheralObservation(string InterfaceId, string? ContainerId, string FriendlyName,
    PeripheralType Type, bool Connected, double? Percentage, bool? Charging,
    PeripheralBatterySource Source, DateTimeOffset UpdatedAt, string Status, bool AuthoritativeType = false)
{
    // 원본 주소/일련번호는 UI와 설정 파일에 노출하지 않는다. 이름/VID/PID로 기기를 합치지 않는다.
    internal string Identity => StableIdentity(ContainerId, InterfaceId);
    internal static string StableIdentity(string? containerId, string interfaceId)
    {
        var key = Guid.TryParse(containerId, out var guid) && guid != Guid.Empty
            ? "container:" + guid.ToString("D") : "interface:" + interfaceId.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}
internal interface IPeripheralBatteryBackend
{
    string Name { get; }
    Task<IReadOnlyList<PeripheralObservation>> CollectAsync(CancellationToken cancellationToken);
}
internal static class PeripheralBatteryMerge
{
    internal static bool IsPercentage(double? value) => value is >= 0 and <= 100;
    internal static IReadOnlyList<PeripheralBatteryReading> Merge(IEnumerable<PeripheralObservation> observations)
        => observations.GroupBy(item => item.Identity).Select(group =>
        {
            var ordered = group.OrderBy(item => item.Source).ThenBy(item => item.InterfaceId, StringComparer.Ordinal).ToArray();
            var verified = ordered.Where(item => item.AuthoritativeType).ToArray();
            var selected = ordered.FirstOrDefault(item => item.Connected && IsPercentage(item.Percentage) &&
                DateTimeOffset.UtcNow - item.UpdatedAt <= TimeSpan.FromMinutes(2)) ??
                verified.FirstOrDefault() ??
                ordered.FirstOrDefault(item => item.Connected) ?? ordered[0];
            var valid = selected.Connected && IsPercentage(selected.Percentage) &&
                DateTimeOffset.UtcNow - selected.UpdatedAt <= TimeSpan.FromMinutes(2);
            var type = ordered.Where(item => item.Type != PeripheralType.Other).Select(item => item.Type)
                .Distinct().ToArray();
            var verifiedTypes = verified.Select(item => item.Type).Distinct().ToArray();
            // 복합 수신기의 서로 다른 기능은 container만으로 개별 무선 장치를 분리할 수 없다.
            return new PeripheralBatteryReading(group.Key, verifiedTypes.Length == 1 ? verified[0].FriendlyName : selected.FriendlyName,
                verifiedTypes.Length == 1 ? verifiedTypes[0] : type.Length == 1 ? type[0] : PeripheralType.Other, selected.Connected,
                valid ? selected.Percentage : null,
                selected.Charging, selected.Source, selected.UpdatedAt,
                valid ? selected.Status :
                    string.Join("; ", ordered.Select(item => $"{item.Source}: {item.Status}").Distinct()));
        }).OrderBy(item => item.Type).ThenBy(item => item.Identity, StringComparer.Ordinal).ToArray();
    internal static string DefaultLabel(PeripheralType type) => type switch
    {
        PeripheralType.Mouse => "MSE", PeripheralType.Keyboard => "KB", PeripheralType.Headset => "HS",
        PeripheralType.Controller => "PAD", PeripheralType.Earbuds => "BUD", _ => "DEV"
    };
    internal static IReadOnlyList<PeripheralPreference> AddPreferences(IEnumerable<PeripheralPreference> saved,
        IEnumerable<PeripheralBatteryReading> readings)
    {
        var result = saved.GroupBy(item => item.Identity).Select(group => group.First()).ToList();
        foreach (var reading in readings.OrderBy(item => item.Type).ThenBy(item => item.Identity, StringComparer.Ordinal))
        {
            var previousIndex = result.FindIndex(item => item.Identity == reading.Identity);
            if (previousIndex >= 0)
            {
                var previous = result[previousIndex];
                if (previous.Type == PeripheralType.Other && reading.Type != PeripheralType.Other)
                {
                    var migratedPrefix = DefaultLabel(reading.Type);
                    var migratedLabel = migratedPrefix;
                    var usedLabels = result.Select(item => item.DefaultLabel).ToHashSet(StringComparer.Ordinal);
                    for (var suffix = 2; usedLabels.Contains(migratedLabel); suffix++) migratedLabel = migratedPrefix + suffix;
                    // 분류 개선만 반영하고 사용자가 저장한 Show/custom name은 절대 초기화하지 않는다.
                    result[previousIndex] = previous with { Type = reading.Type, FriendlyName = reading.FriendlyName, DefaultLabel = migratedLabel };
                }
                continue;
            }
            var prefix = DefaultLabel(reading.Type);
            var used = result.Select(item => item.DefaultLabel).ToHashSet(StringComparer.Ordinal);
            var label = prefix;
            for (var suffix = 2; used.Contains(label); suffix++) label = prefix + suffix;
            result.Add(new(reading.Identity, reading.FriendlyName, reading.Type, label, reading.ValidPercentage.HasValue));
        }
        return result.OrderBy(item => item.Type).ThenBy(item => item.Identity, StringComparer.Ordinal).ToArray();
    }
}
