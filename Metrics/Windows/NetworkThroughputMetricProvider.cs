using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DevOverlay.Configuration;

namespace DevOverlay.Metrics.Windows;

/// <summary>
/// Aggregates routed Ethernet and Wi-Fi interface counters, then calculates byte rates from
/// consecutive samples. It deliberately omits loopback and tunnel interfaces.
/// </summary>
public sealed class NetworkThroughputMetricProvider : IMetricProvider, ISelectableDeviceProvider
{
    private readonly DeviceSelection _selection;
    private NetworkCounterSample? _previousSample;

    public NetworkThroughputMetricProvider(DeviceSelection? selection = null)
    {
        _selection = selection ?? DeviceSelection.Auto;
    }

    public string Name => "Windows network throughput";
    internal DeviceSelection Selection => _selection;
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentSample = ReadCurrentSample();
        if (currentSample is null || !NetworkThroughputCalculator.TryCalculate(_previousSample, currentSample.Value, out var throughput))
        {
            _previousSample = currentSample;
            return Task.FromResult<IReadOnlyCollection<MetricSnapshot>>(CreateUnavailableSnapshots());
        }

        _previousSample = currentSample;
        var timestamp = DateTimeOffset.UtcNow;
        var download = NetworkThroughputFormatter.Format(throughput.DownloadBytesPerSecond);
        var upload = NetworkThroughputFormatter.Format(throughput.UploadBytesPerSecond);
        IReadOnlyCollection<MetricSnapshot> snapshots =
        [
            new(MetricId.NetworkDownload, MetricCategory.Network, "다운로드", download.Value, download.Unit, true, timestamp),
            new(MetricId.NetworkUpload, MetricCategory.Network, "업로드", upload.Value, upload.Unit, true, timestamp)
        ];
        return Task.FromResult(snapshots);
    }

    public IReadOnlyCollection<DeviceDescriptor> GetAvailableDevices() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .Select(networkInterface => new DeviceDescriptor(networkInterface.Id, networkInterface.Name))
            .OrderBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private NetworkCounterSample? ReadCurrentSample()
    {
        var candidates = new List<NetworkAdapterCounter>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(networkInterface => networkInterface.NetworkInterfaceType is
                         NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
        {
            try
            {
                var statistics = networkInterface.GetIPStatistics();
                if (statistics.BytesReceived < 0 || statistics.BytesSent < 0) continue;
                candidates.Add(new NetworkAdapterCounter(
                    networkInterface.Id,
                    networkInterface.NetworkInterfaceType,
                    networkInterface.OperationalStatus,
                    HasIpv4Gateway(networkInterface),
                    (ulong)statistics.BytesReceived,
                    (ulong)statistics.BytesSent));
            }
            catch (NetworkInformationException)
            {
                // Interface state can change between enumeration and statistics retrieval.
            }
        }

        var selected = NetworkAdapterSelection.Select(candidates, _selection);
        if (selected.Length == 0)
        {
            return null;
        }

        ulong downloadBytes = 0;
        ulong uploadBytes = 0;
        var interfaceIds = new List<string>(selected.Length);
        foreach (var candidate in selected)
        {
            try
            {
                downloadBytes = checked(downloadBytes + candidate.Received);
                uploadBytes = checked(uploadBytes + candidate.Sent);
                interfaceIds.Add(candidate.Id);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        if (interfaceIds.Count == 0)
        {
            return null;
        }

        interfaceIds.Sort(StringComparer.Ordinal);
        return new NetworkCounterSample(
            string.Join("|", interfaceIds),
            downloadBytes,
            uploadBytes,
            Stopwatch.GetTimestamp());
    }

    private static bool HasIpv4Gateway(NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface.GetIPProperties().GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork && gateway.Address != IPAddress.Any);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static IReadOnlyCollection<MetricSnapshot> CreateUnavailableSnapshots() =>
    [
        MetricSnapshot.Unavailable(MetricId.NetworkDownload, MetricCategory.Network, "다운로드", "B/s"),
        MetricSnapshot.Unavailable(MetricId.NetworkUpload, MetricCategory.Network, "업로드", "B/s")
    ];

}
