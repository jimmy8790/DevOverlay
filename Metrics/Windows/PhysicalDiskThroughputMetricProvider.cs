using System.ComponentModel;
using System.Diagnostics;
using DevOverlay.Configuration;
using DevOverlay.Platform.Windows;

namespace DevOverlay.Metrics.Windows;

/// <summary>
/// Reads the system volume through LogicalDisk, or Auto/specific physical targets through
/// PhysicalDisk. Both Windows categories supply already time-normalized byte rates.
/// </summary>
public sealed class PhysicalDiskThroughputMetricProvider : IMetricProvider, ISelectableDeviceProvider, IAsyncDisposable
{
    private const string CategoryName = "PhysicalDisk";
    private const string LogicalCategoryName = "LogicalDisk";
    private const string ReadCounterName = "Disk Read Bytes/sec";
    private const string WriteCounterName = "Disk Write Bytes/sec";
    private const string DeviceIdPrefix = "physical-disk:";
    private const string SystemVolumeIdPrefix = "system-volume:";

    private readonly DeviceSelection _selection;
    private DiskCounters[] _counters = [];
    private bool _isPrimed;
    private bool _isDisposed;

    public PhysicalDiskThroughputMetricProvider(DeviceSelection? selection = null)
    {
        _selection = selection ?? DeviceSelection.SystemDrive;
    }

    public string Name => "Windows physical disk throughput";
    internal DeviceSelection Selection => _selection;
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (!EnsureCounters())
        {
            return Task.FromResult<IReadOnlyCollection<MetricSnapshot>>(CreateUnavailableSnapshots());
        }

