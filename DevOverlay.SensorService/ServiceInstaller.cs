using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using DevOverlay.SensorHostProtocol;
using DevOverlay.SensorServiceSetup;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DevOverlay.SensorService;

/// <summary>Invoked only by an explicit elevated install/repair/uninstall action, never by telemetry IPC.</summary>
internal static class ServiceInstaller
{
    public static void Execute(string action)
    {
        var installer = new SensorServiceInstaller(SensorServiceInstallLayout.ForCurrentMachine(),
            new WindowsServiceControlManager(), new WindowsServiceDirectorySecurity());
        // The running executable's folder is the package payload; it is copied, never registered.
        var source = AppContext.BaseDirectory;
        switch (action)
        {
            case "--install": installer.Install(source); break;
            case "--repair": installer.Repair(source); break;
            case "--uninstall": installer.Uninstall(); break;
            default: throw new ArgumentException("Unknown service action.", nameof(action));
        }
    }
}

internal sealed class WindowsServiceControlManager : IServiceControlManager
{
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ServiceErrorNormal = 1;
    private const uint ServiceDisabled = 4;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceControlStop = 1;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(15);

    public ServiceRegistration? Query()
    {
        using var manager = OpenManager();
        using var service = OpenService(manager, SensorServiceIdentity.ServiceName, ServiceAllAccess);
        if (service.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist) return null;
            ThrowLastError("OpenService");
        }
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{SensorServiceIdentity.ServiceName}");
        return new ServiceRegistration(key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
    }

    public void Stop()
    {
        using var manager = OpenManager();
        using var service = OpenExisting(manager);
        using var controller = new ServiceController(SensorServiceIdentity.ServiceName);
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Stopped) return;
        if (controller.Status != ServiceControllerStatus.StopPending &&
            !ControlService(service, ServiceControlStop, out _) && Marshal.GetLastWin32Error() != ErrorServiceNotActive)
            ThrowLastError("ControlService(STOP)");
        controller.WaitForStatus(ServiceControllerStatus.Stopped, StateTimeout);
    }

    public void Create(string quotedImagePath)
    {
        using var manager = OpenManager();
        using var created = CreateService(manager, SensorServiceIdentity.ServiceName, SensorServiceIdentity.DisplayName,
            ServiceAllAccess, ServiceWin32OwnProcess, SensorServiceIdentity.AutomaticStartType, ServiceErrorNormal,
            quotedImagePath, null, nint.Zero, null, null, null);
        if (created.IsInvalid) ThrowLastError("CreateService"); // null account selects LocalSystem, with no stored password.
        ConfigureDescription(created);
        EnsureEventSource();
    }

    public void Reconfigure(string quotedImagePath)
    {
        using var manager = OpenManager();
        using var service = OpenExisting(manager);
        if (!ChangeServiceConfig(service, ServiceNoChange, SensorServiceIdentity.AutomaticStartType, ServiceErrorNormal,
                quotedImagePath, null, nint.Zero, null, SensorServiceIdentity.ServiceAccount, null, SensorServiceIdentity.DisplayName))
            ThrowLastError("ChangeServiceConfig");
        ConfigureDescription(service);
        EnsureEventSource();
    }

    public void Disable()
    {
        using var manager = OpenManager();
        using var service = OpenExisting(manager);
        if (!ChangeServiceConfig(service, ServiceNoChange, ServiceDisabled, ServiceNoChange,
                null, null, nint.Zero, null, null, null, null))
            ThrowLastError("ChangeServiceConfig(disable)");
    }

    public void Start()
    {
        using var manager = OpenManager();
        using var service = OpenExisting(manager);
        if (!StartService(service, 0, nint.Zero) && Marshal.GetLastWin32Error() != ErrorServiceAlreadyRunning)
            ThrowLastError("StartService");
        using var controller = new ServiceController(SensorServiceIdentity.ServiceName);
        controller.WaitForStatus(ServiceControllerStatus.Running, StateTimeout);
    }

    public void Delete()
    {
        using var manager = OpenManager();
        using var service = OpenExisting(manager);
        if (!DeleteService(service)) ThrowLastError("DeleteService");
        // PawnIO is intentionally neither stopped nor removed.
    }

    private static SafeServiceHandle OpenManager()
    {
        var manager = OpenSCManager(null, null, ScManagerAllAccess);
        if (manager.IsInvalid) ThrowLastError("OpenSCManager");
        return manager;
    }

    private static SafeServiceHandle OpenExisting(SafeServiceHandle manager)
    {
        var service = OpenService(manager, SensorServiceIdentity.ServiceName, ServiceAllAccess);
        if (service.IsInvalid) ThrowLastError("OpenService");
        return service;
    }

    private static void ConfigureDescription(SafeServiceHandle service)
    {
        var description = new ServiceDescription { Text = SensorServiceIdentity.Description };
        if (!ChangeServiceConfig2(service, 1, ref description)) ThrowLastError("ChangeServiceConfig2(description)");
    }

    private static void EnsureEventSource()
    {
        if (!EventLog.SourceExists(SensorServiceIdentity.ServiceName))
            EventLog.CreateEventSource(SensorServiceIdentity.ServiceName, "Application");
    }

    private static void ThrowLastError(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceDescription { [MarshalAs(UnmanagedType.LPWStr)] public string Text; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
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
    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle CreateService(SafeServiceHandle manager, string name, string displayName,
        uint access, uint serviceType, uint startType, uint errorControl, string binaryPath,
        string? loadOrderGroup, nint tagId, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(SafeServiceHandle service, uint serviceType, uint startType,
        uint errorControl, string? binaryPath, string? loadOrderGroup, nint tagId, string? dependencies,
        string? account, string? password, string? displayName);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(SafeServiceHandle service, uint infoLevel, ref ServiceDescription description);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(SafeServiceHandle service, uint argumentCount, nint arguments);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(SafeServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(SafeServiceHandle service);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint service);
}
