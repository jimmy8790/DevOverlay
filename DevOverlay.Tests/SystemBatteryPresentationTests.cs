using System.IO;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Presentation;
using DevOverlay.Platform.Windows;
using DevOverlay.UI;
using Xunit;

namespace DevOverlay.Tests;

[Collection(WpfCollection.Name)]
public sealed class SystemBatteryPresentationTests
{
    [Theory]
    [InlineData(13320, "3h 42m")] [InlineData(3480, "58m")] [InlineData(0, "0m")] [InlineData(359940, "99h 59m")]
    public void RuntimeFormatterDoesNotShowSeconds(double seconds, string expected)
    {
        var metric = SystemBatteryMetricProvider.CreateSnapshots(new(true, 78, BatteryFlow.Discharging, -14.2, seconds));
        Assert.Equal(expected, MetricTextFormatter.FormatValue(metric.Single(item => item.Id == MetricId.BatteryRemaining)));
    }

    [Theory]
    [InlineData(1, 28.823, "+28.8W")] [InlineData(2, -14.2, "-14.2W")] [InlineData(1, -6.24, "-6.2W")]
    [InlineData(1, 0.04, "0.0W")] [InlineData(2, -0.04, "0.0W")] [InlineData(1, 999.9, "+999.9W")] [InlineData(2, -999.9, "-999.9W")]
    [InlineData(3, null, "AC")] [InlineData(1, null, "N/A")] [InlineData(2, null, "N/A")]
    public void PowerFieldIsSignedWattsWithoutDirectionLabel(int flow, double? watts, string expected)
    {
        var metric = SystemBatteryMetricProvider.CreateSnapshots(new(true, 78, (BatteryFlow)flow, watts, null))
            .Single(item => item.Id == MetricId.BatteryPower);
        Assert.Equal(string.Empty, MetricTextFormatter.GetPrefix(metric));
        Assert.Equal(expected, MetricTextFormatter.FormatValue(metric));
        Assert.Equal(expected, MetricTextFormatter.Format(metric));
        Assert.DoesNotContain("CHG", MetricTextFormatter.Format(metric));
        Assert.DoesNotContain("USE", MetricTextFormatter.Format(metric));
        Assert.DoesNotContain("-0.0", MetricTextFormatter.Format(metric));
        Assert.DoesNotContain("+-", MetricTextFormatter.Format(metric));
        Assert.DoesNotContain("+N/A", MetricTextFormatter.Format(metric));
    }

    [Fact]
    public void InactiveRuntimeShowsNotAvailableAndUnknownDischargingRuntimeIsUnavailable()
    {
        foreach (var flow in new[] { BatteryFlow.Ac, BatteryFlow.Unknown })
            Assert.Equal("N/A", MetricTextFormatter.FormatValue(SystemBatteryMetricProvider.CreateSnapshots(new(true, 78, flow, null, null)).Last()));
        Assert.Equal("N/A", MetricTextFormatter.FormatValue(SystemBatteryMetricProvider.CreateSnapshots(new(true, 78, BatteryFlow.Discharging, null, null)).Last()));
        Assert.Equal("N/A", MetricTextFormatter.FormatValue(SystemBatteryMetricProvider.CreateSnapshots(new(true, 78, BatteryFlow.Charging, null, null)).Last()));
    }

