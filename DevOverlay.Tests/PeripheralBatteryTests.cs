using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Peripherals;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class PeripheralBatteryTests
{
    private const string Container = "11111111-1111-4111-8111-111111111111";
    private static PeripheralObservation Row(string iface = "mouse", string? container = Container,
        double? percent = 82, PeripheralBatterySource source = PeripheralBatterySource.WindowsProperty,
        bool connected = true) => new(iface, container, "Wireless mouse", PeripheralType.Mouse,
            connected, percent, null, source, DateTimeOffset.UtcNow, "Ready");
    private static PeripheralBatteryReading Reading(double? percent = 82, bool connected = true) =>
        PeripheralBatteryMerge.Merge([Row(percent: percent, connected: connected)])[0];

    [Theory]
    [InlineData(-1, false)] [InlineData(101, false)] [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)] [InlineData(0, true)] [InlineData(100, true)]
    public void NumericBatteryRange(double value, bool expected) => Assert.Equal(expected, Reading(value).ValidPercentage.HasValue);

    [Fact]
    public void PhysicalInterfacesMergeWithPreferredSourceAndFallback()
    {
        var rows = new[] { Row("hid", percent: 82), Row("bluetooth", percent: 80, source: PeripheralBatterySource.BluetoothGatt) };
        var selected = Assert.Single(PeripheralBatteryMerge.Merge(rows));
        Assert.Equal(82, selected.Percentage); Assert.Equal(PeripheralBatterySource.WindowsProperty, selected.Source);
        selected = Assert.Single(PeripheralBatteryMerge.Merge([rows[0] with { Percentage = null }, rows[1]]));
        Assert.Equal(80, selected.Percentage); Assert.Equal(PeripheralBatterySource.BluetoothGatt, selected.Source);
        selected = Assert.Single(PeripheralBatteryMerge.Merge([rows[0] with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3) }, rows[1]]));
        Assert.Equal(80, selected.Percentage);
    }
    [Fact]
    public void DifferentContainersAndUnidentifiedInterfacesNeverMergeByName()
    {
        Assert.Equal(2, PeripheralBatteryMerge.Merge([Row(), Row("other", Guid.NewGuid().ToString())]).Count);
        Assert.Equal(2, PeripheralBatteryMerge.Merge([Row("a", null), Row("b", null)]).Count);
        Assert.Equal(Row().Identity, Row("other-interface", Container.ToUpperInvariant()).Identity);
        Assert.DoesNotContain(Container, Row().Identity, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void StaleAndDisconnectedNeverBecomeValidZero()
    {
        Assert.Null(Reading(null).ValidPercentage); Assert.Null(Reading(82, false).ValidPercentage);
        Assert.Null((Reading() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3) }).ValidPercentage);
    }
    [Theory]
    [InlineData(PeripheralType.Mouse, "MSE")] [InlineData(PeripheralType.Keyboard, "KB")]
    [InlineData(PeripheralType.Headset, "HS")] [InlineData(PeripheralType.Controller, "PAD")]
    [InlineData(PeripheralType.Earbuds, "BUD")] [InlineData(PeripheralType.Other, "DEV")]
    public void CompactDefaultLabels(PeripheralType type, string label) => Assert.Equal(label, PeripheralBatteryMerge.DefaultLabel(type));

    [Fact]
    public void DuplicateLabelsStayStableAcrossDisconnectAndDiscoveryOrder()
    {
        var first = Reading(); var second = first with { Identity = "another" };
        var saved = PeripheralBatteryMerge.AddPreferences([], [first, second]);
        Assert.Equal(2, saved.Select(item => item.DefaultLabel).Distinct().Count());
        Assert.Equal(saved, PeripheralBatteryMerge.AddPreferences(saved, [second]));
        Assert.Equal(saved, PeripheralBatteryMerge.AddPreferences([], [second, first]));
        var keyboard = first with { Identity = "keyboard", Type = PeripheralType.Keyboard };
        Assert.Equal(PeripheralType.Keyboard, PeripheralBatteryMerge.AddPreferences(saved, [keyboard]).Last().Type);
    }
    [Fact]
    public void PreferencesPersistAndBlankNamesFallBack()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevOverlay-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var preference = PeripheralBatteryMerge.AddPreferences([], [Reading()])[0] with { CustomName = "  G502  ", Show = false };
            var store = new OverlaySettingsStore(path);
            store.Save(OverlaySettings.CreateDefault() with { PeripheralDevices = [preference], PeripheralBatteriesEnabled = false });
            var loaded = store.Load(); var actual = Assert.Single(loaded.PeripheralDevices);
            Assert.Equal("G502", actual.CustomName); Assert.False(actual.Show); Assert.False(loaded.PeripheralBatteriesEnabled);
            Assert.Equal("MSE", (actual with { CustomName = " " }).HudLabel);
            Assert.Equal(12, PeripheralPreference.NormalizeName("abcdefghijklmnop").Length);
            Assert.Equal("글자12", PeripheralPreference.NormalizeName(" 글자\n12 "));
            var settings = new SettingsViewModel(loaded);
            settings.UpdatePeripheralDevices([], loaded.PeripheralDevices);
            Assert.Contains("Disconnected", settings.PeripheralDevices[0].StatusText);
            OverlaySettings? changed = null; settings.SettingsChanged += value => changed = value;
            settings.PeripheralDevices[0].Show = true;
            Assert.True(Assert.Single(changed!.PeripheralDevices).Show);
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!); }
    }
    [Fact]
    public void HudPairsRemainStableAcrossPercentageAndVisibilityChanges()
    {
        var reading = Reading(); var prefs = PeripheralBatteryMerge.AddPreferences([], [reading]);
        var settings = OverlaySettings.CreateDefault() with { PeripheralDevices = prefs };
        var overlay = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        overlay.ApplyPeripheralBatteries([reading]);
        var group = Assert.Single(overlay.Groups); var item = Assert.Single(group.Items);
        Assert.Empty(group.Title); Assert.Equal("MSE 82%", item.Text); var width = item.DisplayWidth;
        foreach (var value in new double?[] { 100, 99, 8, null })
        { overlay.ApplyPeripheralBatteries([reading with { Percentage = value }]); Assert.Same(item, group.Items[0]); Assert.Equal(width, item.DisplayWidth); }
        Assert.Equal("MSE N/A", item.Text);
        overlay.ApplyMetrics([new(MetricId.CpuUtilization, MetricCategory.Cpu, "CPU", 18, "%", true, DateTimeOffset.UtcNow)]);
        Assert.Equal(1, overlay.Groups.Count(entry => entry.HasFollowingGroup));
        overlay.ApplySettings(settings with { PeripheralBatteriesEnabled = false }); Assert.Single(overlay.Groups);
        overlay.ApplySettings(settings); Assert.Equal(2, overlay.Groups.Count); Assert.False(overlay.Groups[^1].HasFollowingGroup);
    }
    [Fact]
    public void MultipleHudDevicesKeepAutomaticOrderAndCustomNames()
    {
        var mouse = Reading(); var keyboard = mouse with { Identity = "keyboard", Type = PeripheralType.Keyboard };
        var prefs = PeripheralBatteryMerge.AddPreferences([], [keyboard, mouse])
            .Select(item => item with { CustomName = item.Type == PeripheralType.Mouse ? "G502" : "Q1" }).ToArray();
        var settings = OverlaySettings.CreateDefault() with { PeripheralDevices = prefs };
        var overlay = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        overlay.ApplyPeripheralBatteries([keyboard, mouse]);
        var group = Assert.Single(overlay.Groups);
        Assert.Equal(new[] { "G502 82%", "Q1 82%" }, group.Items.Select(item => item.Text));
        overlay.ApplySettings(settings with { PeripheralDevices = [prefs[0] with { Show = false }, prefs[1]] });
        Assert.Equal("Q1 82%", Assert.Single(group.Items).Text);
        var viewModel = new SettingsViewModel(settings);
        viewModel.UpdatePeripheralDevices([keyboard, mouse], prefs);
        Assert.Equal(mouse.Identity, viewModel.PeripheralDevices[0].Identity);
        viewModel.UpdatePeripheralDevices([], []); Assert.Empty(viewModel.PeripheralDevices);
    }
    [Fact]
    public void CompositeContainerTypeIsConservativeAndStaleSourcesStayUnavailable()
    {
        var merged = Assert.Single(PeripheralBatteryMerge.Merge([Row(), Row("keyboard") with { Type = PeripheralType.Keyboard }]));
        Assert.Equal(PeripheralType.Other, merged.Type);
        merged = Assert.Single(PeripheralBatteryMerge.Merge([Row() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3) }]));
        Assert.Null(merged.Percentage);
        Assert.False(Assert.Single(PeripheralBatteryMerge.AddPreferences([], [merged])).Show);
    }
    [Fact]
    public async Task BackendFailureAndTimeoutDoNotDiscardHealthyDevices()
    {
        await using var service = new PeripheralBatteryService([new FakeBackend(_ => throw new InvalidOperationException()),
            new FakeBackend(_ => throw new OperationCanceledException()), new FakeBackend(_ => Task.FromResult<IReadOnlyList<PeripheralObservation>>([Row()]))], _ => { });
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(82, Assert.Single(service.Current).Percentage);
    }
    [Fact]
    public async Task ServiceRetainsUnavailableDeviceAndHonorsCancellation()
    {
        var rows = new List<PeripheralObservation> { Row() };
        await using var service = new PeripheralBatteryService([new FakeBackend(_ => Task.FromResult<IReadOnlyList<PeripheralObservation>>(rows.ToArray()))], _ => { });
        await service.RefreshAsync(CancellationToken.None); rows.Clear(); await service.RefreshAsync(CancellationToken.None);
        Assert.False(Assert.Single(service.Current).Connected); Assert.Null(service.Current[0].ValidPercentage);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(stop.Token));
    }
    private sealed class FakeBackend(Func<CancellationToken, Task<IReadOnlyList<PeripheralObservation>>> collect) : IPeripheralBatteryBackend
    {
        public string Name => "Fake";
        public Task<IReadOnlyList<PeripheralObservation>> CollectAsync(CancellationToken token) => collect(token);
    }
}
