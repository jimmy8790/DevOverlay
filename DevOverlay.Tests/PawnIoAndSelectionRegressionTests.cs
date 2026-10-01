using System.Net.NetworkInformation;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;
using DevOverlay.UI;
using Xunit;

namespace DevOverlay.Tests;

public sealed class PawnIoAndSelectionRegressionTests
{
    [Fact]
    public void PawnIoDetection_SeparatesMissingOutdatedAndInstalled_WithoutInstalling()
    {
        Assert.Equal(PawnIoState.NotInstalled, new PawnIoPrerequisiteService(() => null).Detect().State);
        Assert.Equal(PawnIoState.Outdated, new PawnIoPrerequisiteService(() => new Version(1, 9)).Detect().State);
        Assert.Equal(PawnIoState.Installed, new PawnIoPrerequisiteService(() => new Version(2, 1)).Detect().State);
    }

    [Fact]
    public void PawnIoDiagnosis_DistinguishesRegistryFromUsableDriver()
    {
        var denied = new PawnIoPrerequisiteService(() => new Version(2, 1), () => new PawnIoDeviceAccess(false, 5)).Diagnose();
        Assert.Equal(PawnIoState.InstalledButDriverUnavailable, denied.State);
        Assert.Contains("5", denied.Detail);
        var accessible = new PawnIoPrerequisiteService(() => new Version(2, 1), () => new PawnIoDeviceAccess(true, 0)).Diagnose();
        Assert.Equal(PawnIoState.DriverAccessible, accessible.State);
    }

