using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DevOverlay.Configuration;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace DevOverlay.Platform.Windows;

/// <summary>Converts monitor-relative WPF DIPs to physical pixels at the final Win32 window boundary.</summary>
public sealed class OverlayPositioningService
{
    private readonly WindowsMonitorProvider _monitors = new();

    public bool TryApply(Window window, OverlayPosition position)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero ||
            !MonitorPlacementCalculator.TryCalculate(_monitors.GetMonitors(),
                new Size(window.ActualWidth, window.ActualHeight), position, out var placement)) return false;
        return SetWindowPos(handle, nint.Zero, placement.LeftPixels, placement.TopPixels, 0, 0,
            0x0001 | 0x0004 | 0x0010); // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
    }

    public OverlayPosition CaptureCustomPosition(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        var monitors = _monitors.GetMonitors();
        if (handle == nint.Zero || !GetWindowRect(handle, out var bounds))
            return OverlayPosition.CreateCustom(0, 0);
        var rect = new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        var monitor = MonitorPlacementCalculator.FindMonitorForBounds(monitors, rect);
        if (monitor is null) return OverlayPosition.CreateCustom(0, 0);
        var captured = MonitorPlacementCalculator.CaptureCustomPosition(monitor, rect);
        var windowSize = new Size(rect.Width / monitor.DpiScaleX, rect.Height / monitor.DpiScaleY);
        if (!MonitorPlacementCalculator.TryCalculate(monitors, windowSize, captured, out var clamped)) return captured;
        return MonitorPlacementCalculator.CaptureCustomPosition(monitor,
            new Rect(clamped.LeftPixels, clamped.TopPixels, rect.Width, rect.Height));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);
}
