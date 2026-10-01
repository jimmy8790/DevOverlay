namespace DevOverlay.Configuration;

/// <summary>Configuration for a provider that can either choose automatically or use one stable device ID.</summary>
public abstract record DeviceSelection
{
    public static DeviceSelection Auto { get; } = new AutomaticDeviceSelection();
    public static DeviceSelection SystemDrive { get; } = new SystemDriveDeviceSelection();

    public static SpecificDeviceSelection Specific(string deviceId) => new(deviceId);
}

public sealed record AutomaticDeviceSelection : DeviceSelection;
public sealed record SystemDriveDeviceSelection : DeviceSelection;

public sealed record SpecificDeviceSelection : DeviceSelection
{
    public SpecificDeviceSelection(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A specific device selection requires a stable device ID.", nameof(deviceId));
        }

        DeviceId = deviceId;
    }

    public string DeviceId { get; }
}
