using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class PhysicalDiskThroughputTests
{
    [Fact]
    public void AutoSelection_AggregatesEveryDistinctPhysicalDisk()
    {
        DeviceDescriptor[] available =
        [
            new("physical-disk:1 C:", "1 C:"),
            new("physical-disk:0 D:", "0 D:"),
            new("physical-disk:1 C:", "Duplicate")
        ];

        var selected = PhysicalDiskThroughputMetricProvider.SelectDevices(DeviceSelection.Auto, available);

        Assert.Collection(
            selected,
            device => Assert.Equal("physical-disk:0 D:", device.Id),
            device => Assert.Equal("physical-disk:1 C:", device.Id));
    }

    [Fact]
    public void SpecificSelection_UsesOnlyMatchingPhysicalDisk()
    {
        DeviceDescriptor[] available =
        [
            new("physical-disk:0 D:", "0 D:"),
            new("physical-disk:1 C:", "1 C:")
        ];

        var selected = PhysicalDiskThroughputMetricProvider.SelectDevices(
            DeviceSelection.Specific("physical-disk:1 C:"),
            available);

        var device = Assert.Single(selected);
        Assert.Equal("physical-disk:1 C:", device.Id);
    }

    [Fact]
    public void SpecificSelection_ReturnsNoDeviceWhenConfiguredDiskIsUnavailable()
    {
        var selected = PhysicalDiskThroughputMetricProvider.SelectDevices(
            DeviceSelection.Specific("physical-disk:missing"),
            [new DeviceDescriptor("physical-disk:0 D:", "0 D:")]);

        Assert.Empty(selected);
    }

    [Theory]
    [InlineData(0, 0, "B/s")]
    [InlineData(1_024, 1, "KB/s")]
    [InlineData(13_107_200, 12.5, "MB/s")]
    public void Format_UsesCompactByteRateUnits(double bytesPerSecond, double expectedValue, string expectedUnit)
    {
        var display = ByteRateFormatter.Format(bytesPerSecond);

        Assert.Equal(expectedValue, display.Value, 5);
        Assert.Equal(expectedUnit, display.Unit);
    }

    [Fact]
    public async Task CollectAsync_TransitionsFromUnavailableToAvailable_WhenHardwareTestIsEnabled()
    {
        if (Environment.GetEnvironmentVariable("DEVOVERLAY_RUN_STORAGE_HARDWARE_TEST") != "1")
        {
            return;
        }

        await using var provider = new PhysicalDiskThroughputMetricProvider();
        if (provider.GetAvailableDevices().Count == 0)
        {
            return;
        }

        var firstSample = await provider.CollectAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        var secondSample = await provider.CollectAsync(CancellationToken.None);

        Assert.All(firstSample, metric => Assert.False(metric.IsAvailable));
        Assert.Collection(
            secondSample.OrderBy(metric => metric.Id),
            metric => AssertAvailable(metric, MetricId.StorageRead),
            metric => AssertAvailable(metric, MetricId.StorageWrite));
    }

    private static void AssertAvailable(MetricSnapshot metric, MetricId expectedId)
    {
        Assert.Equal(expectedId, metric.Id);
        Assert.True(metric.IsAvailable);
        Assert.NotNull(metric.Value);
        Assert.True(metric.Value >= 0);
    }
}
