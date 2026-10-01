using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using DevOverlay.SensorHostProtocol;
using DevOverlay.SensorServiceSetup;
using Microsoft.Win32.SafeHandles;

namespace DevOverlay.Platform.Windows;

internal enum SensorServiceRunState { NotInstalled, Stopped, StartPending, Running, StopPending, Broken, Unknown }
internal readonly record struct SensorServiceStatus(SensorServiceRunState State, uint ProcessId = 0, string? Detail = null);

internal interface ISensorServiceStatusReader
{
    SensorServiceStatus Read();
}

internal sealed class SensorServiceStatusReader(SensorServiceInstallLayout? layout = null) : ISensorServiceStatusReader
{
    private const int ErrorServiceDoesNotExist = 1060;
    private readonly SensorServiceInstallLayout _layout = layout ?? SensorServiceInstallLayout.ForCurrentMachine();

    public SensorServiceStatus Read()
    {
        try { return ReadCore(); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or Win32Exception or System.Security.SecurityException)
        { return new(SensorServiceRunState.Unknown, Detail: exception.Message); }
    }

    private SensorServiceStatus ReadCore()
    {
        using var manager = OpenSCManager(null, null, 0x0001); // SC_MANAGER_CONNECT
        if (manager.IsInvalid) return new(SensorServiceRunState.Unknown, Detail: Error("OpenSCManager"));
        using var service = OpenService(manager, SensorServiceIdentity.ServiceName, 0x0004); // SERVICE_QUERY_STATUS
        if (service.IsInvalid)
            return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                ? new(SensorServiceRunState.NotInstalled) : new(SensorServiceRunState.Unknown, Detail: Error("OpenService"));
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
            return new(SensorServiceRunState.Unknown, Detail: Error("QueryServiceStatusEx"));
        var state = status.CurrentState switch
        {
            1 => SensorServiceRunState.Stopped,
            2 => SensorServiceRunState.StartPending,
            3 => SensorServiceRunState.StopPending,
            4 => SensorServiceRunState.Running,
            _ => SensorServiceRunState.Unknown
        };
        var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{SensorServiceIdentity.ServiceName}");
        using (key)
        {
            var image = key?.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(image)) return new(SensorServiceRunState.Broken, status.ProcessId, "Service executable path is missing.");
            var path = image.Trim();
            if (path.StartsWith('"'))
            {
                var end = path.IndexOf('"', 1);
                path = end > 1 ? path[1..end] : string.Empty;
            }
            if (!File.Exists(Environment.ExpandEnvironmentVariables(path)))
                return new(SensorServiceRunState.Broken, status.ProcessId, "Service executable is missing; choose Repair.");
            if (!_layout.IsInstalledImage(image))
                return new(SensorServiceRunState.Broken, status.ProcessId,
                    "Service runs from an unprotected location; choose Repair to move it to Program Files.");
        }
        return new(state, status.ProcessId);
    }

    private static string Error(string operation) => $"{operation}: Windows error {Marshal.GetLastWin32Error()}.";

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle service, int level,
        out ServiceStatusProcess status, uint bufferSize, out uint bytesNeeded);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
