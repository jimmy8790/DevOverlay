using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using DevOverlay.Metrics;

namespace DevOverlay.Configuration;

/// <summary>Small, resilient per-user JSON store for configuration only; telemetry is never persisted.</summary>
public sealed class OverlaySettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public OverlaySettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevOverlay",
            "settings.json");
    }

    public string FilePath { get; }

    public OverlaySettings Load()
    {
        var defaults = OverlaySettings.CreateDefault();
        try
        {
            if (!File.Exists(FilePath))
            {
                return defaults;
            }

            var json = File.ReadAllText(FilePath);
            using var document = JsonDocument.Parse(json);
            var hasStorageDeviceId = document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.EnumerateObject().Any(property =>
                    string.Equals(property.Name, nameof(PersistedOverlaySettings.StorageDeviceId), StringComparison.OrdinalIgnoreCase));
            var persisted = JsonSerializer.Deserialize<PersistedOverlaySettings>(json, SerializerOptions);
            return persisted is null ? defaults : ToSettings(persisted, defaults, hasStorageDeviceId);
        }
        catch (IOException)
        {
            return defaults;
        }
        catch (UnauthorizedAccessException)
        {
            return defaults;
        }
        catch (JsonException)
        {
            return defaults;
        }
    }

    public void Save(OverlaySettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The settings path must include a directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var persisted = FromSettings(settings);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(persisted, SerializerOptions));
            File.Move(temporaryPath, FilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static OverlaySettings ToSettings(PersistedOverlaySettings persisted, OverlaySettings defaults, bool hasStorageDeviceId) =>
        OverlaySettings.CreateForCurrentFeatures(
            persisted.CpuGroupEnabled ?? defaults.EnabledGroups.Contains(MetricCategory.Cpu),
            persisted.GpuGroupEnabled ?? defaults.EnabledGroups.Contains(MetricCategory.Gpu),
            persisted.NetworkGroupEnabled ?? defaults.EnabledGroups.Contains(MetricCategory.Network),
            persisted.StorageGroupEnabled ?? defaults.EnabledGroups.Contains(MetricCategory.Storage),
            ToSelection(persisted.GpuDeviceId),
            ToSelection(persisted.NetworkDeviceId),
            hasStorageDeviceId ? ToStorageSelection(persisted.StorageDeviceId) : DeviceSelection.SystemDrive,
            persisted.IsOverlayVisible ?? defaults.IsVisible,
            persisted.CpuUsageEnabled ?? defaults.EnabledMetrics.Contains(MetricId.CpuUtilization),
            persisted.GpuUsageEnabled ?? defaults.EnabledMetrics.Contains(MetricId.GpuUtilization),
            persisted.GpuTemperatureEnabled ?? defaults.EnabledMetrics.Contains(MetricId.GpuTemperature),
            persisted.GpuPowerEnabled ?? defaults.EnabledMetrics.Contains(MetricId.GpuPower),
            persisted.GpuVramEnabled ?? defaults.EnabledMetrics.Contains(MetricId.GpuVramUsed),
            persisted.NetworkDownloadEnabled ?? defaults.EnabledMetrics.Contains(MetricId.NetworkDownload),
            persisted.NetworkUploadEnabled ?? defaults.EnabledMetrics.Contains(MetricId.NetworkUpload),
            persisted.StorageReadEnabled ?? defaults.EnabledMetrics.Contains(MetricId.StorageRead),
            persisted.StorageWriteEnabled ?? defaults.EnabledMetrics.Contains(MetricId.StorageWrite),
            persisted.CpuTemperatureEnabled ?? defaults.EnabledMetrics.Contains(MetricId.CpuTemperature),
            persisted.CpuPowerEnabled ?? defaults.EnabledMetrics.Contains(MetricId.CpuPower),
            persisted.NetworkTodayTotalEnabled ?? defaults.EnabledMetrics.Contains(MetricId.NetworkTodayTotal),
            persisted.FpsGroupEnabled ?? defaults.EnabledGroups.Contains(MetricCategory.Frame),
            persisted.FramesPerSecondEnabled ?? defaults.EnabledMetrics.Contains(MetricId.FramesPerSecond),
            persisted.OnePercentLowEnabled ?? defaults.EnabledMetrics.Contains(MetricId.OnePercentLow),
            persisted.FrameTimeEnabled ?? defaults.EnabledMetrics.Contains(MetricId.FrameTime),
            ToSelection(persisted.FpsExecutable),
            persisted.LatencyGroupEnabled ?? defaults.EnabledGroups.Contains(MetricCategory.Latency),
            persisted.RenderLatencyEnabled ?? defaults.EnabledMetrics.Contains(MetricId.Latency),
            persisted.AiUsageGroupEnabled ?? defaults.EnabledGroups.Contains(MetricCategory.AiUsage),
            persisted.CodexAccountLimitsEnabled ?? defaults.EnabledMetrics.Contains(MetricId.CodexPrimaryRateLimit),
            persisted.ClaudeUsageEnabled ?? defaults.EnabledMetrics.Contains(MetricId.ClaudePrimaryRateLimit),
            persisted.BatteryGroupEnabled ?? defaults.EnabledGroups.Contains(MetricCategory.Battery)) with
        {
            RefreshIntervalMs = OverlaySettings.NormalizeRefreshIntervalMs(persisted.RefreshIntervalMs ?? defaults.RefreshIntervalMs),
            Position = ToPosition(persisted, defaults.Position),
            GroupOrder = ToGroupOrder(persisted.GroupOrder),
            Appearance = ToAppearance(persisted.Appearance, defaults.Appearance),
            Hotkey = new OverlayHotkey(
                persisted.HotkeyModifiers is { } modifiers ? (OverlayHotkeyModifiers)modifiers : defaults.Hotkey.Modifiers,
                persisted.HotkeyVirtualKey is { } virtualKey ? virtualKey : defaults.Hotkey.VirtualKey).Normalize(),
            PopupBehavior = defaults.PopupBehavior,
            PeripheralBatteriesEnabled = persisted.PeripheralBatteriesEnabled ?? true,
            PeripheralDevices = (persisted.PeripheralDevices ?? []).Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Identity))
                .GroupBy(item => item.Identity).Select(group => group.First()).Take(256)
                .Select(item => item with { Type = Enum.IsDefined(item.Type) ? item.Type : DevOverlay.Peripherals.PeripheralType.Other,
                    CustomName = DevOverlay.Peripherals.PeripheralPreference.NormalizeName(item.CustomName),
                    DefaultLabel = DevOverlay.Peripherals.PeripheralPreference.NormalizeName(item.DefaultLabel) is { Length: > 0 } label ? label : "DEV" }).ToArray()
        };

    private static PersistedOverlaySettings FromSettings(OverlaySettings settings) => new()
    {
        PeripheralBatteriesEnabled = settings.PeripheralBatteriesEnabled,
        PeripheralDevices = settings.PeripheralDevices.ToArray(),
        RefreshIntervalMs = OverlaySettings.NormalizeRefreshIntervalMs(settings.RefreshIntervalMs),
        CpuGroupEnabled = settings.EnabledGroups.Contains(MetricCategory.Cpu),
        GpuGroupEnabled = settings.EnabledGroups.Contains(MetricCategory.Gpu),
        NetworkGroupEnabled = settings.EnabledGroups.Contains(MetricCategory.Network),
        StorageGroupEnabled = settings.EnabledGroups.Contains(MetricCategory.Storage),
        FpsGroupEnabled = settings.EnabledGroups.Contains(MetricCategory.Frame),
        LatencyGroupEnabled = settings.EnabledGroups.Contains(MetricCategory.Latency),
        AiUsageGroupEnabled = settings.EnabledGroups.Contains(MetricCategory.AiUsage),
        BatteryGroupEnabled = settings.EnabledGroups.Contains(MetricCategory.Battery),
        GpuDeviceId = ToDeviceId(settings.GpuDeviceSelection),
        NetworkDeviceId = ToDeviceId(settings.NetworkDeviceSelection),
        StorageDeviceId = settings.StorageDeviceSelection is SystemDriveDeviceSelection
            ? "dev-overlay:system-drive" : ToDeviceId(settings.StorageDeviceSelection),
        FpsExecutable = ToDeviceId(settings.FpsTargetSelection),
        IsOverlayVisible = settings.IsVisible,
        HotkeyModifiers = (int)settings.Hotkey.Normalize().Modifiers,
        HotkeyVirtualKey = settings.Hotkey.Normalize().VirtualKey,
        PositionMode = settings.Position.Mode,
        PositionMonitorId = settings.Position.MonitorId,
        PositionAnchor = settings.Position.Anchor,
        PositionOffsetX = settings.Position.OffsetX,
        PositionOffsetY = settings.Position.OffsetY,
        GroupOrder = OverlaySettings.NormalizeGroupOrder(settings.GroupOrder).Select(category => category.ToString()).ToArray(),
        Appearance = new PersistedAppearanceSettings
        {
            BackgroundColor = settings.Appearance.BackgroundColor,
            BackgroundOpacity = settings.Appearance.BackgroundOpacity,
            ValueTextColor = settings.Appearance.ValueTextColor,
            LabelTextColor = settings.Appearance.LabelTextColor,
            Spacing = settings.Appearance.Spacing
        },
        CpuUsageEnabled = settings.EnabledMetrics.Contains(MetricId.CpuUtilization),
        CpuTemperatureEnabled = settings.EnabledMetrics.Contains(MetricId.CpuTemperature),
        CpuPowerEnabled = settings.EnabledMetrics.Contains(MetricId.CpuPower),
        GpuUsageEnabled = settings.EnabledMetrics.Contains(MetricId.GpuUtilization),
        GpuTemperatureEnabled = settings.EnabledMetrics.Contains(MetricId.GpuTemperature),
        GpuPowerEnabled = settings.EnabledMetrics.Contains(MetricId.GpuPower),
        GpuVramEnabled = settings.EnabledMetrics.Contains(MetricId.GpuVramUsed),
        NetworkDownloadEnabled = settings.EnabledMetrics.Contains(MetricId.NetworkDownload),
        NetworkUploadEnabled = settings.EnabledMetrics.Contains(MetricId.NetworkUpload),
        NetworkTodayTotalEnabled = settings.EnabledMetrics.Contains(MetricId.NetworkTodayTotal),
        StorageReadEnabled = settings.EnabledMetrics.Contains(MetricId.StorageRead),
        StorageWriteEnabled = settings.EnabledMetrics.Contains(MetricId.StorageWrite),
        FramesPerSecondEnabled = settings.EnabledMetrics.Contains(MetricId.FramesPerSecond),
        OnePercentLowEnabled = settings.EnabledMetrics.Contains(MetricId.OnePercentLow),
        FrameTimeEnabled = settings.EnabledMetrics.Contains(MetricId.FrameTime),
        RenderLatencyEnabled = settings.EnabledMetrics.Contains(MetricId.Latency),
        CodexAccountLimitsEnabled = settings.EnabledMetrics.Contains(MetricId.CodexPrimaryRateLimit),
        ClaudeUsageEnabled = settings.EnabledMetrics.Contains(MetricId.ClaudePrimaryRateLimit)
    };

    private static DeviceSelection ToSelection(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) ? DeviceSelection.Auto : DeviceSelection.Specific(deviceId);

    private static DeviceSelection ToStorageSelection(string? deviceId) =>
        string.Equals(deviceId, "dev-overlay:system-drive", StringComparison.OrdinalIgnoreCase)
            ? DeviceSelection.SystemDrive : ToSelection(deviceId);

    private static string? ToDeviceId(DeviceSelection selection) =>
        selection is SpecificDeviceSelection specific ? specific.DeviceId : null;

    private static OverlayPosition ToPosition(PersistedOverlaySettings persisted, OverlayPosition defaults)
    {
        if (persisted.PositionMode is not OverlayPositionMode.AutoTopRight and not OverlayPositionMode.Custom)
        {
            return defaults;
        }

        var offsetX = persisted.PositionOffsetX;
        var offsetY = persisted.PositionOffsetY;
        if (!offsetX.HasValue || !offsetY.HasValue || !double.IsFinite(offsetX.Value) || !double.IsFinite(offsetY.Value))
        {
            return defaults;
        }

        var anchor = persisted.PositionAnchor is OverlayAnchor.TopLeft or OverlayAnchor.TopRight or OverlayAnchor.BottomLeft or OverlayAnchor.BottomRight
            ? persisted.PositionAnchor.Value
            : defaults.Anchor;
        var monitorId = string.IsNullOrWhiteSpace(persisted.PositionMonitorId) ? defaults.MonitorId : persisted.PositionMonitorId;
        return new OverlayPosition(monitorId, anchor, offsetX.Value, offsetY.Value, persisted.PositionMode.Value);
    }

    private static IReadOnlyList<MetricCategory> ToGroupOrder(string[]? persisted)
    {
        var categories = (persisted ?? []).Where(name => Enum.TryParse<MetricCategory>(name, true, out var parsed) &&
            Enum.IsDefined(parsed)).Select(name => Enum.Parse<MetricCategory>(name, true));
        return OverlaySettings.NormalizeGroupOrder(categories);
    }

    private static OverlayAppearance ToAppearance(PersistedAppearanceSettings? persisted, OverlayAppearance defaults) =>
        persisted is null
            ? defaults
            : new OverlayAppearance(
                persisted.BackgroundColor ?? defaults.BackgroundColor,
                persisted.BackgroundOpacity ?? defaults.BackgroundOpacity,
                persisted.ValueTextColor ?? defaults.ValueTextColor,
                persisted.LabelTextColor ?? defaults.LabelTextColor,
                persisted.Spacing ?? defaults.Spacing).Normalize();

    private sealed class PersistedOverlaySettings
    {
        public bool? CpuGroupEnabled { get; init; }
        public bool? GpuGroupEnabled { get; init; }
        public bool? NetworkGroupEnabled { get; init; }
        public bool? StorageGroupEnabled { get; init; }
        public bool? FpsGroupEnabled { get; init; }
        public bool? LatencyGroupEnabled { get; init; }
        public bool? AiUsageGroupEnabled { get; init; }
        public bool? BatteryGroupEnabled { get; init; }
        public bool? PeripheralBatteriesEnabled { get; init; }
        public DevOverlay.Peripherals.PeripheralPreference[]? PeripheralDevices { get; init; }
        public string? GpuDeviceId { get; init; }
        public string? NetworkDeviceId { get; init; }
        public string? StorageDeviceId { get; init; }
        public string? FpsExecutable { get; init; }
        public bool? IsOverlayVisible { get; init; }
        public int? HotkeyModifiers { get; init; }
        public uint? HotkeyVirtualKey { get; init; }
        public int? RefreshIntervalMs { get; init; }
        public OverlayPositionMode? PositionMode { get; init; }
        public string? PositionMonitorId { get; init; }
        public OverlayAnchor? PositionAnchor { get; init; }
        public double? PositionOffsetX { get; init; }
        public double? PositionOffsetY { get; init; }
        public string[]? GroupOrder { get; init; }
        public PersistedAppearanceSettings? Appearance { get; init; }
        public bool? CpuUsageEnabled { get; init; }
        public bool? CpuTemperatureEnabled { get; init; }
        public bool? CpuPowerEnabled { get; init; }
        public bool? GpuUsageEnabled { get; init; }
        public bool? GpuTemperatureEnabled { get; init; }
        public bool? GpuPowerEnabled { get; init; }
        public bool? GpuVramEnabled { get; init; }
        public bool? NetworkDownloadEnabled { get; init; }
        public bool? NetworkUploadEnabled { get; init; }
        public bool? NetworkTodayTotalEnabled { get; init; }
        public bool? StorageReadEnabled { get; init; }
        public bool? StorageWriteEnabled { get; init; }
        public bool? FramesPerSecondEnabled { get; init; }
        public bool? OnePercentLowEnabled { get; init; }
        public bool? FrameTimeEnabled { get; init; }
        public bool? RenderLatencyEnabled { get; init; }
        public bool? CodexAccountLimitsEnabled { get; init; }
        public bool? ClaudeUsageEnabled { get; init; }
    }


    private sealed class PersistedAppearanceSettings
    {
        public string? BackgroundColor { get; init; }
        public double? BackgroundOpacity { get; init; }
        public string? ValueTextColor { get; init; }
        public string? LabelTextColor { get; init; }
        // Kept only so old development settings deserialize safely. It is intentionally ignored.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? UiScale { get; init; }
        public double? Spacing { get; init; }
    }
}
