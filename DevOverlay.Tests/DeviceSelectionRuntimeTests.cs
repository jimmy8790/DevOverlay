using DevOverlay.Configuration;
using DevOverlay.Metrics.Demo;
using DevOverlay.Metrics.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class DeviceSelectionRuntimeTests
{
    [Fact]
    public void DefaultSettings_UseAutomaticGpuNetworkAndSystemStorage()
    {
        var settings = OverlaySettings.CreateDefault();

        Assert.IsType<AutomaticDeviceSelection>(settings.GpuDeviceSelection);
        Assert.IsType<AutomaticDeviceSelection>(settings.NetworkDeviceSelection);
        Assert.IsType<SystemDriveDeviceSelection>(settings.StorageDeviceSelection);
    }

    [Fact]
    public void SpecificSelection_RetainsStableDeviceId()
    {
        var selection = DeviceSelection.Specific("network-guid-or-nvml-uuid");

        Assert.Equal("network-guid-or-nvml-uuid", selection.DeviceId);
    }

    [Fact]
    public async Task MissingExplicitNetworkDevice_ReturnsUnavailableMetrics()
    {
        var provider = new NetworkThroughputMetricProvider(DeviceSelection.Specific("missing-interface-id"));

        var metrics = await provider.CollectAsync(CancellationToken.None);

        Assert.All(metrics, metric => Assert.False(metric.IsAvailable));
    }

    [Fact]
    public async Task MissingExplicitStorageDevice_ReturnsUnavailableMetrics()
    {
        await using var provider = new PhysicalDiskThroughputMetricProvider(DeviceSelection.Specific("missing-physical-disk-id"));

        var metrics = await provider.CollectAsync(CancellationToken.None);

        Assert.All(metrics, metric => Assert.False(metric.IsAvailable));
    }

    [Fact]
    public void NormalRuntimeProviders_ExcludeDemoProvider()
    {
        var providers = App.CreateRuntimeProviders(OverlaySettings.CreateDefault());

        Assert.DoesNotContain(providers, provider => provider is DemoMetricProvider);
        Assert.Contains(providers, provider => provider is NvidiaGpuMetricProvider);
        Assert.Contains(providers, provider => provider is PhysicalDiskThroughputMetricProvider);
        Assert.Contains(providers, provider => provider is NetworkThroughputMetricProvider);
    }
}
