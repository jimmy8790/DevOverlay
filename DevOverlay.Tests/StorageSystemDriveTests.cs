using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using Xunit;

namespace DevOverlay.Tests;

public sealed class StorageSystemDriveTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32", "C:")]
    [InlineData(@"D:\Windows", "D:")]
    [InlineData(@"\\server\share\Windows", null)]
    public void SystemVolume_UsesActualWindowsSystemDirectory(string directory, string? expected) =>
        Assert.Equal(expected, SystemDriveResolver.FromSystemDirectory(directory));

    [Fact]
    public void SystemDrive_UsesLogicalDiskCounterNotPhysicalAuto()
    {
        var physical = new[]
        {
            new DeviceDescriptor("physical-disk:0 C:", "0 C:"),
            new DeviceDescriptor("physical-disk:1 D:", "1 D:")
        };
        var system = Assert.Single(PhysicalDiskThroughputMetricProvider.ResolveCounterTargets(
            DeviceSelection.SystemDrive, physical, "C:"));
        Assert.Equal("LogicalDisk", system.Category);
        Assert.Equal("C:", system.InstanceName);
        Assert.Empty(PhysicalDiskThroughputMetricProvider.ResolveCounterTargets(DeviceSelection.SystemDrive, physical, null));

        var auto = PhysicalDiskThroughputMetricProvider.ResolveCounterTargets(DeviceSelection.Auto, physical, "C:");
        Assert.Equal(2, auto.Length);
        Assert.All(auto, target => Assert.Equal("PhysicalDisk", target.Category));
        var specific = Assert.Single(PhysicalDiskThroughputMetricProvider.ResolveCounterTargets(
            DeviceSelection.Specific("physical-disk:1 D:"), physical, "C:"));
        Assert.Equal("1 D:", specific.InstanceName);
        Assert.Empty(PhysicalDiskThroughputMetricProvider.ResolveCounterTargets(
            DeviceSelection.Specific("missing"), physical, "C:"));
    }

    [Fact]
    public void NewAndOldStorageSettings_PreserveExplicitChoices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DevOverlay-system-{Guid.NewGuid():N}.json");
        try
        {
            var store = new OverlaySettingsStore(path);
            Assert.IsType<SystemDriveDeviceSelection>(store.Load().StorageDeviceSelection);
            File.WriteAllText(path, "{\"GpuDeviceId\":null}");
            Assert.IsType<SystemDriveDeviceSelection>(store.Load().StorageDeviceSelection);
            File.WriteAllText(path, "{\"StorageDeviceId\":null}");
            Assert.IsType<AutomaticDeviceSelection>(store.Load().StorageDeviceSelection);
            File.WriteAllText(path, "{\"StorageDeviceId\":\"physical-disk:1 D:\"}");
            Assert.Equal("physical-disk:1 D:",
                Assert.IsType<SpecificDeviceSelection>(store.Load().StorageDeviceSelection).DeviceId);
            store.Save(OverlaySettings.CreateDefault());
            Assert.IsType<SystemDriveDeviceSelection>(store.Load().StorageDeviceSelection);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SystemDriveSelection_RoundTripsWithoutResettingPositionAppearanceOrOtherDevices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DevOverlay-system-{Guid.NewGuid():N}.json");
        try
        {
            var expected = OverlaySettings.CreateDefault() with
            {
                StorageDeviceSelection = DeviceSelection.SystemDrive,
                GpuDeviceSelection = DeviceSelection.Specific("gpu"),
                NetworkDeviceSelection = DeviceSelection.Specific("network"),
                Position = OverlayPosition.CreateCustom(250, 125),
                Appearance = new OverlayAppearance("#112233", 0, "#445566", "#778899")
            };
            var store = new OverlaySettingsStore(path);
            store.Save(expected);
            var actual = store.Load();
            Assert.IsType<SystemDriveDeviceSelection>(actual.StorageDeviceSelection);
            Assert.Equal(expected.Position, actual.Position);
            Assert.Equal(expected.Appearance, actual.Appearance);
            Assert.Equal("gpu", Assert.IsType<SpecificDeviceSelection>(actual.GpuDeviceSelection).DeviceId);
            Assert.Equal("network", Assert.IsType<SpecificDeviceSelection>(actual.NetworkDeviceSelection).DeviceId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task SystemDriveHardwareProbe_WhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("DEVOVERLAY_RUN_STORAGE_HARDWARE_TEST") != "1") return;
        await using var provider = new PhysicalDiskThroughputMetricProvider(DeviceSelection.SystemDrive);
        await provider.CollectAsync(CancellationToken.None);
        await Task.Delay(1100);
        var metrics = await provider.CollectAsync(CancellationToken.None);
        Assert.All(metrics, metric => Assert.True(metric.IsAvailable));
    }
}