        try
        {
            if (!_isPrimed)
            {
                PrimeCounters();
                _isPrimed = true;
                return Task.FromResult<IReadOnlyCollection<MetricSnapshot>>(CreateUnavailableSnapshots());
            }

            var readBytesPerSecond = 0d;
            var writeBytesPerSecond = 0d;
            foreach (var counter in _counters)
            {
                var read = counter.ReadBytesPerSecond.NextValue();
                var write = counter.WriteBytesPerSecond.NextValue();
                if (!double.IsFinite(read) || !double.IsFinite(write) || read < 0 || write < 0)
                {
                    ResetCounters();
                    return Task.FromResult<IReadOnlyCollection<MetricSnapshot>>(CreateUnavailableSnapshots());
                }

                readBytesPerSecond += read;
                writeBytesPerSecond += write;
            }

            var timestamp = DateTimeOffset.UtcNow;
            var readDisplay = ByteRateFormatter.Format(readBytesPerSecond);
            var writeDisplay = ByteRateFormatter.Format(writeBytesPerSecond);
            IReadOnlyCollection<MetricSnapshot> snapshots =
            [
                new(MetricId.StorageRead, MetricCategory.Storage, "읽기", readDisplay.Value, readDisplay.Unit, true, timestamp),
                new(MetricId.StorageWrite, MetricCategory.Storage, "쓰기", writeDisplay.Value, writeDisplay.Unit, true, timestamp)
            ];
            return Task.FromResult(snapshots);
        }
        catch (InvalidOperationException)
        {
            ResetCounters();
            return Task.FromResult<IReadOnlyCollection<MetricSnapshot>>(CreateUnavailableSnapshots());
        }
        catch (Win32Exception)
        {
            ResetCounters();
            return Task.FromResult<IReadOnlyCollection<MetricSnapshot>>(CreateUnavailableSnapshots());
        }
    }

    public IReadOnlyCollection<DeviceDescriptor> GetAvailableDevices()
    {
        try
        {
            return new PerformanceCounterCategory(CategoryName)
                .GetInstanceNames()
                .Where(IsPhysicalDiskInstance)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(instanceName => instanceName, StringComparer.OrdinalIgnoreCase)
                .Select(instanceName => new DeviceDescriptor(ToDeviceId(instanceName), instanceName))
                .ToArray();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (Win32Exception)
        {
            return [];
        }
    }

    internal static DeviceDescriptor[] SelectDevices(
        DeviceSelection selection,
        IEnumerable<DeviceDescriptor> availableDevices)
    {
        var available = availableDevices
            .DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (selection is SpecificDeviceSelection specific)
        {
            return available.Where(device => string.Equals(device.Id, specific.DeviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        if (selection is SystemDriveDeviceSelection) return [];

        return available;
    }

    internal static DiskCounterTarget[] ResolveCounterTargets(
        DeviceSelection selection, IEnumerable<DeviceDescriptor> physicalDevices, string? systemVolume)
    {
        if (selection is SystemDriveDeviceSelection)
            return string.IsNullOrWhiteSpace(systemVolume) ? [] :
                [new(SystemVolumeIdPrefix + systemVolume, LogicalCategoryName, systemVolume)];

        return SelectDevices(selection, physicalDevices)
            .Select(device => new DiskCounterTarget(device.Id, CategoryName, ToInstanceName(device.Id)))
            .ToArray();
    }

    public ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            ResetCounters();
            _isDisposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private bool EnsureCounters()
    {
        var selectedDevices = ResolveCounterTargets(_selection,
            _selection is SystemDriveDeviceSelection ? Array.Empty<DeviceDescriptor>() : GetAvailableDevices(),
            _selection is SystemDriveDeviceSelection ? SystemDriveResolver.GetVolumeName() : null);
        if (HaveSameDeviceSet(selectedDevices))
        {
            return _counters.Length > 0;
        }

        ResetCounters();
        if (selectedDevices.Length == 0)
        {
            return false;
        }

        try
        {
            _counters = selectedDevices
                .Select(device => new DiskCounters(
                    device.Id,
                    new PerformanceCounter(device.Category, ReadCounterName, device.InstanceName, true),
                    new PerformanceCounter(device.Category, WriteCounterName, device.InstanceName, true)))
                .ToArray();
            return true;
        }
        catch (InvalidOperationException)
        {
            ResetCounters();
            return false;
        }
        catch (Win32Exception)
        {
            ResetCounters();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            ResetCounters();
            return false;
        }
    }

    private bool HaveSameDeviceSet(IReadOnlyList<DiskCounterTarget> selectedDevices) =>
        _counters.Length == selectedDevices.Count &&
        _counters.Select(counter => counter.DeviceId)
            .SequenceEqual(selectedDevices.Select(device => device.Id), StringComparer.OrdinalIgnoreCase);

    private void PrimeCounters()
    {
        foreach (var counter in _counters)
        {
            _ = counter.ReadBytesPerSecond.NextValue();
            _ = counter.WriteBytesPerSecond.NextValue();
        }
    }

    private void ResetCounters()
    {
        foreach (var counter in _counters)
        {
            counter.Dispose();
        }

        _counters = [];
        _isPrimed = false;
    }

    private static bool IsPhysicalDiskInstance(string instanceName) =>
        !string.IsNullOrWhiteSpace(instanceName) &&
        !string.Equals(instanceName, "_Total", StringComparison.OrdinalIgnoreCase);

    private static string ToDeviceId(string instanceName) => DeviceIdPrefix + instanceName;

    private static string ToInstanceName(string deviceId) => deviceId.StartsWith(DeviceIdPrefix, StringComparison.OrdinalIgnoreCase)
        ? deviceId[DeviceIdPrefix.Length..]
        : deviceId.StartsWith(SystemVolumeIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? deviceId[SystemVolumeIdPrefix.Length..]
            : deviceId;

    private static IReadOnlyCollection<MetricSnapshot> CreateUnavailableSnapshots() =>
    [
        MetricSnapshot.Unavailable(MetricId.StorageRead, MetricCategory.Storage, "읽기", "B/s"),
        MetricSnapshot.Unavailable(MetricId.StorageWrite, MetricCategory.Storage, "쓰기", "B/s")
    ];

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private sealed class DiskCounters(string deviceId, PerformanceCounter readBytesPerSecond, PerformanceCounter writeBytesPerSecond) : IDisposable
    {
        public string DeviceId { get; } = deviceId;
        public PerformanceCounter ReadBytesPerSecond { get; } = readBytesPerSecond;
        public PerformanceCounter WriteBytesPerSecond { get; } = writeBytesPerSecond;

        public void Dispose()
        {
            ReadBytesPerSecond.Dispose();
            WriteBytesPerSecond.Dispose();
        }
    }

    internal readonly record struct DiskCounterTarget(string Id, string Category, string InstanceName);
}
