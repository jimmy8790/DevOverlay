namespace DevOverlay.Metrics;

/// <summary>Safe, UI-facing device information. Native handles remain inside providers.</summary>
public sealed record DeviceDescriptor(string Id, string DisplayName);

public interface ISelectableDeviceProvider
{
    IReadOnlyCollection<DeviceDescriptor> GetAvailableDevices();
}
