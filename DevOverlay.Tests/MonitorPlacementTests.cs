using System.Windows;
using DevOverlay.Configuration;
using DevOverlay.Platform.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class MonitorPlacementTests
{
    private static readonly MonitorWorkArea Primary = new("MONITOR\\primary", new Rect(0, 0, 1600, 900), 1, 1, true);
    private static readonly MonitorWorkArea Secondary = new("MONITOR\\secondary", new Rect(-1920, -100, 1920, 1080), 1.25, 1.25, false);

    [Fact]
    public void CustomPositionUsesSavedMonitorAndConvertsDipsToPixels()
    {
        var saved = OverlayPosition.CreateCustom(Secondary.Id, 80, 40);
        Assert.True(MonitorPlacementCalculator.TryCalculate([Primary, Secondary], new Size(200, 40), saved, out var placed));
        Assert.Equal(Secondary.Id, placed.Monitor.Id);
        Assert.Equal((-1820, -50), (placed.LeftPixels, placed.TopPixels));
        Assert.Equal(saved, MonitorPlacementCalculator.CaptureCustomPosition(Secondary,
            new Rect(placed.LeftPixels, placed.TopPixels, 250, 50)));
    }

    [Fact]
    public void RemovedMonitorFallsBackToPrimaryAndClamps()
    {
        var saved = OverlayPosition.CreateCustom(Secondary.Id, 5000, 5000);
        Assert.True(MonitorPlacementCalculator.TryCalculate([Primary], new Size(200, 40), saved, out var placed));
        Assert.Equal(Primary.Id, placed.Monitor.Id);
        Assert.Equal((1400, 860), (placed.LeftPixels, placed.TopPixels));
    }

    [Fact]
    public void WindowSizeChangeClampsOnlyWhenNeeded()
    {
        var saved = OverlayPosition.CreateCustom(1200, 100);
        Assert.True(MonitorPlacementCalculator.TryCalculate([Primary], new Size(200, 40), saved, out var first));
        Assert.True(MonitorPlacementCalculator.TryCalculate([Primary], new Size(500, 40), saved, out var wider));
        Assert.Equal(1200, first.LeftPixels);
        Assert.Equal(1100, wider.LeftPixels);
    }
}
