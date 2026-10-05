using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

[Collection(WpfCollection.Name)]
public sealed class SettingsComboBoxBindingTests
{
    [Fact]
    public void Auto_IsVisiblySelectedBeforeAndAfterEnumeration_WithoutPublishingChanges()
    {
        RunOnSta(() =>
        {
            var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
            var changes = 0;
            viewModel.SettingsChanged += _ => changes++;
            var gpu = CreateComboBox(viewModel, nameof(viewModel.GpuDevices), nameof(viewModel.SelectedGpuDevice));
            var network = CreateComboBox(viewModel, nameof(viewModel.NetworkDevices), nameof(viewModel.SelectedNetworkDevice));
            var storage = CreateComboBox(viewModel, nameof(viewModel.StorageDevices), nameof(viewModel.SelectedStorageDevice));

            AssertAuto(gpu);
            AssertAuto(network);
            AssertSystem(storage);

            viewModel.UpdateGpuDevices([new DeviceDescriptor("gpu-a", "GPU A")]);
            viewModel.UpdateNetworkDevices([new DeviceDescriptor("network-a", "Network A")]);
            viewModel.UpdateStorageDevices([new DeviceDescriptor("disk-a", "Disk A")]);
            FlushBindings();

            AssertAuto(gpu);
            AssertAuto(network);
            AssertSystem(storage);
            Assert.Equal(0, changes);

            var reopened = new SettingsViewModel(OverlaySettings.CreateDefault());
            AssertAuto(CreateComboBox(reopened, nameof(reopened.GpuDevices), nameof(reopened.SelectedGpuDevice)));
            AssertAuto(CreateComboBox(reopened, nameof(reopened.NetworkDevices), nameof(reopened.SelectedNetworkDevice)));
            AssertSystem(CreateComboBox(reopened, nameof(reopened.StorageDevices), nameof(reopened.SelectedStorageDevice)));
        });
    }

    [Fact]
    public void MissingSpecificDevice_RemainsSelectedAndResolvesWhenItReturns()
    {
        RunOnSta(() =>
        {
            var settings = OverlaySettings.CreateForCurrentFeatures(
                true, true, true, true, DeviceSelection.Specific("gpu-missing"),
                DeviceSelection.Auto, DeviceSelection.Auto);
            var viewModel = new SettingsViewModel(settings);
            var gpu = CreateComboBox(viewModel, nameof(viewModel.GpuDevices), nameof(viewModel.SelectedGpuDevice));
            var changes = 0;
            viewModel.SettingsChanged += _ => changes++;

            Assert.Equal("gpu-missing", viewModel.GpuDeviceId);
            Assert.Same(viewModel.SelectedGpuDevice, gpu.SelectedItem);
            Assert.Equal("gpu-missing", SelectedId(gpu));
            Assert.StartsWith("Unavailable", Assert.IsType<DeviceChoice>(gpu.SelectedItem).DisplayName);
            viewModel.UpdateGpuDevices([new DeviceDescriptor("gpu-missing", "GPU Returned")]);
            FlushBindings();
            Assert.Equal("gpu-missing", SelectedId(gpu));
            Assert.Equal("GPU Returned", Assert.IsType<DeviceChoice>(gpu.SelectedItem).DisplayName);
            Assert.Equal(0, changes);
        });
    }

    [Fact]
    public void FirstSpecificSelection_SurvivesRefresh_AndLeavesOtherCategoriesUnchanged()
    {
        RunOnSta(() =>
        {
            var settings = OverlaySettings.CreateForCurrentFeatures(
                true, true, true, true, DeviceSelection.Auto,
                DeviceSelection.Specific("network-a"), DeviceSelection.Specific("disk-a"));
            var viewModel = new SettingsViewModel(settings);
            var gpu = CreateComboBox(viewModel, nameof(viewModel.GpuDevices), nameof(viewModel.SelectedGpuDevice));
            var network = CreateComboBox(viewModel, nameof(viewModel.NetworkDevices), nameof(viewModel.SelectedNetworkDevice));
            var storage = CreateComboBox(viewModel, nameof(viewModel.StorageDevices), nameof(viewModel.SelectedStorageDevice));
            viewModel.UpdateGpuDevices([new DeviceDescriptor("gpu-a", "GPU A")]);
            viewModel.UpdateNetworkDevices([new DeviceDescriptor("network-a", "Network A")]);
            viewModel.UpdateStorageDevices([new DeviceDescriptor("disk-a", "Disk A")]);
            FlushBindings();
            Assert.Equal("network-a", viewModel.NetworkDeviceId);
            Assert.Equal("disk-a", viewModel.StorageDeviceId);
            Assert.Same(viewModel.SelectedNetworkDevice, network.SelectedItem);
            Assert.Same(viewModel.SelectedStorageDevice, storage.SelectedItem);
            var changes = new List<OverlaySettings>();
            viewModel.SettingsChanged += changes.Add;

            gpu.SelectedItem = viewModel.GpuDevices.Single(choice => choice.Id == "gpu-a");
            FlushBindings();
            Assert.Equal("gpu-a", SelectedId(gpu));
            Assert.Equal("network-a", SelectedId(network));
            Assert.Equal("disk-a", SelectedId(storage));
            Assert.Single(changes);
            Assert.Equal("gpu-a", Assert.IsType<SpecificDeviceSelection>(changes[0].GpuDeviceSelection).DeviceId);
            Assert.Equal("network-a", Assert.IsType<SpecificDeviceSelection>(changes[0].NetworkDeviceSelection).DeviceId);
            Assert.Equal("disk-a", Assert.IsType<SpecificDeviceSelection>(changes[0].StorageDeviceSelection).DeviceId);

            // Provider recreation must not rebuild Settings; a later enumeration may still arrive.
            viewModel.UpdateGpuDevices([new DeviceDescriptor("gpu-a", "GPU A")]);
            viewModel.UpdateNetworkDevices([new DeviceDescriptor("network-a", "Network A")]);
            viewModel.UpdateStorageDevices([new DeviceDescriptor("disk-a", "Disk A")]);
            FlushBindings();
            Assert.Equal("gpu-a", SelectedId(gpu));
            Assert.Equal("network-a", SelectedId(network));
            Assert.Equal("disk-a", SelectedId(storage));
            Assert.Single(changes);
        });
    }

