using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class NvidiaGpuMetricValueConverterTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(72, 72)]
    [InlineData(150, 100)]
    public void ToPercentage_ClampsNvmlValue(uint source, double expected)
    {
        Assert.Equal(expected, NvidiaGpuMetricValueConverter.ToPercentage(source));
    }

    [Fact]
    public void MilliwattsToWatts_UsesThousandMillwattsPerWatt()
    {
        Assert.Equal(47.25, NvidiaGpuMetricValueConverter.MilliwattsToWatts(47_250), 2);
    }

    [Fact]
    public void BytesToGigabytes_UsesBinaryGigabytes()
    {
        Assert.Equal(3.5, NvidiaGpuMetricValueConverter.BytesToGigabytes(3_758_096_384), 2);
    }

    [Fact]
    public void UnavailableMetric_HasNoValueAndIsNotReportedAsZero()
    {
        var metric = MetricSnapshot.Unavailable(MetricId.GpuPower, MetricCategory.Gpu, "GPU 전력", "W");

        Assert.False(metric.IsAvailable);
        Assert.Null(metric.Value);
    }

    [Fact]
    public async Task CollectAsync_ReturnsAvailableNvmlMetrics_WhenHardwareTestIsEnabled()
    {
        if (Environment.GetEnvironmentVariable("DEVOVERLAY_RUN_GPU_HARDWARE_TEST") != "1")
        {
            return;
        }

        await using var provider = new NvidiaGpuMetricProvider();
        var metrics = await provider.CollectAsync(CancellationToken.None);

        Assert.Collection(
            metrics.OrderBy(metric => metric.Id),
            metric => AssertAvailable(metric, MetricId.GpuUtilization, 0, 100),
            metric => AssertAvailable(metric, MetricId.GpuTemperature, 1, 150),
            metric => AssertAvailable(metric, MetricId.GpuPower, 0, 1_000),
            metric => AssertAvailable(metric, MetricId.GpuVramUsed, 0, 100));
    }

    private static void AssertAvailable(MetricSnapshot metric, MetricId expectedId, double minimum, double maximum)
    {
        Assert.Equal(expectedId, metric.Id);
        Assert.True(metric.IsAvailable);
        Assert.NotNull(metric.Value);
        Assert.InRange(metric.Value.Value, minimum, maximum);
    }
}
