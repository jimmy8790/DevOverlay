using System.IO;

namespace DevOverlay.SensorHostProtocol;

public static class SensorServiceIdentity
{
    public const string ServiceName = "DevOverlaySensorService";
    public const string DisplayName = "DevOverlay CPU Sensor Service";
    public const string Description = "Provides privileged CPU Package temperature and power telemetry to DevOverlay.";
    public const string ServiceAccount = "LocalSystem";
    public const uint AutomaticStartType = 2;
    public const string PipeName = "dev-overlay-cpu-service-v1";
    public const string PipeSecurityDescriptor = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00100083;;;BU)";
    public const uint PipeRejectRemoteClients = 0x00000008;
    public const int PipeClientRights = 0x00100083; // data, read attributes, SYNCHRONIZE; no FILE_CREATE_PIPE_INSTANCE (0x4)

    public static string QuoteExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('"') ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A valid executable path is required.", nameof(path));
        return $"\"{Path.GetFullPath(path)}\"";
    }
}