    [Fact]
    public void NetworkAndStorageSelections_AreIndependent_AndAutoCanBeChosenAgain()
    {
        RunOnSta(() =>
        {
            var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
            var gpu = CreateComboBox(viewModel, nameof(viewModel.GpuDevices), nameof(viewModel.SelectedGpuDevice));
            var network = CreateComboBox(viewModel, nameof(viewModel.NetworkDevices), nameof(viewModel.SelectedNetworkDevice));
            var storage = CreateComboBox(viewModel, nameof(viewModel.StorageDevices), nameof(viewModel.SelectedStorageDevice));
            viewModel.UpdateGpuDevices([new DeviceDescriptor("gpu-a", "GPU A")]);
            viewModel.UpdateNetworkDevices([new DeviceDescriptor("network-a", "Network A")]);
            viewModel.UpdateStorageDevices([new DeviceDescriptor("disk-a", "Disk A")]);
            FlushBindings();
            var changes = new List<OverlaySettings>();
            viewModel.SettingsChanged += changes.Add;

            network.SelectedItem = viewModel.NetworkDevices.Single(choice => choice.Id == "network-a");
            FlushBindings();
            Assert.Single(changes);
            viewModel.UpdateNetworkDevices([new DeviceDescriptor("network-a", "Network A")]);
            FlushBindings();
            Assert.Equal("network-a", SelectedId(network));
            AssertAuto(gpu);
            AssertSystem(storage);
            Assert.Single(changes);

            storage.SelectedItem = viewModel.StorageDevices.Single(choice => choice.Id == "disk-a");
            FlushBindings();
            Assert.Equal(2, changes.Count);
            viewModel.UpdateStorageDevices([new DeviceDescriptor("disk-a", "Disk A")]);
            FlushBindings();
            AssertAuto(gpu);
            Assert.Equal("network-a", SelectedId(network));
            Assert.Equal("disk-a", SelectedId(storage));
            Assert.Equal(2, changes.Count);

            network.SelectedItem = viewModel.NetworkDevices.Single(choice => choice.Id == DeviceChoice.AutoId);
            storage.SelectedItem = viewModel.StorageDevices.Single(choice => choice.Id == DeviceChoice.AutoId);
            FlushBindings();
            AssertAuto(network);
            AssertAuto(storage);
            Assert.Equal(4, changes.Count);
            Assert.IsType<AutomaticDeviceSelection>(changes[^1].NetworkDeviceSelection);
            Assert.IsType<AutomaticDeviceSelection>(changes[^1].StorageDeviceSelection);
        });
    }

    private static ComboBox CreateComboBox(SettingsViewModel viewModel, string itemsPath, string selectedPath)
    {
        var comboBox = new ComboBox { DataContext = viewModel, DisplayMemberPath = "DisplayName", IsSynchronizedWithCurrentItem = false };
        comboBox.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(itemsPath));
        comboBox.SetBinding(Selector.SelectedItemProperty, new Binding(selectedPath)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        FlushBindings();
        return comboBox;
    }

    private static void AssertAuto(ComboBox comboBox)
    {
        Assert.Equal(DeviceChoice.AutoId, SelectedId(comboBox));
        Assert.Equal("Auto", Assert.IsType<DeviceChoice>(comboBox.SelectedItem).DisplayName);
    }

    private static void AssertSystem(ComboBox comboBox)
    {
        Assert.Equal(DeviceChoice.SystemDriveId, SelectedId(comboBox));
        Assert.StartsWith("System (", Assert.IsType<DeviceChoice>(comboBox.SelectedItem).DisplayName);
    }

    private static string SelectedId(ComboBox comboBox) =>
        Assert.IsType<DeviceChoice>(comboBox.SelectedItem).Id;

    private static void FlushBindings()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
