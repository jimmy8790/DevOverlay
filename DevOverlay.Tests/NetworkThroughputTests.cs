using DevOverlay.Metrics.Windows;
using DevOverlay.Metrics;
using System.Diagnostics;
using Xunit;

namespace DevOverlay.Tests;

public sealed class NetworkThroughputTests
{
    [Fact]
    public void TryCalculate_ReturnsFalseForFirstSample()
    {
        var current = Sample("wifi", 1_000, 500, 10_000);

        Assert.False(NetworkThroughputCalculator.TryCalculate(null, current, out _));
    }

    [Fact]
    public void TryCalculate_UsesActualElapsedTimeForBothDirections()
    {
        var previous = Sample("wifi", 1_000, 500, 10_000);
        var current = Sample("wifi", 3_500, 1_100, 10_000 + (2 * Stopwatch.Frequency));

        Assert.True(NetworkThroughputCalculator.TryCalculate(previous, current, out var throughput));
        Assert.Equal(1_250, throughput.DownloadBytesPerSecond);
        Assert.Equal(300, throughput.UploadBytesPerSecond);
    }

    [Theory]
    [InlineData("ethernet", 1_000, 100, 20_000)]
    [InlineData("wifi", 900, 100, 20_000)]
    [InlineData("wifi", 1_000, 50, 20_000)]
    public void TryCalculate_ReturnsFalseForAdapterChangeOrCounterReset(
        string signature,
        ulong downloadBytes,
        ulong uploadBytes,
        long timestamp)
    {
        var previous = Sample("wifi", 1_000, 100, 10_000);
        var current = Sample(signature, downloadBytes, uploadBytes, timestamp);

        Assert.False(NetworkThroughputCalculator.TryCalculate(previous, current, out _));
    }

    [Theory]
    [InlineData(1_023, 1_023, "B/s")]
    [InlineData(1_024, 1, "KB/s")]
    [InlineData(13_107_200, 12.5, "MB/s")]
    [InlineData(1_073_741_824, 1, "GB/s")]
    public void Format_AutomaticallySelectsCompactBinaryUnit(double bytesPerSecond, double expectedValue, string expectedUnit)
    {
        var display = NetworkThroughputFormatter.Format(bytesPerSecond);

        Assert.Equal(expectedValue, display.Value, 5);
        Assert.Equal(expectedUnit, display.Unit);
    }

    [Fact]
    public async Task CollectAsync_TransitionsFromUnavailableToAvailable_WhenHardwareTestIsEnabled()
    {
        if (Environment.GetEnvironmentVariable("DEVOVERLAY_RUN_NETWORK_HARDWARE_TEST") != "1")
        {
            return;
        }

        var provider = new NetworkThroughputMetricProvider();
        var firstSample = await provider.CollectAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        var secondSample = await provider.CollectAsync(CancellationToken.None);

        Assert.All(firstSample, metric => Assert.False(metric.IsAvailable));
        Assert.Collection(
            secondSample.OrderBy(metric => metric.Id),
            metric => AssertAvailable(metric, MetricId.NetworkDownload),
            metric => AssertAvailable(metric, MetricId.NetworkUpload));
    }

    private static NetworkCounterSample Sample(string signature, ulong download, ulong upload, long timestamp) =>
        new(signature, download, upload, timestamp);

    private static void AssertAvailable(MetricSnapshot metric, MetricId expectedId)
    {
        Assert.Equal(expectedId, metric.Id);
        Assert.True(metric.IsAvailable);
        Assert.NotNull(metric.Value);
        Assert.True(metric.Value >= 0);
    }
}
