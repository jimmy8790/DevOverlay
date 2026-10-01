using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void RefreshIntervalSliderAndDirectTextShareOneExactPersistedValue()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        OverlaySettings? changed = null;
        viewModel.SettingsChanged += settings => changed = settings;

        viewModel.RefreshIntervalSliderValue = 752;
        Assert.Equal(750, viewModel.RefreshIntervalMs);
        Assert.Equal("750", viewModel.RefreshIntervalText);

        viewModel.RefreshIntervalText = "333";
        Assert.Equal(333, viewModel.RefreshIntervalMs);
        Assert.Equal(333, viewModel.RefreshIntervalSliderValue);
        Assert.Equal(333, changed?.RefreshIntervalMs);
    }

    [Fact]
    public void RefreshIntervalTemporaryInvalidTextDoesNotChangeRuntimeValueAndCommitClampsIntegers()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        var changeCount = 0;
        viewModel.SettingsChanged += _ => changeCount++;

        viewModel.RefreshIntervalText = "";
        viewModel.RefreshIntervalText = "abc";
        Assert.Equal(500, viewModel.RefreshIntervalMs);
        Assert.Equal(0, changeCount);

        viewModel.CommitRefreshIntervalText();
        Assert.Equal("500", viewModel.RefreshIntervalText);
        viewModel.RefreshIntervalText = "100";
        viewModel.CommitRefreshIntervalText();
        Assert.Equal(250, viewModel.RefreshIntervalMs);
        viewModel.RefreshIntervalText = "5000";
        viewModel.CommitRefreshIntervalText();
        Assert.Equal(2000, viewModel.RefreshIntervalMs);
    }

    [Fact]
    public void DeviceSelectionChange_PublishesSpecificStableId()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        OverlaySettings? changedSettings = null;
        viewModel.SettingsChanged += settings => changedSettings = settings;

        viewModel.GpuDeviceId = "GPU-stable-id";

        Assert.NotNull(changedSettings);
        Assert.Equal("GPU-stable-id", Assert.IsType<SpecificDeviceSelection>(changedSettings.GpuDeviceSelection).DeviceId);
    }

    [Fact]
    public void DeviceSelectionChange_PreservesUnrelatedOverlaySettings()
    {
        var position = new OverlayPosition("PrimaryDisplay", OverlayAnchor.BottomLeft, 25, 30);
        var settings = OverlaySettings.CreateDefault() with
        {
            Position = position,
            PopupBehavior = PopupBehavior.Hover
        };
        var viewModel = new SettingsViewModel(settings);
        OverlaySettings? updated = null;
        viewModel.SettingsChanged += value => updated = value;

        viewModel.GpuDeviceId = "gpu-a";

        Assert.NotNull(updated);
        Assert.Equal(position, updated.Position);
        Assert.Equal(PopupBehavior.Hover, updated.PopupBehavior);
    }

    [Fact]
    public void MissingConfiguredDevice_RemainsAvailableAsAnUnavailableChoice()
    {
        var settings = OverlaySettings.CreateForCurrentFeatures(
            cpuEnabled: true,
            gpuEnabled: true,
            networkEnabled: true,
            storageEnabled: true,
            gpuDeviceSelection: DeviceSelection.Specific("missing-gpu"),
            networkDeviceSelection: DeviceSelection.Auto,
            storageDeviceSelection: DeviceSelection.Auto);
        var viewModel = new SettingsViewModel(settings);

        viewModel.UpdateGpuDevices([new DeviceDescriptor("other-gpu", "Other GPU")]);

        Assert.Contains(viewModel.GpuDevices, choice => choice.Id == "missing-gpu" && choice.DisplayName.StartsWith("Unavailable"));
    }

    [Fact]
    public void DeviceListRefresh_KeepsSpecificIdsWhenComboBoxTemporarilyReportsNull()
    {
        var settings = OverlaySettings.CreateForCurrentFeatures(
            cpuEnabled: true,
            gpuEnabled: true,
            networkEnabled: true,
            storageEnabled: true,
            gpuDeviceSelection: DeviceSelection.Specific("GPU-uuid"),
            networkDeviceSelection: DeviceSelection.Specific("network-id"),
            storageDeviceSelection: DeviceSelection.Specific("physical-disk:1 C:"));
        var viewModel = new SettingsViewModel(settings);

        viewModel.UpdateGpuDevices([new DeviceDescriptor("GPU-uuid", "GPU")]);
        viewModel.UpdateNetworkDevices([new DeviceDescriptor("network-id", "Network")]);
        viewModel.UpdateStorageDevices([new DeviceDescriptor("physical-disk:1 C:", "Disk")]);
        viewModel.GpuDeviceId = null!;
        viewModel.NetworkDeviceId = null!;
        viewModel.StorageDeviceId = null!;
        viewModel.GpuDeviceId = "";
        viewModel.NetworkDeviceId = "";
        viewModel.StorageDeviceId = "";

        Assert.Equal("GPU-uuid", viewModel.GpuDeviceId);
        Assert.Equal("network-id", viewModel.NetworkDeviceId);
        Assert.Equal("physical-disk:1 C:", viewModel.StorageDeviceId);
        Assert.Contains(viewModel.GpuDevices, choice => choice.Id == viewModel.GpuDeviceId);
        Assert.Contains(viewModel.NetworkDevices, choice => choice.Id == viewModel.NetworkDeviceId);
        Assert.Contains(viewModel.StorageDevices, choice => choice.Id == viewModel.StorageDeviceId);
    }

    [Fact]
    public void ReopenedSettingsViewModel_ResolvesPersistedSpecificDeviceIds()
    {
        var settings = OverlaySettings.CreateForCurrentFeatures(
            cpuEnabled: true,
            gpuEnabled: true,
            networkEnabled: true,
            storageEnabled: true,
            gpuDeviceSelection: DeviceSelection.Specific("GPU-uuid"),
            networkDeviceSelection: DeviceSelection.Specific("network-id"),
            storageDeviceSelection: DeviceSelection.Specific("physical-disk:1 C:"));
        var reopened = new SettingsViewModel(settings);

        reopened.UpdateGpuDevices([new DeviceDescriptor("GPU-uuid", "GPU")]);
        reopened.UpdateNetworkDevices([new DeviceDescriptor("network-id", "Network")]);
        reopened.UpdateStorageDevices([new DeviceDescriptor("physical-disk:1 C:", "Disk")]);

        Assert.Equal("GPU-uuid", reopened.GpuDeviceId);
        Assert.Equal("network-id", reopened.NetworkDeviceId);
        Assert.Equal("physical-disk:1 C:", reopened.StorageDeviceId);
    }

    [Fact]
    public void GroupToggle_PublishesUpdatedVisibilityWithoutChangingOtherGroups()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        OverlaySettings? changedSettings = null;
        viewModel.SettingsChanged += settings => changedSettings = settings;

        viewModel.NetworkEnabled = false;

        Assert.NotNull(changedSettings);
        Assert.False(changedSettings.EnabledGroups.Contains(MetricCategory.Network));
        Assert.True(changedSettings.EnabledGroups.Contains(MetricCategory.Cpu));
        Assert.True(changedSettings.EnabledGroups.Contains(MetricCategory.Gpu));
        Assert.True(changedSettings.EnabledGroups.Contains(MetricCategory.Storage));
    }
}
