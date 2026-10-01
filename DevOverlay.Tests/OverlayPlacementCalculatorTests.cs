using System.Windows;
using DevOverlay.Configuration;
using DevOverlay.Platform.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class OverlayPlacementCalculatorTests
{
    [Fact]
    public void TopRightPlacement_UsesMarginAndResolvedWindowSize()
    {
        var success = OverlayPlacementCalculator.TryCalculate(
            new Rect(0, 0, 1920, 1040),
            new Size(500, 40),
            new OverlayPosition("PrimaryDisplay", OverlayAnchor.TopRight, 16, 16),
            out var placement);

        Assert.True(success);
        Assert.Equal(new Point(1404, 16), placement);
    }

    [Fact]
    public void Placement_ClampsInvalidOffsetsInsideWorkArea()
    {
        var success = OverlayPlacementCalculator.TryCalculate(
            new Rect(0, 0, 1000, 800),
            new Size(300, 50),
            new OverlayPosition("PrimaryDisplay", OverlayAnchor.BottomRight, 10_000, 10_000),
            out var placement);

        Assert.True(success);
        Assert.Equal(new Point(0, 0), placement);
    }

    [Fact]
    public void Placement_PreservesNegativeMonitorOrigin()
    {
        var success = OverlayPlacementCalculator.TryCalculate(
            new Rect(-1600, 0, 1600, 900),
            new Size(320, 45),
            new OverlayPosition("Secondary", OverlayAnchor.TopRight, 12, 12),
            out var placement);

        Assert.True(success);
        Assert.Equal(new Point(-332, 12), placement);
    }

    [Fact]
    public void Placement_UsesVisibleOriginWhenWindowIsLargerThanWorkArea()
    {
        var success = OverlayPlacementCalculator.TryCalculate(
            new Rect(100, 50, 400, 300),
            new Size(600, 500),
            new OverlayPosition("PrimaryDisplay", OverlayAnchor.BottomRight, 16, 16),
            out var placement);

        Assert.True(success);
        Assert.Equal(new Point(100, 50), placement);
    }

    [Fact]
    public void Placement_ReturnsFalseUntilWindowHasResolvedSize()
    {
        var success = OverlayPlacementCalculator.TryCalculate(
            new Rect(0, 0, 1000, 800),
            new Size(0, 40),
            OverlayPosition.CreateDefault(),
            out _);

        Assert.False(success);
    }

    [Fact]
    public void CustomPlacement_PreservesValidLocationAcrossWindowSizeChanges()
    {
        var position = OverlayPosition.CreateCustom(120, 80);
        var workArea = new Rect(0, 0, 1000, 800);

        Assert.True(OverlayPlacementCalculator.TryCalculate(workArea, new Size(200, 40), position, out var first));
        Assert.True(OverlayPlacementCalculator.TryCalculate(workArea, new Size(450, 40), position, out var resized));

        Assert.Equal(new Point(120, 80), first);
        Assert.Equal(first, resized);
    }

    [Fact]
    public void CustomPlacement_ClampsOnlyWhenNewSizeNeedsIt()
    {
        var position = OverlayPosition.CreateCustom(850, 700);
        var workArea = new Rect(0, 0, 1000, 800);

        Assert.True(OverlayPlacementCalculator.TryCalculate(workArea, new Size(100, 50), position, out var first));
        Assert.True(OverlayPlacementCalculator.TryCalculate(workArea, new Size(300, 150), position, out var resized));

        Assert.Equal(new Point(850, 700), first);
        Assert.Equal(new Point(700, 650), resized);
    }

    [Fact]
    public void CustomPlacement_RecoversOffscreenSavedPosition()
    {
        var success = OverlayPlacementCalculator.TryCalculate(
            new Rect(-1600, 0, 1600, 900), new Size(320, 45), OverlayPosition.CreateCustom(9_000, 9_000), out var placement);

        Assert.True(success);
        Assert.Equal(new Point(-320, 855), placement);
    }
}
