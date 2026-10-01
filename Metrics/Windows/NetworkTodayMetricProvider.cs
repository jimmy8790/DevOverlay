using System.Diagnostics;
using System.Management;
using DevOverlay.Configuration;
using Windows.Networking.Connectivity;

namespace DevOverlay.Metrics.Windows;

/// <summary>Queries Windows' per-connection historical usage, including time before app launch.</summary>
public sealed class NetworkTodayMetricProvider(DeviceSelection selection) : IMetricProvider
{
    public string Name => "Windows network today usage";
    internal DeviceSelection Selection => selection;
    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(1);
    public bool UsesFixedRefreshInterval => true;

    public async Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var total = await QueryTodayBytesAsync(now, cancellationToken);
            return [NetworkTodayFormatter.CreateSnapshot(total, now)];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Windows network usage history unavailable: {exception}");
            return [NetworkTodayFormatter.CreateSnapshot(null, DateTimeOffset.UtcNow)];
        }
    }

    internal async Task<ulong?> QueryTodayBytesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var midnight = GetLocalMidnight(now);
        var physicalAdapterIds = selection is SpecificDeviceSelection
            ? new HashSet<Guid>()
            : GetPhysicalAdapterIds();
        var usages = new List<NetworkTodayUsage>();
        foreach (var profile in NetworkInformation.GetConnectionProfiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var adapter = profile.NetworkAdapter;
            if (adapter is null || adapter.IanaInterfaceType is not (6 or 71)) continue;
            if (selection is SpecificDeviceSelection specific)
            {
                if (!Guid.TryParse(specific.DeviceId, out var selectedId) || adapter.NetworkAdapterId != selectedId) continue;
            }
            else if (!physicalAdapterIds.Contains(adapter.NetworkAdapterId)) continue;
            var samples = await profile.GetNetworkUsageAsync(midnight, now, DataUsageGranularity.Total,
                new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare });
            foreach (var sample in samples)
            {
                usages.Add(new NetworkTodayUsage(adapter.NetworkAdapterId, adapter.IanaInterfaceType,
                    sample.BytesReceived, sample.BytesSent));
            }
        }

        return NetworkTodayUsageCalculator.Sum(usages, selection, physicalAdapterIds);
    }

    internal static DateTimeOffset GetLocalMidnight(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0,
            TimeZoneInfo.Local.GetUtcOffset(local.Date));
    }

    private static HashSet<Guid> GetPhysicalAdapterIds()
    {
        // Same backing class as Get-NetAdapter -Physical; Virtual excludes Ethernet-looking VPNs.
        using var searcher = new ManagementObjectSearcher(
            @"root\StandardCimv2",
            "SELECT InterfaceGuid, HardwareInterface, Virtual FROM MSFT_NetAdapter");
        using var adapters = searcher.Get();
        var ids = new HashSet<Guid>();
        foreach (ManagementObject adapter in adapters)
        {
            if (adapter["HardwareInterface"] is true && adapter["Virtual"] is false &&
                Guid.TryParse(adapter["InterfaceGuid"]?.ToString(), out var id))
                ids.Add(id);
        }
        return ids;
    }
}