    [Fact]
    public void RenderedHudWidthIsStableAcrossChargingDischargingAcAndUnavailable()
    {
        RunSta(() =>
        {
            var model = new OverlayViewModel(OverlaySettings.CreateDefault(), Dispatcher.CurrentDispatcher);
            var window = new OverlayWindow(model, new OverlayPositioningService());
            var content = (FrameworkElement)window.Content;
            try
            {
                double? width = null;
                foreach (var reading in new[] {
                    new SystemBatteryReading(true, 78, BatteryFlow.Discharging, -14.2, 13320, LeftSource: BatteryLeftSource.Windows),
                    new SystemBatteryReading(true, 78, BatteryFlow.Discharging, -21.3, 7098, PowerSource: BatteryPowerSource.Native, LeftSource: BatteryLeftSource.NativeDerived),
                    new SystemBatteryReading(true, 78, BatteryFlow.Discharging, -21.3, 7098, PowerSource: BatteryPowerSource.Estimated, LeftSource: BatteryLeftSource.EstimatedDerived),
                    new SystemBatteryReading(true, 78, BatteryFlow.Discharging, null, null),
                    new SystemBatteryReading(true, 64, BatteryFlow.Charging, 28.8, null, 3120),
                    new SystemBatteryReading(true, 64, BatteryFlow.Charging, 10.8, null, null, BatteryPowerSource.Estimated),
                    new SystemBatteryReading(true, 64, BatteryFlow.Charging, 28.8, null, 3120, BatteryPowerSource.Native, FullSource: BatteryFullSource.NativeDerived),
                    new SystemBatteryReading(true, 64, BatteryFlow.Charging, 33.8, null, 1904, BatteryPowerSource.Estimated, FullSource: BatteryFullSource.EstimatedDerived),
                    new SystemBatteryReading(true, 5, BatteryFlow.Charging, 1.2, null, 359940, BatteryPowerSource.Estimated, FullSource: BatteryFullSource.EstimatedDerived),
                    new SystemBatteryReading(true, 69, BatteryFlow.Charging, -6.2, null, null, BatteryPowerSource.Estimated),
                    new SystemBatteryReading(true, 69, BatteryFlow.Charging, -19.4, null, null, BatteryPowerSource.Native),
                    new SystemBatteryReading(true, 100, BatteryFlow.Charging, 999.9, null, 359940, BatteryPowerSource.Native, FullSource: BatteryFullSource.NativeDerived),
                    new SystemBatteryReading(true, 100, BatteryFlow.Discharging, -999.9, 359940, PowerSource: BatteryPowerSource.Native, LeftSource: BatteryLeftSource.Windows),
                    new SystemBatteryReading(true, 78, BatteryFlow.Discharging, -18.4, 12600, null, BatteryPowerSource.Estimated),
                    new SystemBatteryReading(true, 100, BatteryFlow.Ac, null, null),
                    new SystemBatteryReading(true, null, BatteryFlow.Unknown, null, null) })
                {
                    model.ApplyMetrics(SystemBatteryMetricProvider.CreateSnapshots(reading));
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    content.Arrange(new Rect(new Point(), content.DesiredSize)); content.UpdateLayout();
                    var group = Assert.Single(model.Groups);
                    Assert.Equal("BAT", group.Title); Assert.Equal(3, group.Items.Count);
                    if (width.HasValue) Assert.Equal(width.Value, content.DesiredSize.Width);
                    width = content.DesiredSize.Width;
                }
                Assert.True(width > 100);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(MetricId.BatteryCharge, "100%")]
    [InlineData(MetricId.BatteryPower, "+999.9W")]
    [InlineData(MetricId.BatteryPower, "-999.9W")]
    [InlineData(MetricId.BatteryPower, "AC")]
    [InlineData(MetricId.BatteryPower, "N/A")]
    [InlineData(MetricId.BatteryRemaining, "99h 59m")]
    public void RepresentativeValuesFitActualWpfValueFields(MetricId id, string text)
    {
        RunSta(() =>
        {
            var measured = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12, Brushes.White, 1.0);
            Assert.True(MetricDisplayLayout.GetTextWidth(id) >= measured.WidthIncludingTrailingWhitespace);
            Assert.True(double.IsNaN(MetricDisplayLayout.GetPrefixWidth(MetricId.BatteryPower))); // 방향 라벨 열이 없다.
            Assert.True(double.IsNaN(MetricDisplayLayout.GetPrefixWidth(MetricId.CpuUtilization)));
        });
    }

    [Fact]
    public void MissingBatteryHidesOnlyBatAndReappearanceRestoresItsPosition()
    {
        RunSta(() =>
        {
            var settings = OverlaySettings.CreateDefault() with { GroupOrder = [MetricCategory.Battery, MetricCategory.Cpu] };
            var model = new OverlayViewModel(settings, Dispatcher.CurrentDispatcher);
            var cpu = new MetricSnapshot(MetricId.CpuUtilization, MetricCategory.Cpu, "CPU", 18, "%", true, DateTimeOffset.UtcNow);
            model.ApplyMetrics([cpu]);
            var originalCpu = Assert.Single(model.Groups);
            var available = SystemBatteryMetricProvider.CreateSnapshots(new(true, 78, BatteryFlow.Discharging, -14.2, 13320));
            model.ApplyMetrics(available); Assert.Equal(MetricCategory.Battery, model.Groups[0].Category);
            model.ApplyMetrics(SystemBatteryMetricProvider.CreateSnapshots(new(false, null, BatteryFlow.Unknown, null, null)));
            Assert.Same(originalCpu, Assert.Single(model.Groups)); Assert.Equal("18%", originalCpu.Items[0].ValueText);
            model.MarkCategoryUnavailable(MetricCategory.Battery); Assert.Single(model.Groups);
            model.ApplyMetrics(available); Assert.Equal(MetricCategory.Battery, model.Groups[0].Category);
            Assert.Same(originalCpu, model.Groups[1]); Assert.Equal(settings.Position, model.Settings.Position);
        });
    }

    [Fact]
    public void SettingsCheckboxHidesReenablesAndPersistsOrderWithoutChangingOtherSettings()
    {
        RunSta(() =>
        {
            var settings = OverlaySettings.CreateDefault() with
            {
                Position = OverlayPosition.CreateCustom(40, 50),
                GroupOrder = [MetricCategory.Battery, MetricCategory.Network, MetricCategory.Cpu],
                RefreshIntervalMs = 333
            };
            var model = new OverlayViewModel(settings, Dispatcher.CurrentDispatcher);
            model.ApplyMetrics(SystemBatteryMetricProvider.CreateSnapshots(new(true, 78, BatteryFlow.Discharging, -14.2, 13320)));
            var viewModel = new SettingsViewModel(settings);
            var window = new SettingsWindow(viewModel);
            try
            {
                var checkbox = Assert.IsType<CheckBox>(window.FindName("ShowBatteryCheckBox"));
                Assert.Equal(nameof(SettingsViewModel.BatteryEnabled), BindingOperations.GetBinding(checkbox, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!.Path.Path);
                OverlaySettings? changed = null;
                viewModel.SettingsChanged += value => { changed = value; model.ApplySettings(value); };
                viewModel.BatteryEnabled = false;
                Assert.Empty(model.Groups); Assert.DoesNotContain(MetricCategory.Battery, changed!.EnabledGroups);
                viewModel.BatteryEnabled = true; Assert.Single(model.Groups);
                Assert.Equal(MetricCategory.Battery, changed!.GroupOrder[0]);
                Assert.Equal(settings.Position, changed.Position); Assert.Equal(settings.Appearance, changed.Appearance);
                Assert.Equal(333, changed.RefreshIntervalMs); Assert.Equal(settings.Hotkey, changed.Hotkey);
                Assert.Equal(settings.GpuDeviceSelection, changed.GpuDeviceSelection);
                Assert.Equal(settings.NetworkDeviceSelection, changed.NetworkDeviceSelection);
                Assert.Equal(settings.StorageDeviceSelection, changed.StorageDeviceSelection);
                Assert.Equal("Battery", viewModel.GroupOrderChoices[0].Label);
                viewModel.MoveGroup(MetricCategory.Battery, 1);
                Assert.Equal(MetricCategory.Battery, changed.GroupOrder[1]);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void VisibilityAndOrderRoundTripWhileLegacySettingsEnableBattery(bool enabled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DevOverlay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(path, """{"GroupOrder":["Network","Cpu"],"CpuGroupEnabled":false}""");
            var store = new OverlaySettingsStore(path);
            var legacy = store.Load();
            Assert.Contains(MetricCategory.Battery, legacy.EnabledGroups);
            Assert.Equal(MetricCategory.Battery, legacy.GroupOrder.Last());
            Assert.DoesNotContain(MetricCategory.Cpu, legacy.EnabledGroups);
            var groups = new HashSet<MetricCategory>(legacy.EnabledGroups);
            if (!enabled) groups.Remove(MetricCategory.Battery);
            store.Save(legacy with { EnabledGroups = groups, GroupOrder = [MetricCategory.Battery, MetricCategory.Network, MetricCategory.Cpu] });
            var loaded = store.Load();
            Assert.Equal(enabled, loaded.EnabledGroups.Contains(MetricCategory.Battery));
            Assert.Equal(MetricCategory.Battery, loaded.GroupOrder.First());
            Assert.Contains(MetricId.BatteryCharge, loaded.EnabledMetrics);
            Assert.Contains(MetricId.BatteryPower, loaded.EnabledMetrics);
            Assert.Contains(MetricId.BatteryRemaining, loaded.EnabledMetrics);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public void OldEnumValuesRemainIntactAndNormalRuntimeIncludesBatteryProvider()
    {
        Assert.Equal(7, (int)MetricCategory.PeripheralBattery);
        Assert.Equal(24, (int)MetricId.PeripheralBattery);
        Assert.Equal((int)MetricCategory.PeripheralBattery + 1, (int)MetricCategory.Battery);
        Assert.Equal((int)MetricId.PeripheralBattery + 1, (int)MetricId.BatteryCharge);
        Assert.Equal((int)MetricId.BatteryCharge + 1, (int)MetricId.BatteryPower);
        Assert.Contains(App.CreateRuntimeProviders(OverlaySettings.CreateDefault()), provider => provider is SystemBatteryMetricProvider);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
