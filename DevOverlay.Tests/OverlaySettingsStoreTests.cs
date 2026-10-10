using System.IO;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using Xunit;

namespace DevOverlay.Tests;

public sealed class OverlaySettingsStoreTests
{
    [Fact]
    public void RefreshIntervalDefaultsClampsAndRoundTripsExactDirectValuesWithoutResettingOtherSettings()
    {
        using var directory = new TemporarySettingsDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, """{"RefreshIntervalMs":100,"FpsExecutable":"game.exe","LatencyGroupEnabled":false}""");
        var store = new OverlaySettingsStore(path);
        var minimum = store.Load();
        Assert.Equal(250, minimum.RefreshIntervalMs);
        Assert.Equal(DeviceSelection.Specific("game.exe"), minimum.FpsTargetSelection);
        Assert.DoesNotContain(MetricCategory.Latency, minimum.EnabledGroups);

        store.Save(minimum with { RefreshIntervalMs = 333 });
        Assert.Equal(333, store.Load().RefreshIntervalMs);
        File.WriteAllText(path, """{"RefreshIntervalMs":5000}""");
        Assert.Equal(2000, store.Load().RefreshIntervalMs);
        File.WriteAllText(path, "{}" );
        Assert.Equal(500, store.Load().RefreshIntervalMs);
    }

    [Fact]
    public void LegacyScaleAndMissingLatencyFieldsKeepFrameVisibilityAndMigrateOrder()
    {
        using var directory = new TemporarySettingsDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, """{"FpsGroupEnabled":true,"FramesPerSecondEnabled":true,"FpsExecutable":"game.exe","GroupOrder":["Network","Cpu","Frame","Gpu","Storage"],"Appearance":{"UiScale":1.6}}""");
        var settings = new OverlaySettingsStore(path).Load();
        Assert.Equal(.8, settings.Appearance.Spacing);
        Assert.Contains(MetricCategory.Frame, settings.EnabledGroups);
        Assert.Contains(MetricCategory.Latency, settings.EnabledGroups);
        foreach (var id in new[] { MetricId.FramesPerSecond, MetricId.OnePercentLow, MetricId.FrameTime, MetricId.Latency })
            Assert.Contains(id, settings.EnabledMetrics);
        Assert.Equal([MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Frame, MetricCategory.Latency,
            MetricCategory.Gpu, MetricCategory.Storage, MetricCategory.AiUsage, MetricCategory.PeripheralBattery, MetricCategory.Battery], settings.GroupOrder);
        Assert.Equal(DeviceSelection.Specific("game.exe"), settings.FpsTargetSelection);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenSettingsFileIsMissing()
    {
        using var directory = new TemporarySettingsDirectory();
        var settings = new OverlaySettingsStore(Path.Combine(directory.Path, "missing.json")).Load();

        Assert.True(settings.EnabledGroups.Contains(MetricCategory.Cpu));
        Assert.True(settings.EnabledGroups.Contains(MetricCategory.Gpu));
        Assert.IsType<AutomaticDeviceSelection>(settings.NetworkDeviceSelection);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsVisibilityAndSpecificDeviceIds()
    {
        using var directory = new TemporarySettingsDirectory();
        var store = new OverlaySettingsStore(Path.Combine(directory.Path, "settings.json"));
        var expected = OverlaySettings.CreateForCurrentFeatures(
            cpuEnabled: false,
            gpuEnabled: true,
            networkEnabled: false,
            storageEnabled: true,
            gpuDeviceSelection: DeviceSelection.Specific("GPU-uuid"),
            networkDeviceSelection: DeviceSelection.Specific("network-guid"),
            storageDeviceSelection: DeviceSelection.Specific("physical-disk:1 C:"),
            isVisible: false);

        store.Save(expected);
        var loaded = store.Load();

        Assert.False(loaded.IsVisible);
        Assert.False(loaded.EnabledGroups.Contains(MetricCategory.Cpu));
        Assert.True(loaded.EnabledGroups.Contains(MetricCategory.Gpu));
        Assert.False(loaded.EnabledGroups.Contains(MetricCategory.Network));
        Assert.True(loaded.EnabledGroups.Contains(MetricCategory.Storage));
        Assert.Equal("GPU-uuid", Assert.IsType<SpecificDeviceSelection>(loaded.GpuDeviceSelection).DeviceId);
        Assert.Equal("network-guid", Assert.IsType<SpecificDeviceSelection>(loaded.NetworkDeviceSelection).DeviceId);
        Assert.Equal("physical-disk:1 C:", Assert.IsType<SpecificDeviceSelection>(loaded.StorageDeviceSelection).DeviceId);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenSettingsJsonIsMalformed()
    {
        using var directory = new TemporarySettingsDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{ invalid json");

        var settings = new OverlaySettingsStore(path).Load();

        Assert.True(settings.EnabledGroups.Contains(MetricCategory.Cpu));
        Assert.True(settings.IsVisible);
    }

    [Fact]
    public void Load_UsesDefaultsForMissingProperties_AndPreservesUnknownDeviceId()
    {
        using var directory = new TemporarySettingsDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{\"CpuGroupEnabled\":false,\"NetworkDeviceId\":\"missing-interface\",\"NewerSetting\":true}");

        var settings = new OverlaySettingsStore(path).Load();

        Assert.False(settings.EnabledGroups.Contains(MetricCategory.Cpu));
        Assert.True(settings.EnabledGroups.Contains(MetricCategory.Gpu));
        Assert.Equal("missing-interface", Assert.IsType<SpecificDeviceSelection>(settings.NetworkDeviceSelection).DeviceId);
        Assert.True(settings.EnabledMetrics.Contains(MetricId.GpuTemperature));
        Assert.IsType<AutomaticDeviceSelection>(settings.GpuDeviceSelection);
        Assert.IsType<SystemDriveDeviceSelection>(settings.StorageDeviceSelection);
    }

    [Fact]
    public void ChangingOneDevice_PreservesOtherSelectionsAndMetricVisibilityAfterReload()
    {
        using var directory = new TemporarySettingsDirectory();
        var store = new OverlaySettingsStore(Path.Combine(directory.Path, "settings.json"));
        var before = OverlaySettings.CreateForCurrentFeatures(
            true, true, true, true, DeviceSelection.Auto,
            DeviceSelection.Specific("network-a"), DeviceSelection.Specific("disk-a"),
            gpuTemperatureEnabled: false, storageWriteEnabled: false);
        store.Save(before);

        var viewModel = new DevOverlay.Presentation.SettingsViewModel(store.Load());
        viewModel.SettingsChanged += store.Save;
        viewModel.GpuDeviceId = "gpu-a";
        var after = store.Load();

        Assert.Equal("gpu-a", Assert.IsType<SpecificDeviceSelection>(after.GpuDeviceSelection).DeviceId);
        Assert.Equal("network-a", Assert.IsType<SpecificDeviceSelection>(after.NetworkDeviceSelection).DeviceId);
        Assert.Equal("disk-a", Assert.IsType<SpecificDeviceSelection>(after.StorageDeviceSelection).DeviceId);
        Assert.DoesNotContain(MetricId.GpuTemperature, after.EnabledMetrics);
        Assert.DoesNotContain(MetricId.StorageWrite, after.EnabledMetrics);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsIndividualMetricVisibility()
    {
        using var directory = new TemporarySettingsDirectory();
        var store = new OverlaySettingsStore(Path.Combine(directory.Path, "settings.json"));
        var expected = OverlaySettings.CreateForCurrentFeatures(
            cpuEnabled: true,
            gpuEnabled: true,
            networkEnabled: true,
            storageEnabled: true,
            gpuDeviceSelection: DeviceSelection.Auto,
            networkDeviceSelection: DeviceSelection.Auto,
            storageDeviceSelection: DeviceSelection.Auto,
            gpuTemperatureEnabled: false,
            networkUploadEnabled: false,
            storageReadEnabled: false);

        store.Save(expected);
        var loaded = store.Load();

        Assert.True(loaded.EnabledMetrics.Contains(MetricId.GpuUtilization));
        Assert.False(loaded.EnabledMetrics.Contains(MetricId.GpuTemperature));
        Assert.True(loaded.EnabledMetrics.Contains(MetricId.NetworkDownload));
        Assert.False(loaded.EnabledMetrics.Contains(MetricId.NetworkUpload));
        Assert.False(loaded.EnabledMetrics.Contains(MetricId.StorageRead));
        Assert.True(loaded.EnabledMetrics.Contains(MetricId.StorageWrite));
    }

    private sealed class TemporarySettingsDirectory : IDisposable
    {
        public TemporarySettingsDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DevOverlay.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
