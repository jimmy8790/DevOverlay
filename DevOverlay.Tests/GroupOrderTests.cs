using System.IO;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Presentation;
using DevOverlay.Platform.Windows;
using DevOverlay.UI;
using Xunit;

namespace DevOverlay.Tests;

public sealed class GroupOrderTests
{
    [Fact]
    public void ChangingOrderReordersOneOverlayWithoutRecreatingGroups()
    {
        var settings = OverlaySettings.CreateDefault();
        var overlay = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        overlay.ApplyMetrics([
            Metric(MetricId.CpuUtilization, MetricCategory.Cpu),
            Metric(MetricId.GpuUtilization, MetricCategory.Gpu),
            Metric(MetricId.StorageRead, MetricCategory.Storage),
            Metric(MetricId.NetworkDownload, MetricCategory.Network)
        ]);
        var cpu = overlay.Groups.Single(group => group.Category == MetricCategory.Cpu);
        var gpu = overlay.Groups.Single(group => group.Category == MetricCategory.Gpu);
        var settingsViewModel = new SettingsViewModel(settings);
        OverlaySettings? changed = null;
        settingsViewModel.SettingsChanged += next => { changed = next; overlay.ApplySettings(next); };

        settingsViewModel.MoveGroup(MetricCategory.Network, -1);
        settingsViewModel.MoveGroup(MetricCategory.Network, -1);
        settingsViewModel.MoveGroup(MetricCategory.Network, -1);
        settingsViewModel.MoveGroup(MetricCategory.Network, -1);
        settingsViewModel.MoveGroup(MetricCategory.Network, -1);

        Assert.Equal([MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Storage],
            overlay.Groups.Select(group => group.Category));
        Assert.Same(cpu, overlay.Groups[1]);
        Assert.Same(gpu, overlay.Groups[2]);
        Assert.Equal(settings.Position, changed!.Position);
        Assert.Equal(3, overlay.Groups.Count(group => group.HasFollowingGroup));

        settingsViewModel.MoveGroup(MetricCategory.Cpu, -1);
        Assert.Equal(MetricCategory.Cpu, overlay.Groups[0].Category);
    }

    [Fact]
    public void HiddenGroupKeepsItsOrderWhenReenabled()
    {
        var settings = OverlaySettings.CreateDefault() with
        {
            GroupOrder = [MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Storage]
        };
        var overlay = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        overlay.ApplyMetrics([
            Metric(MetricId.CpuUtilization, MetricCategory.Cpu),
            Metric(MetricId.GpuUtilization, MetricCategory.Gpu),
            Metric(MetricId.NetworkDownload, MetricCategory.Network)
        ]);
        overlay.ApplySettings(settings with
        {
            EnabledGroups = new HashSet<MetricCategory>(settings.EnabledGroups.Where(group => group != MetricCategory.Network))
        });
        Assert.Equal([MetricCategory.Cpu, MetricCategory.Gpu], overlay.Groups.Select(group => group.Category));

        overlay.ApplySettings(settings);
        Assert.Equal([MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Gpu],
            overlay.Groups.Select(group => group.Category));
    }

