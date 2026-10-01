using System.Runtime.InteropServices;
using System.Windows;
using FormsScreen = System.Windows.Forms.Screen;
using Rect = System.Windows.Rect;

namespace DevOverlay.Platform.Windows;

/// <summary>Uses the per-monitor interface path when Windows exposes one; falls back to the display device name.</summary>
internal sealed class WindowsMonitorProvider
{
    public IReadOnlyCollection<MonitorWorkArea> GetMonitors()
    {
        var result = new List<MonitorWorkArea>();
        foreach (var screen in FormsScreen.AllScreens)
        {
            var bounds = screen.Bounds;
            var center = new NativePoint(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            nint monitor = MonitorFromPoint(center, 2);
            double dpiX = 96, dpiY = 96;
            if (monitor != nint.Zero && GetDpiForMonitor(monitor, 0, out uint x, out uint y) == 0 && x > 0 && y > 0)
            {
                dpiX = x;
                dpiY = y;
            }
            var area = screen.WorkingArea;
            result.Add(new MonitorWorkArea(GetStableId(screen.DeviceName),
                new Rect(area.Left, area.Top, area.Width, area.Height), dpiX / 96, dpiY / 96, screen.Primary));
        }
        return result;
    }

    private static string GetStableId(string displayName)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(displayName, 0, ref device, 1) && !string.IsNullOrWhiteSpace(device.DeviceId)
            ? device.DeviceId : displayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceKey;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string displayName, uint index, ref DisplayDevice device, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
