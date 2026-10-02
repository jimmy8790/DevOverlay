using System.Diagnostics;
using DevOverlay.Metrics.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class PresentedFrameWindowTests
{
    [Fact]
    public void StableRawFramesProduceOneHundredFps()
    {
        var window = new PresentedFrameWindow();
        var now = (ulong)Stopwatch.GetTimestamp();
        for (ulong index = 1000; index > 0; index--) window.Add(now - index, 10, 10, now);
        Assert.Equal(100, window.Calculate(now));
    }

    [Fact]
    public void SlowestOnePercentIsAveragedRatherThanUsingPercentileBoundary()
    {
        var window = new PresentedFrameWindow();
        var now = (ulong)Stopwatch.GetTimestamp();
        for (ulong index = 200; index > 2; index--) window.Add(now - index, 10, 10, now);
        window.Add(now - 2, 20, 20, now);
        window.Add(now - 1, 40, 40, now);
        Assert.Equal(1000d / 30, window.Calculate(now)); // ceil(200 * .01)=2: (20 + 40)/2
        Assert.NotEqual(1000d / 20, window.Calculate(now)); // percentile boundary is not the requested average
    }

    [Fact]
    public void InvalidOrReplayedFramesCannotAdvanceSourceAndOldFramesAreEvicted()
    {
        var window = new PresentedFrameWindow();
        var now = (ulong)Stopwatch.GetTimestamp();
        window.Add(now - 10, 20, 20, now);
        window.Add(now - 10, 200, 200, now);
        window.Add(now - 9, double.NaN, double.NaN, now);
        window.Add(now - 8, double.PositiveInfinity, double.PositiveInfinity, now);
        window.Add(now - 7, 0, 0, now);
        window.Add(now - 6, -1, -1, now);
        Assert.Equal(now - 10, window.LastQpc);
        Assert.Equal(50, window.Calculate(now));
        Assert.Null(window.Calculate(now + (ulong)(Stopwatch.Frequency * 16)));
    }

    [Fact]
    public void OnePercentLowUsesDisplayIntervalsWhilePresentJitterIsAbsorbed()
    {
        // Door Kickers 2 pattern: a late Present followed by an early one, both shown on the steady 144 Hz cadence.
        var window = new PresentedFrameWindow();
        var now = (ulong)Stopwatch.GetTimestamp();
        for (ulong index = 400; index > 0; index--)
        {
            var present = index % 100 == 0 ? 14.0 : index % 100 == 99 ? 1.6 : 6.94;
            Assert.True(window.Add(now - index, present, 6.94, now));
        }
        Assert.Equal(1000 / 6.94, window.Calculate(now)!.Value, 9);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void UndisplayedPresentAdvancesSourceButAddsNoOnePercentSample(double display)
    {
        var window = new PresentedFrameWindow();
        var now = (ulong)Stopwatch.GetTimestamp();
        Assert.Equal(FrameAdmission.Accepted, window.Admit(now - 3, 6.94, display, now));
        Assert.Equal(now - 3, window.LastQpc); // freshness still follows the Present stream
        Assert.Null(window.Calculate(now)); // no displayed sample: unavailable, not a fake value
        window.Add(now - 2, 6.94, 7, now);
        window.Add(now - 1, 20, display, now);
        Assert.Equal(now - 1, window.LastQpc);
        Assert.Equal(1000d / 7, window.Calculate(now)!.Value, 9);
    }

    [Fact]
    public void PresentAdmissionIsUnchangedByTheDisplayInterval()
    {
        var window = new PresentedFrameWindow();
        var now = (ulong)Stopwatch.GetTimestamp();
        Assert.Equal(FrameAdmission.InvalidInterval, window.Admit(now - 5, double.NaN, 7, now));
        Assert.Equal(FrameAdmission.Accepted, window.Admit(now - 4, 7, 7, now));
        Assert.Equal(FrameAdmission.DuplicateQpc, window.Admit(now - 4, 7, 50, now)); // replayed row: no sample
        Assert.Equal(1000d / 7, window.Calculate(now)!.Value, 9);
    }

    [Fact]
    public void WindowCoversTheLastThreeSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), PresentedFrameWindow.OnePercentLowWindow);
    }

    [Fact]
    public void HitchLeavesTheWindowExactlyThreeSecondsAfterItsPresent()
    {
        // 144 FPS for 3 s with one 1000 ms display interval presented at hitchQpc.
        var window = new PresentedFrameWindow();
        var second = (ulong)Stopwatch.Frequency;
        var frame = second / 144;
        var start = (ulong)Stopwatch.GetTimestamp() - 10 * second;
        var hitchQpc = start + 144 * frame;
        for (ulong index = 0; index < 3 * 144; index++)
        {
            var qpc = start + index * frame;
            Assert.True(window.Add(qpc, 6.94, qpc == hitchQpc ? 1000 : 6.94, start + 3 * second));
        }
        Assert.True(window.Calculate(hitchQpc + 3 * second) < 20); // age exactly 3 s: still inside
        Assert.Equal(1000 / 6.94, window.Calculate(hitchQpc + 3 * second + 1)!.Value, 9);
    }

    [Fact]
    public void HitchOlderThanTheWindowNoLongerLowersOnePercentLow()
    {
        var window = new PresentedFrameWindow();
        var second = (ulong)Stopwatch.Frequency;
        var now = (ulong)Stopwatch.GetTimestamp();
        window.Add(now - 10 * second, 2000, 2000, now);
        for (ulong index = 400; index > 0; index--) window.Add(now - index * second / 144, 6.94, 6.94, now);
        Assert.Equal(1000 / 6.94, window.Calculate(now)!.Value, 9);
    }

    [Fact]
    public void ThirtyFpsUsesTheSingleSlowestFrameOfTheWindow()
    {
        // 3 s at 30 FPS: N = 90, ceil(0.9) = 1 sample, so the value is the slowest displayed frame itself.
        var window = new PresentedFrameWindow();
        var second = (ulong)Stopwatch.Frequency;
        var now = (ulong)Stopwatch.GetTimestamp();
        for (ulong index = 90; index > 1; index--) window.Add(now - index * second / 30, 33.3, 33.3, now);
        window.Add(now - second / 30, 50, 50, now);
        Assert.Equal(20, window.Calculate(now)!.Value, 9);
    }

    [Fact]
    public void SubsetRoundingUsesCeiling()
    {
        var window = new PresentedFrameWindow();
        var now = (ulong)Stopwatch.GetTimestamp();
        for (ulong index = 101; index > 2; index--) window.Add(now - index, 10, 10, now);
        window.Add(now - 2, 20, 20, now);
        window.Add(now - 1, 40, 40, now);
        Assert.Equal(1000d / 30, window.Calculate(now));
    }
}
