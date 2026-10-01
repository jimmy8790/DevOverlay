using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using Xunit;
using Xunit.Abstractions;
using System.Net.NetworkInformation;
using System.IO;
using DevOverlay.Presentation;

namespace DevOverlay.Tests;

public sealed class NetworkTodayAndCpuSensorTests(ITestOutputHelper output)
{
    [Fact]
    public void DailyTotalAddsReceivedAndSentAcrossPhysicalAdapters()
    {
        var wifi = Guid.NewGuid();
        var ethernet = Guid.NewGuid();
        var records = new[]
        {
            new NetworkTodayUsage(wifi, 71, 40, 10),
            new NetworkTodayUsage(ethernet, 6, 20, 5),
            new NetworkTodayUsage(Guid.NewGuid(), 131, 9999, 9999),
            new NetworkTodayUsage(Guid.NewGuid(), 24, 9999, 9999)
        };

        HashSet<Guid> physical = [wifi, ethernet];
        Assert.Equal(75UL, NetworkTodayUsageCalculator.Sum(records, DeviceSelection.Auto, physical));
        Assert.Equal(50UL, NetworkTodayUsageCalculator.Sum(records, DeviceSelection.Specific(wifi.ToString()), physical));
        Assert.Null(NetworkTodayUsageCalculator.Sum(records, DeviceSelection.Specific(Guid.NewGuid().ToString()), physical));
        Assert.Null(NetworkTodayUsageCalculator.Sum(records, DeviceSelection.Auto, new HashSet<Guid>()));
    }

