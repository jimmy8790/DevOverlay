using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DevOverlay.Metrics.Windows;
using Microsoft.Win32.SafeHandles;

namespace DevOverlay.Platform.Windows;

/// <summary>Read-only Battery IOCTLs. Definitions verified against Microsoft WinSDK poclass.h.</summary>
internal sealed class WindowsBatteryNative : ISystemBatteryNative
{
    internal static readonly Guid BatteryInterface = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    // CTL_CODE(FILE_DEVICE_BATTERY=0x29, function, METHOD_BUFFERED=0, FILE_READ_ACCESS=1)
    internal const uint QueryTag = (0x29u << 16) | (1u << 14) | (0x10u << 2);
    internal const uint QueryInformation = (0x29u << 16) | (1u << 14) | (0x11u << 2);
    internal const uint QueryStatus = (0x29u << 16) | (1u << 14) | (0x13u << 2);

    public BatteryPowerStatus? ReadPowerStatus()
    {
        if (!GetSystemPowerStatus(out var status))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemPowerStatus failed.");
        return new(status.AcLineStatus, status.BatteryFlag, status.BatteryLifePercent, status.BatteryLifeTime);
    }

    public IReadOnlyList<ISystemBatteryDevice> EnumerateDevices()
    {
        var guid = BatteryInterface;
        using var info = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        if (info.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Battery device enumeration failed.");
        var devices = new List<ISystemBatteryDevice>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref guid, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break; // ERROR_NO_MORE_ITEMS
                    throw new Win32Exception(error, "Battery interface enumeration failed.");
                }
                SetupDiGetDeviceInterfaceDetail(info, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required < 6 || required > 65536)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Invalid Battery interface detail size.");
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    // cbSize는 x64 8 / x86 Unicode 6. DevicePath의 시작은 두 경우 모두 +4.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(info, ref data, buffer, required, out _, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Battery interface detail failed.");
                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (!string.IsNullOrWhiteSpace(path)) devices.Add(new Device(path));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return devices;
        }
        catch { foreach (var device in devices) device.Dispose(); throw; }
    }

    private sealed class Device : ISystemBatteryDevice
    {
        private readonly SafeFileHandle _handle;
        private uint _tag;
        public int LastError { get; private set; }
        public string Identity { get; }
        public uint Tag => _tag;
        public Device(string path)
        {
            Identity = path;
            // 이 IOCTL들은 READ_ACCESS만 요구한다. 배터리 설정/쓰기 IOCTL은 제공하지 않는다.
            _handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (_handle.IsInvalid) { LastError = Marshal.GetLastWin32Error(); Debug.WriteLine($"Battery open unavailable: Win32 {LastError}"); }
        }

        public bool RefreshInformation(out uint capabilities, out uint fullCapacity)
        {
            capabilities = 0; fullCapacity = 0; _tag = 0;
            if (_handle.IsInvalid) return false;
            if (!Query(_handle, QueryTag, 0u, out uint tag, out var error) || tag == 0)
            { LastError = error; return false; }
            if (!Query(_handle, QueryInformation, new BatteryQuery { Tag = tag }, out BatteryInformation info, out error))
            { LastError = error; return false; }
            _tag = tag; capabilities = info.Capabilities; fullCapacity = info.FullChargedCapacity;
            LastError = 0;
            return true;
        }

        public bool ReadStatus(out BatteryDeviceStatus status)
        {
            status = default;
            if (_handle.IsInvalid || _tag == 0) return false;
            if (!Query(_handle, QueryStatus, new BatteryWaitStatus { Tag = _tag }, out BatteryStatus raw, out var error))
            { LastError = error; return false; }
            LastError = 0;
            status = new(raw.PowerState, raw.Capacity, raw.Rate);
            return true;
        }
        public void Dispose() => _handle.Dispose();
    }

    private static bool Query<TInput, TOutput>(SafeFileHandle handle, uint code, TInput input, out TOutput output, out int error)
        where TInput : struct where TOutput : struct
    {
        output = default; error = 0;
        var inputSize = Marshal.SizeOf<TInput>(); var outputSize = Marshal.SizeOf<TOutput>();
        var inBuffer = Marshal.AllocHGlobal(inputSize);
        var outBuffer = Marshal.AllocHGlobal(outputSize);
        try
        {
            Marshal.StructureToPtr(input, inBuffer, false);
            if (!DeviceIoControl(handle, code, inBuffer, (uint)inputSize, outBuffer, (uint)outputSize, out var returned, IntPtr.Zero))
            {
                error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"Battery query 0x{code:X} unavailable: Win32 {error}");
                return false;
            }
            if (returned < outputSize) { error = 13; return false; } // ERROR_INVALID_DATA
            output = Marshal.PtrToStructure<TOutput>(outBuffer);
            return true;
        }
        finally { Marshal.FreeHGlobal(inBuffer); Marshal.FreeHGlobal(outBuffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology, Reserved1, Reserved2, Reserved3;
        public byte Chemistry1, Chemistry2, Chemistry3, Chemistry4;
        public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryQuery { public uint Tag, InformationLevel, AtRate; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryWaitStatus { public uint Tag, Timeout, PowerState, LowCapacity, HighCapacity; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryStatus { public uint PowerState, Capacity, Voltage; public int Rate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData { public uint Size; public Guid InterfaceClassGuid; public uint Flags; public UIntPtr Reserved; }
    private sealed class DeviceInfoHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static extern DeviceInfoHandle SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(DeviceInfoHandle info, IntPtr device, ref Guid guid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(DeviceInfoHandle info, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr info);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
