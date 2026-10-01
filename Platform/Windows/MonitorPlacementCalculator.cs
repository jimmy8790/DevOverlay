using System.Windows;
using DevOverlay.Configuration;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace DevOverlay.Platform.Windows;

/// <summary>Monitor work areas are physical pixels; saved offsets and window sizes are WPF DIPs.</summary>
internal sealed record MonitorWorkArea(string Id, Rect Pixels, double DpiScaleX, double DpiScaleY, bool IsPrimary);
internal readonly record struct MonitorPlacement(MonitorWorkArea Monitor, int LeftPixels, int TopPixels);

internal static class MonitorPlacementCalculator
{
    public static bool TryCalculate(
        IReadOnlyCollection<MonitorWorkArea> monitors,
        Size windowSizeDips,
        OverlayPosition position,
        out MonitorPlacement placement)
    {
        placement = default;
        var monitor = ResolveMonitor(monitors, position.MonitorId);
        if (monitor is null || !IsValidScale(monitor.DpiScaleX) || !IsValidScale(monitor.DpiScaleY)) return false;
        var localWorkArea = new Rect(0, 0,
            monitor.Pixels.Width / monitor.DpiScaleX,
            monitor.Pixels.Height / monitor.DpiScaleY);
        if (!OverlayPlacementCalculator.TryCalculate(localWorkArea, windowSizeDips, position, out var local)) return false;
        placement = new MonitorPlacement(monitor,
            (int)Math.Round(monitor.Pixels.Left + local.X * monitor.DpiScaleX),
            (int)Math.Round(monitor.Pixels.Top + local.Y * monitor.DpiScaleY));
        return true;
    }

    public static MonitorWorkArea? ResolveMonitor(IReadOnlyCollection<MonitorWorkArea> monitors, string? monitorId) =>
        monitors.FirstOrDefault(monitor => string.Equals(monitor.Id, monitorId, StringComparison.OrdinalIgnoreCase)) ??
        monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors.FirstOrDefault();

    public static MonitorWorkArea? FindMonitorForBounds(IReadOnlyCollection<MonitorWorkArea> monitors, Rect windowPixels)
    {
        if (monitors.Count == 0) return null;
        var overlapping = monitors
            .Select(monitor => (Monitor: monitor, Area: Rect.Intersect(monitor.Pixels, windowPixels)))
            .OrderByDescending(item => item.Area.IsEmpty ? 0 : item.Area.Width * item.Area.Height)
            .First();
        if (!overlapping.Area.IsEmpty) return overlapping.Monitor;
        var center = new Point(windowPixels.Left + windowPixels.Width / 2, windowPixels.Top + windowPixels.Height / 2);
        return monitors.OrderBy(monitor => SquaredDistanceToRect(center, monitor.Pixels)).First();
    }

    public static OverlayPosition CaptureCustomPosition(MonitorWorkArea monitor, Rect windowPixels)
    {
        var x = (windowPixels.Left - monitor.Pixels.Left) / monitor.DpiScaleX;
        var y = (windowPixels.Top - monitor.Pixels.Top) / monitor.DpiScaleY;
        return OverlayPosition.CreateCustom(monitor.Id, Math.Max(0, x), Math.Max(0, y));
    }

    private static bool IsValidScale(double value) => double.IsFinite(value) && value > 0;

    private static double SquaredDistanceToRect(Point point, Rect bounds)
    {
        var x = Math.Clamp(point.X, bounds.Left, bounds.Right);
        var y = Math.Clamp(point.Y, bounds.Top, bounds.Bottom);
        return Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2);
    }
}
