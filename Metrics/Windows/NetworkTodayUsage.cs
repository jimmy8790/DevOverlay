using DevOverlay.Configuration;

namespace DevOverlay.Metrics.Windows;

/// <summary>A profile's Windows-recorded received and sent bytes for one local-day interval.</summary>
internal readonly record struct NetworkTodayUsage(Guid AdapterId, uint InterfaceType, ulong Received, ulong Sent);

internal static class NetworkTodayUsageCalculator
{
    public static ulong? Sum(IEnumerable<NetworkTodayUsage> usages, DeviceSelection selection, IReadOnlySet<Guid> physicalAdapterIds)
    {
        Guid? selectedId = selection is SpecificDeviceSelection specific && Guid.TryParse(specific.DeviceId, out var id)
            ? id : null;
        if (selection is SpecificDeviceSelection && selectedId is null) return null;

        ulong total = 0;
        var found = false;
        foreach (var usage in usages)
        {
            // IANA 6 = Ethernet, 71 = Wi-Fi. VPN/tunnel/loopback profiles are not added twice.
            if (usage.InterfaceType is not (6 or 71) ||
                (selectedId.HasValue && usage.AdapterId != selectedId.Value) ||
                (selection is not SpecificDeviceSelection && !physicalAdapterIds.Contains(usage.AdapterId))) continue;
            try
            {
                total = checked(total + usage.Received + usage.Sent);
                found = true;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        return found ? total : null;
    }
}
