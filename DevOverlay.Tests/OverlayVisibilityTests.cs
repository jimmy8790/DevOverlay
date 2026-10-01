using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class OverlayVisibilityTests
{
    [Fact]
    public void ApplySettings_RemovesDisabledGroupAndRestoresItFromLatestMetrics()
    {
        var settings = OverlaySettings.CreateDefault();
        var viewModel = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        viewModel.ApplyMetrics(
        [
            Available(MetricId.CpuUtilization, MetricCategory.Cpu),
            Available(MetricId.NetworkDownload, MetricCategory.Network),
            Available(MetricId.NetworkUpload, MetricCategory.Network)
        ]);

        Assert.Collection(
            viewModel.Groups,
            group => Assert.Equal(MetricCategory.Cpu, group.Category),
            group => Assert.Equal(MetricCategory.Network, group.Category));

        viewModel.ApplySettings(OverlaySettings.CreateForCurrentFeatures(
            cpuEnabled: true,
            gpuEnabled: true,
            networkEnabled: false,
            storageEnabled: true,
            gpuDeviceSelection: DeviceSelection.Auto,
            networkDeviceSelection: DeviceSelection.Auto,
            storageDeviceSelection: DeviceSelection.Auto));

        Assert.Single(viewModel.Groups);
        Assert.Equal(MetricCategory.Cpu, viewModel.Groups[0].Category);

        viewModel.ApplySettings(settings);

        Assert.Collection(
            viewModel.Groups,
            group => Assert.Equal(MetricCategory.Cpu, group.Category),
            group => Assert.Equal(MetricCategory.Network, group.Category));
    }

    [Fact]
    public void ApplySettings_HidesIndividualMetricsAndRemovesGroupWhenAllChildrenAreDisabled()
    {
        var viewModel = new OverlayViewModel(OverlaySettings.CreateDefault(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        viewModel.ApplyMetrics(
        [
            Available(MetricId.GpuUtilization, MetricCategory.Gpu),
            Available(MetricId.GpuTemperature, MetricCategory.Gpu),
            Available(MetricId.GpuPower, MetricCategory.Gpu),
            Available(MetricId.GpuVramUsed, MetricCategory.Gpu),
            Available(MetricId.NetworkDownload, MetricCategory.Network),
            Available(MetricId.NetworkUpload, MetricCategory.Network),
            Available(MetricId.StorageRead, MetricCategory.Storage),
            Available(MetricId.StorageWrite, MetricCategory.Storage)
        ]);

        viewModel.ApplySettings(OverlaySettings.CreateForCurrentFeatures(
            cpuEnabled: true,
            gpuEnabled: true,
            networkEnabled: true,
            storageEnabled: true,
            gpuDeviceSelection: DeviceSelection.Auto,
            networkDeviceSelection: DeviceSelection.Auto,
            storageDeviceSelection: DeviceSelection.Auto,
            gpuTemperatureEnabled: false,
            gpuVramEnabled: false,
            networkUploadEnabled: false,
            networkTodayTotalEnabled: false,
            storageReadEnabled: false));

        var gpu = Assert.Single(viewModel.Groups, group => group.Category == MetricCategory.Gpu);
        Assert.Collection(
            gpu.Items,
            item => Assert.Equal(MetricId.GpuUtilization, item.Id),
            item => Assert.Equal(MetricId.GpuPower, item.Id));
        var network = Assert.Single(viewModel.Groups, group => group.Category == MetricCategory.Network);
        Assert.Single(network.Items);
        Assert.Equal(MetricId.NetworkDownload, network.Items[0].Id);
        var storage = Assert.Single(viewModel.Groups, group => group.Category == MetricCategory.Storage);
        Assert.Single(storage.Items);
        Assert.Equal(MetricId.StorageWrite, storage.Items[0].Id);

        viewModel.ApplySettings(OverlaySettings.CreateForCurrentFeatures(
            cpuEnabled: true,
            gpuEnabled: true,
            networkEnabled: true,
            storageEnabled: true,
            gpuDeviceSelection: DeviceSelection.Auto,
            networkDeviceSelection: DeviceSelection.Auto,
            storageDeviceSelection: DeviceSelection.Auto,
            gpuUsageEnabled: false,
            gpuTemperatureEnabled: false,
            gpuPowerEnabled: false,
            gpuVramEnabled: false));

        Assert.DoesNotContain(viewModel.Groups, group => group.Category == MetricCategory.Gpu);
    }

    [Fact]
    public void GroupDisable_DoesNotDestroyIndividualMetricPreferences()
    {
        var hiddenGroup = OverlaySettings.CreateForCurrentFeatures(
            cpuEnabled: true,
            gpuEnabled: false,
            networkEnabled: true,
            storageEnabled: true,
            gpuDeviceSelection: DeviceSelection.Auto,
            networkDeviceSelection: DeviceSelection.Auto,
            storageDeviceSelection: DeviceSelection.Auto,
            gpuTemperatureEnabled: false,
            gpuPowerEnabled: false);
        var reenabledGroup = hiddenGroup with
        {
            EnabledGroups = new HashSet<MetricCategory>
            {
                MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Network, MetricCategory.Storage
            }
        };

        Assert.False(hiddenGroup.EnabledGroups.Contains(MetricCategory.Gpu));
        Assert.True(reenabledGroup.EnabledGroups.Contains(MetricCategory.Gpu));
        Assert.True(reenabledGroup.EnabledMetrics.Contains(MetricId.GpuUtilization));
        Assert.False(reenabledGroup.EnabledMetrics.Contains(MetricId.GpuTemperature));
        Assert.False(reenabledGroup.EnabledMetrics.Contains(MetricId.GpuPower));
        Assert.True(reenabledGroup.EnabledMetrics.Contains(MetricId.GpuVramUsed));
    }

    [Fact]
    public void Separators_ExistOnlyBetweenCurrentlyVisibleGroups()
    {
        var viewModel = new OverlayViewModel(OverlaySettings.CreateDefault(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        viewModel.ApplyMetrics(
        [
            Available(MetricId.NetworkDownload, MetricCategory.Network),
            Available(MetricId.CpuUtilization, MetricCategory.Cpu),
            Available(MetricId.StorageRead, MetricCategory.Storage),
            Available(MetricId.GpuUtilization, MetricCategory.Gpu)
        ]);

        AssertSeparators(viewModel, MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Storage, MetricCategory.Network);

        viewModel.ApplySettings(Settings(cpu: true, gpu: true, storage: false, network: true));
        AssertSeparators(viewModel, MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Network);

        viewModel.ApplySettings(Settings(cpu: false, gpu: true, storage: true, network: false));
        AssertSeparators(viewModel, MetricCategory.Gpu, MetricCategory.Storage);

        viewModel.ApplySettings(Settings(cpu: false, gpu: true, storage: false, network: false));
        AssertSeparators(viewModel, MetricCategory.Gpu);

        viewModel.ApplySettings(Settings(cpu: false, gpu: false, storage: false, network: false));
        Assert.Empty(viewModel.Groups);

        viewModel.ApplySettings(OverlaySettings.CreateForCurrentFeatures(
            true, true, true, true, DeviceSelection.Auto, DeviceSelection.Auto, DeviceSelection.Auto,
            gpuUsageEnabled: false, gpuTemperatureEnabled: false, gpuPowerEnabled: false, gpuVramEnabled: false));
        AssertSeparators(viewModel, MetricCategory.Cpu, MetricCategory.Storage, MetricCategory.Network);
    }

    private static OverlaySettings Settings(bool cpu, bool gpu, bool storage, bool network) =>
        OverlaySettings.CreateForCurrentFeatures(cpu, gpu, network, storage,
            DeviceSelection.Auto, DeviceSelection.Auto, DeviceSelection.Auto);

    private static void AssertSeparators(OverlayViewModel viewModel, params MetricCategory[] categories)
    {
        Assert.Equal(categories, viewModel.Groups.Select(group => group.Category));
        Assert.Equal(Math.Max(0, categories.Length - 1), viewModel.Groups.Count(group => group.HasFollowingGroup));
        for (var index = 0; index < viewModel.Groups.Count; index++)
        {
            Assert.Equal(index < viewModel.Groups.Count - 1, viewModel.Groups[index].HasFollowingGroup);
            Assert.Equal(
                index < viewModel.Groups.Count - 1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed,
                viewModel.Groups[index].SeparatorVisibility);
        }
    }

    private static MetricSnapshot Available(MetricId id, MetricCategory category) =>
        new(id, category, "test", 1, "%", true, DateTimeOffset.UtcNow);
}
