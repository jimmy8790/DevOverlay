using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevOverlay.Platform.Windows;

public readonly record struct PawnIoDeviceAccess(bool IsAccessible, int Win32Error);

/// <summary>Opens only the PawnIO device handle; it does not load modules or issue IOCTLs.</summary>
public static class PawnIoDeviceAccessProbe
{
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

    public static PawnIoDeviceAccess Probe()
    {
        using var handle = CreateFile(DevicePath, 3, 3, nint.Zero, 3, 0, nint.Zero);
        return handle.IsInvalid
            ? new PawnIoDeviceAccess(false, Marshal.GetLastWin32Error())
            : new PawnIoDeviceAccess(true, 0);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);
}
