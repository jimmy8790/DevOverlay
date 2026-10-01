using System.Diagnostics;
using DevOverlay.Metrics.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class PresentedFrameDiagnosticsTests
{
    [Fact]
    public void SameSamplesIndependentlyMatchProductionFormulaAndRetainRealStutters()
    {
        var window = new PresentedFrameWindow();
        var diagnostics = new PresentedFrameDiagnostics();
        var now = (ulong)Stopwatch.GetTimestamp();
        for (ulong index = 200; index > 0; index--)
        {
            var ms = index == 1 ? 1000 : index == 2 ? 20 : 10;
            var accepted = window.Add(now - index, ms, ms, now);
            diagnostics.Observe(now - index, ms, accepted, index == 1, 2);
        }
        var stats = diagnostics.Statistics(now, 15);
        Assert.Equal(200, stats.Count);
        Assert.Equal(2, stats.SlowCount);
        Assert.Equal(10, stats.MinMs);
        Assert.Equal(10, stats.MedianMs);
        Assert.Equal(1000, stats.MaxMs);
        Assert.Equal(510, stats.SlowMeanMs);
        Assert.Equal(1000d / 510, stats.LowFps);
        Assert.Equal(window.Calculate(now), stats.LowFps);
        Assert.Equal(1, stats.Dropped); // classification never removes a positive present interval
    }

    [Fact]
    public void WindowHorizonAloneCanMateriallyChangeLowWithoutChangingFormula()
    {
        var diagnostics = new PresentedFrameDiagnostics();
        var start = (ulong)(Stopwatch.Frequency * 100);
        var now = start + (ulong)(Stopwatch.Frequency * 60);
        for (ulong index = 1; index <= 6000; index++)
            diagnostics.Observe(start + index * (ulong)(Stopwatch.Frequency / 100), index == 1000 ? 400 : 10, true, false, 2);
        foreach (var seconds in new[] { 5, 10, 15, 30 })
            Assert.Equal(100, diagnostics.Statistics(now, seconds).LowFps);
        Assert.Equal(6000, diagnostics.Statistics(now, 60).Count);
        Assert.Equal(1000d / ((59 * 10d + 400) / 60), diagnostics.Statistics(now, 60).LowFps, 8);
        Assert.Equal(diagnostics.Statistics(now, 60), diagnostics.Statistics(now, null));
    }

    [Fact]
    public void DuplicatedQpcAndInvalidIntervalsDoNotChangePopulation()
    {
        var window = new PresentedFrameWindow();
        var diagnostics = new PresentedFrameDiagnostics();
        var now = (ulong)Stopwatch.GetTimestamp();
        foreach (var ms in new[] { 10d, 300, double.NaN, 0 })
            diagnostics.Observe(now, ms, window.Add(now, ms, ms, now), false, 50);
        Assert.Equal(1, diagnostics.Statistics(now, 15).Count);
        Assert.Equal(1, diagnostics.Statistics(now, 15).Generated);
        Assert.Contains("Received=4 Rejected=3", diagnostics.Report(now));
        Assert.Contains("ZeroDisplay=NotSeparatelyExposed", diagnostics.Report(now));
    }
}
