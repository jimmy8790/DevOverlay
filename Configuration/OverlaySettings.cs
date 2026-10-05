using DevOverlay.Metrics;

namespace DevOverlay.Configuration;

/// <summary>Settings remain UI-independent so they can be persisted and applied by the host.</summary>
public sealed record OverlaySettings
{
    public const int MinimumRefreshIntervalMs = 250;
    public const int DefaultRefreshIntervalMs = 500;
    public const int MaximumRefreshIntervalMs = 2000;
    public static IReadOnlyList<MetricCategory> DefaultGroupOrder { get; } =
        [MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Frame, MetricCategory.Latency, MetricCategory.Storage, MetricCategory.Network, MetricCategory.AiUsage, MetricCategory.PeripheralBattery];
    public bool PeripheralBatteriesEnabled { get; init; } = true;
    public IReadOnlyList<DevOverlay.Peripherals.PeripheralPreference> PeripheralDevices { get; init; } = [];
    public bool IsVisible { get; init; } = true;
    /// <summary>Semantic modifier/virtual-key pair used by the Windows global HUD visibility hotkey.</summary>
    public OverlayHotkey Hotkey { get; init; } = OverlayHotkey.Default;
    /// <summary>How often normal live telemetry is published to the HUD. Specialized providers can retain a slower cadence.</summary>
    public int RefreshIntervalMs { get; init; } = DefaultRefreshIntervalMs;
    public OverlayPosition Position { get; init; } = OverlayPosition.CreateDefault();
    public IReadOnlyList<MetricCategory> GroupOrder { get; init; } = DefaultGroupOrder;
    public PopupBehavior PopupBehavior { get; init; } = PopupBehavior.Disabled;
    public OverlayAppearance Appearance { get; init; } = OverlayAppearance.CreateDefault();
    public DeviceSelection GpuDeviceSelection { get; init; } = DeviceSelection.Auto;
    public DeviceSelection NetworkDeviceSelection { get; init; } = DeviceSelection.Auto;
    public DeviceSelection StorageDeviceSelection { get; init; } = DeviceSelection.SystemDrive;
    public DeviceSelection FpsTargetSelection { get; init; } = DeviceSelection.Auto;
    public IReadOnlySet<MetricCategory> EnabledGroups { get; init; } = new HashSet<MetricCategory>();
    public IReadOnlySet<MetricId> EnabledMetrics { get; init; } = new HashSet<MetricId>();

    public static IReadOnlyList<MetricCategory> NormalizeGroupOrder(IEnumerable<MetricCategory>? order)
    {
        var result = (order ?? []).Where(Enum.IsDefined).Distinct().ToList();
        if (result.Count == 0) return DefaultGroupOrder;
        if (!result.Contains(MetricCategory.Frame))
        {
            var gpuIndex = result.IndexOf(MetricCategory.Gpu);
            result.Insert(gpuIndex < 0 ? result.Count : gpuIndex + 1, MetricCategory.Frame);
        }
        if (!result.Contains(MetricCategory.Latency))
        {
            var frameIndex = result.IndexOf(MetricCategory.Frame);
            result.Insert(frameIndex < 0 ? result.Count : frameIndex + 1, MetricCategory.Latency);
        }
        foreach (var category in DefaultGroupOrder)
            if (!result.Contains(category)) result.Add(category);
        return result;
    }

    public static OverlaySettings CreateDefault() => new()
    {
        EnabledGroups = new HashSet<MetricCategory>
        {
            MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Frame, MetricCategory.Latency, MetricCategory.Storage, MetricCategory.Network
        },
        EnabledMetrics = new HashSet<MetricId>
        {
            MetricId.CpuUtilization,
            MetricId.CpuTemperature, MetricId.CpuPower,
            MetricId.GpuUtilization, MetricId.GpuTemperature, MetricId.GpuPower, MetricId.GpuVramUsed,
            MetricId.StorageRead, MetricId.StorageWrite,
            MetricId.NetworkDownload, MetricId.NetworkUpload, MetricId.NetworkTodayTotal,
            MetricId.FramesPerSecond, MetricId.OnePercentLow, MetricId.FrameTime, MetricId.Latency
        }
    };

    public static int NormalizeRefreshIntervalMs(int value) => Math.Clamp(value, MinimumRefreshIntervalMs, MaximumRefreshIntervalMs);