    [Fact]
    public void OrderPersistsAndMissingOrUnknownEntriesAreSafe()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DevOverlay-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var store = new OverlaySettingsStore(path);
        try
        {
            var settings = OverlaySettings.CreateDefault() with
            {
                GroupOrder = [MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Storage, MetricCategory.Gpu],
                Position = OverlayPosition.CreateCustom(40, 50),
                Appearance = new OverlayAppearance("#010203", .5, "#040506", "#070809")
            };
            store.Save(settings);
            var loaded = store.Load();
            Assert.Equal([MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Storage,
                MetricCategory.Gpu, MetricCategory.Frame, MetricCategory.Latency, MetricCategory.AiUsage], loaded.GroupOrder);
            Assert.Equal(settings.Position, loaded.Position);
            Assert.Equal(settings.Appearance, loaded.Appearance);

            File.WriteAllText(path, """{"GroupOrder":["Network","FutureGroup","Network","Cpu"]}""");
            Assert.Equal([MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Frame, MetricCategory.Latency,
                MetricCategory.Gpu, MetricCategory.Storage, MetricCategory.AiUsage],
                store.Load().GroupOrder);
            File.WriteAllText(path, "{}");
            Assert.Equal(OverlaySettings.DefaultGroupOrder, store.Load().GroupOrder);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void LegacyOrderInsertsLatencyImmediatelyAfterFpsWithoutReorderingOtherGroups()
    {
        var migrated = OverlaySettings.NormalizeGroupOrder(
            [MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Frame, MetricCategory.Gpu, MetricCategory.Storage]);
        Assert.Equal([MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Frame, MetricCategory.Latency,
            MetricCategory.Gpu, MetricCategory.Storage, MetricCategory.AiUsage], migrated);
    }

    [Fact]
    public void HidingFpsDoesNotHideLatencyAndPreservesItsSavedPosition()
    {
        var settings = OverlaySettings.CreateDefault() with
        {
            GroupOrder = [MetricCategory.Network, MetricCategory.Latency, MetricCategory.Frame,
                MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Storage]
        };
        var overlay = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        overlay.ApplyMetrics([
            Metric(MetricId.FramesPerSecond, MetricCategory.Frame),
            Metric(MetricId.Latency, MetricCategory.Latency)]);
        overlay.ApplySettings(settings with
        {
            EnabledGroups = new HashSet<MetricCategory>(settings.EnabledGroups.Where(group => group != MetricCategory.Frame))
        });

        Assert.Equal([MetricCategory.Latency], overlay.Groups.Select(group => group.Category));
        overlay.ApplySettings(settings);
        Assert.Equal([MetricCategory.Latency, MetricCategory.Frame], overlay.Groups.Select(group => group.Category));
    }

    [Fact]
    public void ResetPositionLeavesGroupOrderAndAppearanceAlone()
    {
        var settings = OverlaySettings.CreateDefault() with
        {
            Position = OverlayPosition.CreateCustom(40, 50),
            GroupOrder = [MetricCategory.Network, MetricCategory.Cpu, MetricCategory.Gpu,
                MetricCategory.Frame, MetricCategory.Latency, MetricCategory.Storage],
            Appearance = new OverlayAppearance("#010203", .5, "#040506", "#070809")
        };
        var viewModel = new SettingsViewModel(settings);
        OverlaySettings? changed = null;
        viewModel.SettingsChanged += next => changed = next;
        viewModel.ResetPosition();
        Assert.Equal(OverlayPositionMode.AutoTopRight, changed!.Position.Mode);
        Assert.Equal(OverlayPosition.CreateDefault(), changed.Position);
        Assert.Equal(OverlaySettings.NormalizeGroupOrder(settings.GroupOrder), changed.GroupOrder);
        Assert.Equal(settings.Appearance, changed.Appearance);
    }

    [Fact]
    public void ResetMovesTheShownCombinedWindowImmediately()
    {
        RunOnSta(() =>
        {
            var settings = OverlaySettings.CreateDefault() with { Position = OverlayPosition.CreateCustom(25, 25) };
            var overlay = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            overlay.ApplyMetrics([Metric(MetricId.CpuUtilization, MetricCategory.Cpu)]);
            var window = new OverlayWindow(overlay, new OverlayPositioningService());
            try
            {
                window.Show();
                FlushDispatcher();
                window.ReapplyPosition();
                FlushDispatcher();
                var customLeft = window.Left;

                var editor = new SettingsViewModel(settings);
                OverlaySettings? saved = null;
                editor.SettingsChanged += next =>
                {
                    saved = next;
                    overlay.ApplySettings(next);
                    window.ApplySettings(next.Position, next.Appearance);
                };
                editor.ResetPosition();
                FlushDispatcher();

                Assert.Equal(OverlayPositionMode.AutoTopRight, saved!.Position.Mode);
                Assert.True(window.Left > customLeft + 50,
                    $"Reset did not move the overlay: custom={customLeft}, reset={window.Left}.");
            }
            finally { window.Close(); }
        });
    }

    private static void FlushDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
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
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static MetricSnapshot Metric(MetricId id, MetricCategory category) =>
        new(id, category, id.ToString(), 1, "%", true, DateTimeOffset.UtcNow);
}
