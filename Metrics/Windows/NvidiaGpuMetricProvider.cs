using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using DevOverlay.Configuration;

namespace DevOverlay.Metrics.Windows;

/// <summary>
/// Collects real-time metrics from one NVIDIA GPU through NVML. NVML is initialized once,
/// and the selected device handle is reused for every subsequent sample.
/// </summary>
public sealed class NvidiaGpuMetricProvider : IMetricProvider, IAsyncDisposable, ISelectableDeviceProvider
{
    private const int NvmlSuccess = 0;
    private const uint NvmlTemperatureGpu = 0;
    private const uint NvmlDeviceNameBufferLength = 96;
    private const uint NvmlDeviceUuidBufferLength = 80;

    private readonly object _syncRoot = new();
    private readonly DeviceSelection _selection;
    private IReadOnlyCollection<NvidiaDevice> _availableDevices = [];
    private bool _initializationAttempted;
    private bool _isInitialized;
    private nint _deviceHandle;

    public NvidiaGpuMetricProvider(DeviceSelection? selection = null)
    {
        _selection = selection ?? DeviceSelection.Auto;
    }

    public string Name => "NVIDIA GPU metrics";
    internal DeviceSelection Selection => _selection;
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!EnsureInitialized())
        {
            return Task.FromResult<IReadOnlyCollection<MetricSnapshot>>(CreateUnavailableSnapshots());
        }

        var timestamp = DateTimeOffset.UtcNow;
        IReadOnlyCollection<MetricSnapshot> snapshots =
        [
            CreateUtilizationSnapshot(timestamp),
            CreateTemperatureSnapshot(timestamp),
            CreatePowerSnapshot(timestamp),
            CreateVramSnapshot(timestamp)
        ];
        return Task.FromResult(snapshots);
    }

    private bool EnsureInitialized()
    {
        lock (_syncRoot)
        {
            if (_isInitialized)
            {
                return true;
            }

            if (_initializationAttempted)
            {
                return false;
            }

            _initializationAttempted = true;
            try
            {
                if (NvmlInitV2() != NvmlSuccess || NvmlDeviceGetCountV2(out var deviceCount) != NvmlSuccess)
                {
                    ShutdownAfterFailedInitialization();
                    return false;
                }

                _availableDevices = EnumerateDevices(deviceCount);
                _deviceHandle = SelectDevice(_availableDevices);
                if (_deviceHandle == nint.Zero)
                {
                    ShutdownAfterFailedInitialization(retainDevices: true);
                    return false;
                }

                _isInitialized = true;
                return true;
            }
            catch (DllNotFoundException)
            {
                ShutdownAfterFailedInitialization();
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                ShutdownAfterFailedInitialization();
                return false;
            }
        }
    }

    public IReadOnlyCollection<DeviceDescriptor> GetAvailableDevices()
    {
        _ = EnsureInitialized();

        lock (_syncRoot)
        {
            return _availableDevices
                .Where(device => device.Id is not null)
                .Select(device => new DeviceDescriptor(device.Id!, device.DisplayName))
                .ToArray();
        }
    }

    private static IReadOnlyCollection<NvidiaDevice> EnumerateDevices(uint deviceCount)
    {
        var devices = new List<NvidiaDevice>();

        for (uint index = 0; index < deviceCount; index++)
        {
            if (NvmlDeviceGetHandleByIndexV2(index, out var device) != NvmlSuccess)
            {
                continue;
            }

            var deviceId = ReadDeviceUuid(device);
            var displayName = ReadDeviceName(device) ?? "NVIDIA GPU";
            var totalMemory = NvmlDeviceGetMemoryInfo(device, out var memory) == NvmlSuccess ? memory.Total : 0;
            devices.Add(new NvidiaDevice(device, deviceId, displayName, totalMemory));
        }

        return devices;
    }

    private nint SelectDevice(IReadOnlyCollection<NvidiaDevice> devices)
    {
        if (_selection is SpecificDeviceSelection specific)
        {
            var selectedDevice = devices.FirstOrDefault(device => device.Id == specific.DeviceId);
            if (selectedDevice.Handle != nint.Zero)
            {
                return selectedDevice.Handle;
            }

            Trace.WriteLine($"Configured NVIDIA GPU '{specific.DeviceId}' was not found; metrics are unavailable.");
            return nint.Zero;
        }

        // Auto chooses the largest dedicated-memory GPU. Equal totals retain enumeration order,
        // which is NVML's lower device index.
        return devices.OrderByDescending(device => device.TotalMemory).FirstOrDefault().Handle;
    }

    private static string? ReadDeviceUuid(nint device)
    {
        var buffer = new StringBuilder((int)NvmlDeviceUuidBufferLength);
        return NvmlDeviceGetUuid(device, buffer, NvmlDeviceUuidBufferLength) == NvmlSuccess
            ? buffer.ToString()
            : null;
    }

    private static string? ReadDeviceName(nint device)
    {
        var buffer = new StringBuilder((int)NvmlDeviceNameBufferLength);
        return NvmlDeviceGetName(device, buffer, NvmlDeviceNameBufferLength) == NvmlSuccess
            ? buffer.ToString()
            : null;
    }

    private MetricSnapshot CreateUtilizationSnapshot(DateTimeOffset timestamp) =>
        NvmlDeviceGetUtilizationRates(_deviceHandle, out var utilization) == NvmlSuccess
            ? Available(MetricId.GpuUtilization, "GPU", NvidiaGpuMetricValueConverter.ToPercentage(utilization.Gpu), "%", timestamp)
            : Unavailable(MetricId.GpuUtilization, "GPU", "%", timestamp);

    private MetricSnapshot CreateTemperatureSnapshot(DateTimeOffset timestamp) =>
        NvmlDeviceGetTemperature(_deviceHandle, NvmlTemperatureGpu, out var temperature) == NvmlSuccess
            ? Available(MetricId.GpuTemperature, "GPU 온도", temperature, "°C", timestamp)
            : Unavailable(MetricId.GpuTemperature, "GPU 온도", "°C", timestamp);

    private MetricSnapshot CreatePowerSnapshot(DateTimeOffset timestamp) =>
        NvmlDeviceGetPowerUsage(_deviceHandle, out var milliwatts) == NvmlSuccess
            ? Available(MetricId.GpuPower, "GPU 전력", NvidiaGpuMetricValueConverter.MilliwattsToWatts(milliwatts), "W", timestamp)
            : Unavailable(MetricId.GpuPower, "GPU 전력", "W", timestamp);

    private MetricSnapshot CreateVramSnapshot(DateTimeOffset timestamp) =>
        NvmlDeviceGetMemoryInfo(_deviceHandle, out var memory) == NvmlSuccess
            ? Available(MetricId.GpuVramUsed, "VRAM", NvidiaGpuMetricValueConverter.BytesToGigabytes(memory.Used), "GB", timestamp)
            : Unavailable(MetricId.GpuVramUsed, "VRAM", "GB", timestamp);

    private static MetricSnapshot Available(MetricId id, string displayName, double value, string unit, DateTimeOffset timestamp) =>
        new(id, MetricCategory.Gpu, displayName, value, unit, true, timestamp);

    private static MetricSnapshot Unavailable(MetricId id, string displayName, string unit, DateTimeOffset timestamp) =>
        new(id, MetricCategory.Gpu, displayName, null, unit, false, timestamp);

    private static IReadOnlyCollection<MetricSnapshot> CreateUnavailableSnapshots() =>
    [
        MetricSnapshot.Unavailable(MetricId.GpuUtilization, MetricCategory.Gpu, "GPU", "%"),
        MetricSnapshot.Unavailable(MetricId.GpuTemperature, MetricCategory.Gpu, "GPU 온도", "°C"),
        MetricSnapshot.Unavailable(MetricId.GpuPower, MetricCategory.Gpu, "GPU 전력", "W"),
        MetricSnapshot.Unavailable(MetricId.GpuVramUsed, MetricCategory.Gpu, "VRAM", "GB")
    ];

    private void ShutdownAfterFailedInitialization(bool retainDevices = false)
    {
        try
        {
            NvmlShutdown();
        }
        catch (DllNotFoundException)
        {
            // NVML could not be loaded, so there is no native state to release.
        }
        catch (EntryPointNotFoundException)
        {
            // An older or incomplete NVML export set cannot be safely used.
        }

        _deviceHandle = nint.Zero;
        if (!retainDevices) _availableDevices = [];
    }

    public ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_isInitialized)
            {
                NvmlShutdown();
                _isInitialized = false;
                _deviceHandle = nint.Zero;
                _availableDevices = [];
            }
        }

        return ValueTask.CompletedTask;
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInitV2();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int NvmlDeviceGetCountV2(out uint deviceCount);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandleByIndexV2(uint index, out nint device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUUID", CharSet = CharSet.Ansi)]
    private static extern int NvmlDeviceGetUuid(nint device, StringBuilder uuid, uint length);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
    private static extern int NvmlDeviceGetName(nint device, StringBuilder name, uint length);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int NvmlDeviceGetUtilizationRates(nint device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int NvmlDeviceGetTemperature(nint device, uint sensorType, out uint temperature);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int NvmlDeviceGetPowerUsage(nint device, out uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static extern int NvmlDeviceGetMemoryInfo(nint device, out NvmlMemory memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private readonly record struct NvidiaDevice(nint Handle, string? Id, string DisplayName, ulong TotalMemory);
}