    [Fact]
    public void NewLocalDayGetsNewMidnight()
    {
        var first = new DateTimeOffset(2026, 9, 29, 23, 59, 59, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 29)));
        var second = first.AddSeconds(2);
        Assert.Equal(new DateTime(2026, 9, 29), NetworkTodayMetricProvider.GetLocalMidnight(first).Date);
        Assert.Equal(new DateTime(2026, 9, 30), NetworkTodayMetricProvider.GetLocalMidnight(second).Date);
    }

    [Fact]
    public void NewMetricsHaveStableWidthsAndVisibility()
    {
        var settings = OverlaySettings.CreateDefault();
        var viewModel = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        viewModel.ApplyMetrics([
            new(MetricId.CpuTemperature, MetricCategory.Cpu, "temp", null, "°C", false, DateTimeOffset.Now),
            new(MetricId.CpuPower, MetricCategory.Cpu, "power", 22, "W", true, DateTimeOffset.Now),
            NetworkTodayFormatter.CreateSnapshot(882900000, DateTimeOffset.Now)
        ]);
        var cpu = Assert.Single(viewModel.Groups, group => group.Category == MetricCategory.Cpu);
        var today = Assert.Single(viewModel.Groups, group => group.Category == MetricCategory.Network).Items.Single();
        var width = today.DisplayWidth;
        viewModel.ApplyMetrics([NetworkTodayFormatter.CreateSnapshot(107374182400, DateTimeOffset.Now)]);
        Assert.Equal(width, today.DisplayWidth);
        Assert.True(cpu.HasFollowingGroup);
        Assert.False(viewModel.Groups.Last().HasFollowingGroup);

        var hidden = settings with { EnabledMetrics = settings.EnabledMetrics.Except([
            MetricId.NetworkTodayTotal, MetricId.NetworkDownload, MetricId.NetworkUpload]).ToHashSet() };
        viewModel.ApplySettings(hidden);
        Assert.DoesNotContain(viewModel.Groups, group => group.Category == MetricCategory.Network);
    }

    [Theory]
    [InlineData(0UL, "DAY 0B")]
    [InlineData(882900000UL, "DAY 842MB")]
    [InlineData(5153960755UL, "DAY 4.8GB")]
    public void DailyTotalUsesCompactNonRateUnits(ulong bytes, string expected)
    {
        var snapshot = NetworkTodayFormatter.CreateSnapshot(bytes, DateTimeOffset.Now);
        Assert.Equal(expected, new DevOverlay.Presentation.MetricItemViewModel(snapshot).Text);
    }

    [Fact]
    public void UnavailableDailyTotalKeepsItsLabel()
    {
        var item = new MetricItemViewModel(NetworkTodayFormatter.CreateSnapshot(null, DateTimeOffset.Now));
        Assert.Equal("DAY N/A", item.Text);
    }

    [Fact]
    public void InvalidCpuReadingsRemainUnavailable()
    {
        Assert.True(CpuSensorSelection.IsPackageSensorName("CPU Package"));
        Assert.False(CpuSensorSelection.IsPackageSensorName("CPU Core #1"));
        Assert.Null(CpuSensorSelection.ValidTemperature(double.NaN));
        Assert.Null(CpuSensorSelection.ValidTemperature(double.PositiveInfinity));
        Assert.Null(CpuSensorSelection.ValidTemperature(null));
        Assert.Null(CpuSensorSelection.ValidPower(-1));
        Assert.Null(CpuSensorSelection.ValidPower(0));
        Assert.Null(CpuSensorSelection.ValidPower(double.PositiveInfinity));
    }

    [Fact]
    public void NewMetricPreferencesRoundTripAndOldSettingsDefaultOn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DevOverlay-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new OverlaySettingsStore(path);
            var adapterId = Guid.NewGuid().ToString("B");
            File.WriteAllText(path, $"{{\"CpuUsageEnabled\":false,\"NetworkDeviceId\":\"{adapterId}\"}}");
            var old = store.Load();
            Assert.True(old.EnabledMetrics.Contains(MetricId.CpuTemperature));
            Assert.True(old.EnabledMetrics.Contains(MetricId.CpuPower));
            Assert.True(old.EnabledMetrics.Contains(MetricId.NetworkTodayTotal));
            Assert.False(old.EnabledMetrics.Contains(MetricId.CpuUtilization));
            Assert.IsType<SpecificDeviceSelection>(old.NetworkDeviceSelection);

            var changed = old with { EnabledMetrics = new HashSet<MetricId>(old.EnabledMetrics)
                .Except([MetricId.CpuTemperature, MetricId.CpuPower, MetricId.NetworkTodayTotal]).ToHashSet() };
            store.Save(changed);
            var loaded = store.Load();
            Assert.DoesNotContain(MetricId.CpuTemperature, loaded.EnabledMetrics);
            Assert.DoesNotContain(MetricId.CpuPower, loaded.EnabledMetrics);
            Assert.DoesNotContain(MetricId.NetworkTodayTotal, loaded.EnabledMetrics);
            Assert.IsType<SpecificDeviceSelection>(loaded.NetworkDeviceSelection);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task HardwareProbe()
    {
        if (Environment.GetEnvironmentVariable("DEVOVERLAY_HARDWARE_PROBE") != "1") return;
        var cpuUsage = new CpuUtilizationMetricProvider();
        await cpuUsage.CollectAsync(CancellationToken.None);
        await Task.Delay(1100);
        var usage = await cpuUsage.CollectAsync(CancellationToken.None);
        foreach (var metric in usage) output.WriteLine($"{metric.Id}: available={metric.IsAvailable}, value={metric.Value}{metric.Unit}");
        await using var cpu = new CpuPackageSensorMetricProvider();
        var cpuMetrics = await cpu.CollectAsync(CancellationToken.None);
        foreach (var metric in cpuMetrics) output.WriteLine($"{metric.Id}: available={metric.IsAvailable}, value={metric.Value}{metric.Unit}");

        var network = new NetworkTodayMetricProvider(DeviceSelection.Auto);
        var networkMetrics = await network.CollectAsync(CancellationToken.None);
        foreach (var metric in networkMetrics) output.WriteLine($"{metric.Id}: available={metric.IsAvailable}, value={metric.Value}{metric.Unit}");
        output.WriteLine($"Auto raw bytes: {await network.QueryTodayBytesAsync(DateTimeOffset.Now, CancellationToken.None)}");
        var wifi = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(adapter =>
            adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && adapter.OperationalStatus == OperationalStatus.Up);
        if (wifi is not null)
        {
            var specific = new NetworkTodayMetricProvider(DeviceSelection.Specific(wifi.Id));
            output.WriteLine($"Specific Wi-Fi raw bytes: {await specific.QueryTodayBytesAsync(DateTimeOffset.Now, CancellationToken.None)}");
        }
        Assert.Contains(cpuMetrics, metric => metric.Id == MetricId.CpuTemperature);
        Assert.Contains(cpuMetrics, metric => metric.Id == MetricId.CpuPower);
        Assert.Contains(networkMetrics, metric => metric.Id == MetricId.NetworkTodayTotal);
    }
}