    public static OverlaySettings CreateForCurrentFeatures(
        bool cpuEnabled,
        bool gpuEnabled,
        bool networkEnabled,
        bool storageEnabled,
        DeviceSelection gpuDeviceSelection,
        DeviceSelection networkDeviceSelection,
        DeviceSelection storageDeviceSelection,
        bool isVisible = true,
        bool cpuUsageEnabled = true,
        bool gpuUsageEnabled = true,
        bool gpuTemperatureEnabled = true,
        bool gpuPowerEnabled = true,
        bool gpuVramEnabled = true,
        bool networkDownloadEnabled = true,
        bool networkUploadEnabled = true,
        bool storageReadEnabled = true,
        bool storageWriteEnabled = true,
        bool cpuTemperatureEnabled = true,
        bool cpuPowerEnabled = true,
        bool networkTodayTotalEnabled = true,
        bool fpsEnabled = true,
        bool framesPerSecondEnabled = true,
        bool onePercentLowEnabled = true,
        bool frameTimeEnabled = true,
        DeviceSelection? fpsTargetSelection = null,
        bool latencyEnabled = true,
        bool renderLatencyEnabled = true,
        bool aiUsageEnabled = false,
        bool codexAccountLimitsEnabled = false,
        bool claudeUsageEnabled = false) => new()
    {
        IsVisible = isVisible,
        GpuDeviceSelection = gpuDeviceSelection,
        NetworkDeviceSelection = networkDeviceSelection,
        StorageDeviceSelection = storageDeviceSelection,
        FpsTargetSelection = fpsTargetSelection ?? DeviceSelection.Auto,
        EnabledGroups = CreateEnabledGroups(cpuEnabled, gpuEnabled, networkEnabled, storageEnabled, fpsEnabled, latencyEnabled, aiUsageEnabled),
        EnabledMetrics = CreateEnabledMetrics(
            cpuUsageEnabled,
            gpuUsageEnabled,
            gpuTemperatureEnabled,
            gpuPowerEnabled,
            gpuVramEnabled,
            networkDownloadEnabled,
            networkUploadEnabled,
            storageReadEnabled,
            storageWriteEnabled,
            cpuTemperatureEnabled,
            cpuPowerEnabled,
            networkTodayTotalEnabled,
            framesPerSecondEnabled,
            onePercentLowEnabled,
            frameTimeEnabled,
            renderLatencyEnabled,
            codexAccountLimitsEnabled,
            claudeUsageEnabled)
    };

    private static IReadOnlySet<MetricCategory> CreateEnabledGroups(
        bool cpuEnabled,
        bool gpuEnabled,
        bool networkEnabled,
        bool storageEnabled,
        bool fpsEnabled,
        bool latencyEnabled,
        bool aiUsageEnabled)
    {
        var groups = new HashSet<MetricCategory>();
        if (cpuEnabled) groups.Add(MetricCategory.Cpu);
        if (gpuEnabled) groups.Add(MetricCategory.Gpu);
        if (networkEnabled) groups.Add(MetricCategory.Network);
        if (storageEnabled) groups.Add(MetricCategory.Storage);
        if (fpsEnabled) groups.Add(MetricCategory.Frame);
        if (latencyEnabled) groups.Add(MetricCategory.Latency);
        if (aiUsageEnabled) groups.Add(MetricCategory.AiUsage);
        return groups;
    }

    private static IReadOnlySet<MetricId> CreateEnabledMetrics(
        bool cpuUsageEnabled,
        bool gpuUsageEnabled,
        bool gpuTemperatureEnabled,
        bool gpuPowerEnabled,
        bool gpuVramEnabled,
        bool networkDownloadEnabled,
        bool networkUploadEnabled,
        bool storageReadEnabled,
        bool storageWriteEnabled,
        bool cpuTemperatureEnabled,
        bool cpuPowerEnabled,
        bool networkTodayTotalEnabled,
        bool framesPerSecondEnabled,
        bool onePercentLowEnabled,
        bool frameTimeEnabled,
        bool renderLatencyEnabled,
        bool codexAccountLimitsEnabled,
        bool claudeUsageEnabled)
    {
        var metrics = new HashSet<MetricId>();
        if (cpuUsageEnabled) metrics.Add(MetricId.CpuUtilization);
        if (cpuTemperatureEnabled) metrics.Add(MetricId.CpuTemperature);
        if (cpuPowerEnabled) metrics.Add(MetricId.CpuPower);
        if (gpuUsageEnabled) metrics.Add(MetricId.GpuUtilization);
        if (gpuTemperatureEnabled) metrics.Add(MetricId.GpuTemperature);
        if (gpuPowerEnabled) metrics.Add(MetricId.GpuPower);
        if (gpuVramEnabled) metrics.Add(MetricId.GpuVramUsed);
        if (networkDownloadEnabled) metrics.Add(MetricId.NetworkDownload);
        if (networkUploadEnabled) metrics.Add(MetricId.NetworkUpload);
        if (networkTodayTotalEnabled) metrics.Add(MetricId.NetworkTodayTotal);
        if (storageReadEnabled) metrics.Add(MetricId.StorageRead);
        if (storageWriteEnabled) metrics.Add(MetricId.StorageWrite);
        if (framesPerSecondEnabled) metrics.Add(MetricId.FramesPerSecond);
        if (onePercentLowEnabled) metrics.Add(MetricId.OnePercentLow);
        if (frameTimeEnabled) metrics.Add(MetricId.FrameTime);
        if (renderLatencyEnabled) metrics.Add(MetricId.Latency);
        if (codexAccountLimitsEnabled)
        {
            metrics.Add(MetricId.CodexPrimaryRateLimit);
            metrics.Add(MetricId.CodexSecondaryRateLimit);
        }
        if (claudeUsageEnabled)
        {
            metrics.Add(MetricId.ClaudePrimaryRateLimit);
            metrics.Add(MetricId.ClaudeSecondaryRateLimit);
        }
        return metrics;
    }
}
