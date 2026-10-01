using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using DevOverlay.Configuration;
using DevOverlay.Metrics.Windows;
using DevOverlay.Presentation;
using DevOverlay.UI;
using Xunit;
using Xunit.Abstractions;

namespace DevOverlay.Tests;

public sealed class SettingsHardwareDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    public void ActualSavedDeviceSelections_ResolveInShownSettingsWindow_WhenExplicitlyRequested()
    {
        if (Environment.GetEnvironmentVariable("DEVOVERLAY_SETTINGS_DIAGNOSTIC") != "1") return;
        RunOnSta(() =>
        {
            var settings = new OverlaySettingsStore().Load();
            var viewModel = new SettingsViewModel(settings);
            var window = new SettingsWindow(viewModel);
            try
            {
                window.Show();
                FlushBindings();
                WriteSelections(window, "initial");
                var gpu = new NvidiaGpuMetricProvider(settings.GpuDeviceSelection);
                var network = new NetworkThroughputMetricProvider(settings.NetworkDeviceSelection);
                var storage = new PhysicalDiskThroughputMetricProvider(settings.StorageDeviceSelection);
                try
                {
                    viewModel.UpdateGpuDevices(gpu.GetAvailableDevices());
                    viewModel.UpdateNetworkDevices(network.GetAvailableDevices());
                    viewModel.UpdateStorageDevices(storage.GetAvailableDevices());
                    FlushBindings();
                    WriteSelections(window, "enumerated");
                }
                finally
                {
                    gpu.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    storage.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            finally { window.Close(); }
        });
    }

    private void WriteSelections(SettingsWindow window, string phase)
    {
        foreach (var name in new[] { "GpuDeviceComboBox", "NetworkDeviceComboBox", "StorageDeviceComboBox" })
        {
            var combo = Assert.IsType<ComboBox>(window.FindName(name));
            output.WriteLine($"{phase} {name}: selectedValue={combo.SelectedValue ?? "null"}, " +
                             $"selectedItem={(combo.SelectedItem as DeviceChoice)?.DisplayName ?? "null"}, choices={combo.Items.Count}");
            Assert.IsType<DeviceChoice>(combo.SelectedItem);
        }
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
}
