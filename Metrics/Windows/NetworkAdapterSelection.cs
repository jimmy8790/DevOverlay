using System.Net.NetworkInformation;
using DevOverlay.Configuration;

namespace DevOverlay.Metrics.Windows;

internal readonly record struct NetworkAdapterCounter(
    string Id,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    bool HasGateway,
    ulong Received,
    ulong Sent);

internal static class NetworkAdapterSelection
{
    public static NetworkAdapterCounter[] Select(IEnumerable<NetworkAdapterCounter> adapters, DeviceSelection selection)
    {
        if (selection is SpecificDeviceSelection specific)
        {
            // Specific selection is resolved before Auto routing; never substitute another adapter.
            return adapters.Where(adapter =>
                string.Equals(adapter.Id, specific.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                adapter.Status == OperationalStatus.Up).ToArray();
        }

        var active = adapters.Where(adapter =>
            adapter.Status == OperationalStatus.Up &&
            adapter.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211).ToArray();
        var routed = active.Where(adapter => adapter.HasGateway).ToArray();
        return routed.Length > 0 ? routed : active;
    }
}