    [Fact]
    public void PawnIoStatusUpdates_DoNotPublishOverlaySettings()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        var changes = 0;
        viewModel.SettingsChanged += _ => changes++;
        viewModel.UpdatePawnIoStatus(new PawnIoStatus(PawnIoState.NotInstalled));
        Assert.True(viewModel.CanInstallPawnIo);
        viewModel.UpdatePawnIoStatus(new PawnIoStatus(PawnIoState.Installed, new Version(2, 1)));
        Assert.False(viewModel.CanInstallPawnIo);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void CpuSensorStatus_IsIndependentOfCpuVisibility()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        var changes = new List<OverlaySettings>();
        viewModel.SettingsChanged += changes.Add;
        viewModel.CpuEnabled = false;
        viewModel.UpdatePawnIoStatus(new PawnIoStatus(PawnIoState.InstalledButDriverUnavailable,
            new Version(2, 1), "Windows error 5"));
        viewModel.UpdateCpuSensorAvailability(false, false);
        Assert.Single(changes);
        Assert.False(changes[0].EnabledGroups.Contains(MetricCategory.Cpu));
        Assert.Contains("unavailable", viewModel.CpuSensorStatusText);
        Assert.False(viewModel.CanRetryCpuSensors);
    }

    [Fact]
    public void CpuControls_AreInDedicatedSettingsSection()
    {
        RunOnSta(() =>
        {
            var window = new SettingsWindow(new SettingsViewModel(OverlaySettings.CreateDefault()));
            try
            {
                var cpu = Assert.IsType<GroupBox>(window.FindName("CpuSettingsGroup"));
                var overlay = Assert.IsType<GroupBox>(window.FindName("OverlaySettingsGroup"));
                var toggle = Assert.IsType<CheckBox>(window.FindName("ShowCpuCheckBox"));
                var serviceButton = Assert.IsType<Button>(window.FindName("InstallSensorServiceButton"));
                Assert.Contains(toggle, Assert.IsType<StackPanel>(cpu.Content).Children.Cast<System.Windows.UIElement>());
                Assert.Contains(Assert.IsType<StackPanel>(cpu.Content).Children.OfType<WrapPanel>(),
                    panel => panel.Children.Contains(serviceButton));
                Assert.DoesNotContain(toggle, Assert.IsType<StackPanel>(overlay.Content).Children.Cast<System.Windows.UIElement>());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void CpuServiceActionsKeepPawnIoAndUseWrappedRowsWithVerticalSpacing()
    {
        RunOnSta(() =>
        {
            var window = new SettingsWindow(new SettingsViewModel(OverlaySettings.CreateDefault()));
            try
            {
                window.Width = window.MinWidth;
                window.Show();
                FlushBindings();
                var pawnStatus = Assert.IsType<TextBlock>(Descendants(window).Single(text => text.Text.StartsWith("PawnIO is required")));
                var actions = Assert.IsType<WrapPanel>(window.FindName("CpuServiceActionPanel"));
                var buttons = actions.Children.OfType<Button>().ToArray();
                Assert.Equal(4, buttons.Length);
                Assert.Contains(buttons, button => button.Content?.ToString() == "Install Sensor Service");
                Assert.All(buttons, button => Assert.True(button.Margin.Bottom >= 6));
                Assert.Equal(System.Windows.TextWrapping.Wrap, pawnStatus.TextWrapping);
                var rows = buttons.GroupBy(button => Math.Round(button.TransformToAncestor(window).Transform(new System.Windows.Point()).Y, 1))
                    .OrderBy(row => row.Key).ToArray();
                Assert.True(rows.Length >= 2);
                var firstBottom = rows[0].Max(button => button.TransformToAncestor(window).Transform(new System.Windows.Point()).Y + button.ActualHeight);
                var secondTop = rows[1].Min(button => button.TransformToAncestor(window).Transform(new System.Windows.Point()).Y);
                Assert.True(secondTop >= firstBottom + 6, $"wrapped rows overlap: {firstBottom} -> {secondTop}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PawnIoInstallerResult_RequiresSuccessExitAndInstalledState()
    {
        var installed = new PawnIoStatus(PawnIoState.Installed, new Version(2, 1));
        Assert.Equal(PawnIoState.Installed, PawnIoPrerequisiteService.EvaluateInstallerResult(0, installed).State);
        Assert.Equal(PawnIoState.InstallFailed, PawnIoPrerequisiteService.EvaluateInstallerResult(1, installed).State);
        Assert.Equal(PawnIoState.InstallFailed, PawnIoPrerequisiteService.EvaluateInstallerResult(0,
            new PawnIoStatus(PawnIoState.NotInstalled)).State);
    }

    [Fact]
    public void NetworkAutoAndSpecific_SelectExactCountersWithoutFallback()
    {
        var adapters = new[]
        {
            new NetworkAdapterCounter("a", NetworkInterfaceType.Ethernet, OperationalStatus.Up, true, 10, 20),
            new NetworkAdapterCounter("b", NetworkInterfaceType.Ethernet, OperationalStatus.Up, true, 30, 40),
            new NetworkAdapterCounter("c", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, false, 50, 60)
        };
        Assert.Equal(new[] { "a", "b" }, NetworkAdapterSelection.Select(adapters, DeviceSelection.Auto).Select(x => x.Id));
        Assert.Equal(10UL, Assert.Single(NetworkAdapterSelection.Select(adapters, DeviceSelection.Specific("a"))).Received);
        Assert.Equal(30UL, Assert.Single(NetworkAdapterSelection.Select(adapters, DeviceSelection.Specific("B"))).Received);
        Assert.Empty(NetworkAdapterSelection.Select(adapters, DeviceSelection.Specific("missing")));
    }

    [Fact]
    public void BluetoothSpecificSelection_UsesOnlyThatActiveInterface()
    {
        var adapters = new[]
        {
            new NetworkAdapterCounter("wifi", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, true, 100, 200),
            new NetworkAdapterCounter("bluetooth", NetworkInterfaceType.Ethernet, OperationalStatus.Up, false, 0, 0)
        };
        var selected = NetworkAdapterSelection.Select(adapters, DeviceSelection.Specific("bluetooth"));
        Assert.Single(selected);
        Assert.Equal(0UL, selected[0].Received);
        Assert.Equal(0UL, selected[0].Sent);
    }

    [Fact]
    public void RuntimeProviders_ReceiveIndependentSpecificIds()
    {
        var settings = OverlaySettings.CreateDefault() with
        {
            GpuDeviceSelection = DeviceSelection.Specific("gpu"),
            NetworkDeviceSelection = DeviceSelection.Specific("network"),
            StorageDeviceSelection = DeviceSelection.Specific("disk")
        };
        var providers = App.CreateRuntimeProviders(settings);
        Assert.Equal("gpu", Assert.IsType<SpecificDeviceSelection>(providers.OfType<NvidiaGpuMetricProvider>().Single().Selection).DeviceId);
        Assert.Equal("network", Assert.IsType<SpecificDeviceSelection>(providers.OfType<NetworkThroughputMetricProvider>().Single().Selection).DeviceId);
        Assert.Equal("network", Assert.IsType<SpecificDeviceSelection>(providers.OfType<NetworkTodayMetricProvider>().Single().Selection).DeviceId);
        Assert.Equal("disk", Assert.IsType<SpecificDeviceSelection>(providers.OfType<PhysicalDiskThroughputMetricProvider>().Single().Selection).DeviceId);
    }

    [Fact]
    public void ChangingDevice_InvalidatesOldCategoryValuesWithoutTouchingOthers()
    {
        var viewModel = new OverlayViewModel(OverlaySettings.CreateDefault(), Dispatcher.CurrentDispatcher);
        viewModel.ApplyMetrics([
            new MetricSnapshot(MetricId.NetworkDownload, MetricCategory.Network, "Download", 12, "MB/s", true, DateTimeOffset.UtcNow),
            new MetricSnapshot(MetricId.CpuUtilization, MetricCategory.Cpu, "CPU", 25, "%", true, DateTimeOffset.UtcNow)
        ]);
        viewModel.MarkCategoryUnavailable(MetricCategory.Network);
        Assert.Equal("↓ N/A", viewModel.Groups.Single(group => group.Category == MetricCategory.Network).Items.Single().Text);
        Assert.Equal("25%", viewModel.Groups.Single(group => group.Category == MetricCategory.Cpu).Items.Single().Text);
    }

    [Fact]
    public void ActualSettingsWindow_AutoIsVisibleBeforeAndAfterEnumeration()
    {
        RunOnSta(() =>
        {
            var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
            var changes = 0;
            viewModel.SettingsChanged += _ => changes++;
            var window = new SettingsWindow(viewModel);
            try
            {
                window.Show();
                FlushBindings();
                AssertAuto(window);
                viewModel.UpdateGpuDevices([new DeviceDescriptor("gpu", "GPU")]);
                viewModel.UpdateNetworkDevices([new DeviceDescriptor("network", "Network")]);
                viewModel.UpdateStorageDevices([new DeviceDescriptor("disk", "Disk")]);
                FlushBindings();
                AssertAuto(window);
                Assert.Equal(0, changes);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ActualSettingsWindow_FirstSpecificSelectionsPersistThroughListRefresh()
    {
        RunOnSta(() =>
        {
            var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
            var window = new SettingsWindow(viewModel);
            try
            {
                window.Show();
                viewModel.UpdateGpuDevices([new DeviceDescriptor("gpu", "GPU")]);
                viewModel.UpdateNetworkDevices([new DeviceDescriptor("network", "Network")]);
                viewModel.UpdateStorageDevices([new DeviceDescriptor("disk", "Disk")]);
                FlushBindings();
                var gpu = Assert.IsType<ComboBox>(window.FindName("GpuDeviceComboBox"));
                var network = Assert.IsType<ComboBox>(window.FindName("NetworkDeviceComboBox"));
                var storage = Assert.IsType<ComboBox>(window.FindName("StorageDeviceComboBox"));
                var updates = new List<OverlaySettings>();
                viewModel.SettingsChanged += updates.Add;
                gpu.SelectedValue = "gpu";
                network.SelectedValue = "network";
                storage.SelectedValue = "disk";
                FlushBindings();
                Assert.Equal(3, updates.Count);
                viewModel.UpdateGpuDevices([new DeviceDescriptor("gpu", "GPU")]);
                viewModel.UpdateNetworkDevices([new DeviceDescriptor("network", "Network")]);
                viewModel.UpdateStorageDevices([new DeviceDescriptor("disk", "Disk")]);
                FlushBindings();
                Assert.Equal("gpu", gpu.SelectedValue);
                Assert.Equal("network", network.SelectedValue);
                Assert.Equal("disk", storage.SelectedValue);
                Assert.Equal(3, updates.Count);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SettingsEdits_PreserveCustomPositionAndOtherSelections()
    {
        var position = OverlayPosition.CreateCustom(300, 240);
        var settings = OverlaySettings.CreateDefault() with
        {
            Position = position,
            GpuDeviceSelection = DeviceSelection.Specific("gpu"),
            StorageDeviceSelection = DeviceSelection.Specific("disk")
        };
        var viewModel = new SettingsViewModel(settings);
        var updates = new List<OverlaySettings>();
        viewModel.SettingsChanged += updates.Add;
        viewModel.CpuUsageEnabled = false;
        viewModel.GpuPowerEnabled = false;
        viewModel.CpuEnabled = false;
        viewModel.BackgroundOpacityPercent = 50;
        viewModel.NetworkDeviceId = "network";
        Assert.All(updates, update => Assert.Equal(position, update.Position));
        Assert.All(updates, update => Assert.Equal("gpu", Assert.IsType<SpecificDeviceSelection>(update.GpuDeviceSelection).DeviceId));
        Assert.All(updates, update => Assert.Equal("disk", Assert.IsType<SpecificDeviceSelection>(update.StorageDeviceSelection).DeviceId));
        Assert.Equal("network", Assert.IsType<SpecificDeviceSelection>(updates[^1].NetworkDeviceSelection).DeviceId);
        Assert.False(App.HasProviderSelectionChanged(updates[0], updates[3]));
        Assert.True(App.HasProviderSelectionChanged(updates[3], updates[4]));
    }

    private static void AssertAuto(SettingsWindow window)
    {
        foreach (var name in new[] { "GpuDeviceComboBox", "NetworkDeviceComboBox" })
        {
            var combo = Assert.IsType<ComboBox>(window.FindName(name));
            Assert.Equal(DeviceChoice.AutoId, combo.SelectedValue);
            Assert.Equal("Auto", Assert.IsType<DeviceChoice>(combo.SelectedItem).DisplayName);
        }
        var storage = Assert.IsType<ComboBox>(window.FindName("StorageDeviceComboBox"));
        Assert.Equal(DeviceChoice.SystemDriveId, storage.SelectedValue);
        Assert.StartsWith("System (", Assert.IsType<DeviceChoice>(storage.SelectedItem).DisplayName);
    }

    private static void FlushBindings()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<TextBlock> Descendants(System.Windows.DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is TextBlock text) yield return text;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
