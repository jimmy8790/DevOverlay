using DevOverlay.Configuration;
using DevOverlay.Peripherals;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class ForgetPeripheralTests
{
    private static string NewContainer() => Guid.NewGuid().ToString();
    private static PeripheralObservation Observation(string container, string name, PeripheralType type, bool connected, double? percent = 80) =>
        new("iface-" + container, container, name, type, connected, percent, null, PeripheralBatterySource.Vendor, DateTimeOffset.UtcNow, "Ready");
    private static PeripheralPreference Preference(PeripheralObservation observation, string label, bool show, string custom = "") =>
        new(observation.Identity, observation.FriendlyName, observation.Type, label, show, custom);

    // Three saved devices: a stale/disconnected one, another disconnected one with its own settings, and a connected one.
    private sealed record Fixture(SettingsViewModel ViewModel, PeripheralObservation Stale, PeripheralObservation Other,
        PeripheralObservation Connected, List<OverlaySettings> Changes, List<string> Forgotten);
    private static Fixture Create()
    {
        var stale = Observation(NewContainer(), "Old receiver", PeripheralType.Other, connected: false, percent: null);
        var other = Observation(NewContainer(), "Gaming Keyboard", PeripheralType.Keyboard, connected: false, percent: null);
        var connected = Observation(NewContainer(), "Wireless mouse", PeripheralType.Mouse, connected: true);
        var preferences = new[]
        {
            Preference(stale, "DEV", show: true, custom: "OLD"),
            Preference(other, "KB", show: true, custom: "TKL"),
            Preference(connected, "MSE", show: true, custom: "X2")
        };
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault() with { PeripheralDevices = preferences });
        var readings = PeripheralBatteryMerge.Merge([other, connected]); // the stale identity is no longer enumerated at all
        viewModel.UpdatePeripheralDevices(readings, preferences);
        var changes = new List<OverlaySettings>(); var forgotten = new List<string>();
        viewModel.SettingsChanged += changes.Add; viewModel.PeripheralForgotten += forgotten.Add;
        return new(viewModel, stale, other, connected, changes, forgotten);
    }

    [Fact]
    public void OnlyDisconnectedOrUnseenSavedDevicesOfferForget()
    {
        var fixture = Create();
        var rows = fixture.ViewModel.PeripheralDevices.ToDictionary(row => row.Identity);
        Assert.True(rows[fixture.Stale.Identity].CanForget);
        Assert.True(rows[fixture.Other.Identity].CanForget);
        Assert.False(rows[fixture.Connected.Identity].CanForget);
        Assert.Equal(System.Windows.Visibility.Visible, rows[fixture.Stale.Identity].ForgetVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, rows[fixture.Connected.Identity].ForgetVisibility);
    }

    [Fact]
    public void ForgettingADisconnectedDeviceRemovesOnlyThatIdentity()
    {
        var fixture = Create();
        Assert.True(fixture.ViewModel.ForgetPeripheral(fixture.Stale.Identity));
        Assert.DoesNotContain(fixture.ViewModel.PeripheralDevices, row => row.Identity == fixture.Stale.Identity);
        var saved = Assert.Single(fixture.Changes).PeripheralDevices;
        Assert.Equal(2, saved.Count);
        Assert.DoesNotContain(saved, item => item.Identity == fixture.Stale.Identity);
        var keyboard = Assert.Single(saved, item => item.Identity == fixture.Other.Identity);
        Assert.True(keyboard.Show); Assert.Equal("TKL", keyboard.CustomName); Assert.Equal("KB", keyboard.DefaultLabel);
        var mouse = Assert.Single(saved, item => item.Identity == fixture.Connected.Identity);
        Assert.True(mouse.Show); Assert.Equal("X2", mouse.CustomName); Assert.Equal("MSE", mouse.DefaultLabel);
        Assert.Equal([fixture.Stale.Identity], fixture.Forgotten);
    }

    [Fact]
    public void ForgetKeepsEveryOtherSettingAndSurvivesSaveAndReload()
    {
        var fixture = Create();
        fixture.ViewModel.ForgetPeripheral(fixture.Other.Identity);
        var path = Path.Combine(Path.GetTempPath(), "DevOverlay-tests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new OverlaySettingsStore(path);
            store.Save(Assert.Single(fixture.Changes));
            var reloaded = store.Load();
            Assert.DoesNotContain(reloaded.PeripheralDevices, item => item.Identity == fixture.Other.Identity);
            Assert.Equal(["OLD", "X2"], reloaded.PeripheralDevices.Select(item => item.CustomName).Order().ToArray());
            Assert.All(reloaded.PeripheralDevices, item => Assert.True(item.Show));
            // A fresh view model built from the reloaded file (application restart) lists neither the forgotten device nor a replacement.
            var restarted = new SettingsViewModel(reloaded);
            restarted.UpdatePeripheralDevices(PeripheralBatteryMerge.Merge([fixture.Connected]), reloaded.PeripheralDevices);
            Assert.Equal(2, restarted.PeripheralDevices.Count);
            Assert.DoesNotContain(restarted.PeripheralDevices, row => row.Identity == fixture.Other.Identity);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (IOException) { } }
    }

    [Fact]
    public void ConnectedAndUnknownDevicesAreNeverForgotten()
    {
        var fixture = Create();
        Assert.False(fixture.ViewModel.ForgetPeripheral(fixture.Connected.Identity));
        Assert.False(fixture.ViewModel.ForgetPeripheral("not-a-saved-identity"));
        Assert.Equal(3, fixture.ViewModel.PeripheralDevices.Count);
        Assert.Empty(fixture.Changes); Assert.Empty(fixture.Forgotten);
    }

    [Fact]
    public void ADeviceThatConnectsLaterBecomesForgettableOnlyAfterItDisconnects()
    {
        var fixture = Create();
        var preferences = fixture.ViewModel.PeripheralDevices.Select(row => new PeripheralPreference(row.Identity, row.FriendlyName,
            PeripheralType.Keyboard, row.DefaultLabel, row.Show, row.CustomName)).ToArray();
        fixture.ViewModel.UpdatePeripheralDevices(PeripheralBatteryMerge.Merge([fixture.Other with { Connected = true, Percentage = 50 }]), preferences);
        Assert.False(fixture.ViewModel.ForgetPeripheral(fixture.Other.Identity));
        fixture.ViewModel.UpdatePeripheralDevices(PeripheralBatteryMerge.Merge([fixture.Other]), preferences);
        Assert.True(fixture.ViewModel.ForgetPeripheral(fixture.Other.Identity));
    }

    [Fact]
    public void RediscoveredHardwareGetsAFreshEntryWithoutTheOldCustomSettings()
    {
        var fixture = Create();
        fixture.ViewModel.ForgetPeripheral(fixture.Other.Identity);
        var saved = fixture.Changes.Single().PeripheralDevices;
        var rediscovered = PeripheralBatteryMerge.Merge([fixture.Other with { Connected = true, Percentage = 64 }]);
        var preferences = PeripheralBatteryMerge.AddPreferences(saved, rediscovered);
        var fresh = Assert.Single(preferences, item => item.Identity == fixture.Other.Identity);
        Assert.Equal("", fresh.CustomName); Assert.Equal("KB", fresh.HudLabel); Assert.True(fresh.Show); // readable numeric device defaults to shown
        Assert.Equal(3, preferences.Count);
        Assert.Equal("X2", preferences.Single(item => item.Identity == fixture.Connected.Identity).CustomName);
    }

    [Fact]
    public async Task ServiceForgetsItsRuntimeMemoryButStillRediscoversTheDevice()
    {
        var device = Observation(NewContainer(), "Gaming Keyboard", PeripheralType.Keyboard, connected: true);
        var present = true; var calls = 0;
        var backend = new FakeBackend(_ => { calls++; return Task.FromResult<IReadOnlyList<PeripheralObservation>>(present ? [device] : []); });
        await using var service = new PeripheralBatteryService([backend], _ => { });
        await service.RefreshAsync(default);
        present = false;
        await service.RefreshAsync(default);
        Assert.False(Assert.Single(service.Current).Connected);   // stub kept for a device that disappeared this session
        var updates = 0; service.Updated += _ => updates++;

        service.Forget(device.Identity);
        service.Forget("never-seen");
        Assert.Empty(service.Current);
        Assert.Equal(2, calls); Assert.Equal(0, updates);          // forgetting performs no device query and no refresh

        await service.RefreshAsync(default);                       // still absent: nothing to resurrect it
        Assert.Empty(service.Current);
        present = true;
        await service.RefreshAsync(default);                       // hardware is back: normal rediscovery
        Assert.Equal(device.Identity, Assert.Single(service.Current).Identity);
    }

    [Fact]
    public void ForgetOnlyEditsSettingsAndNeverTouchesBackendsOrDevices()
    {
        // The view model holds no backend, transport or device reference; its only outputs are these two events.
        var members = typeof(SettingsViewModel).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public).Select(field => field.FieldType);
        Assert.DoesNotContain(members, type => typeof(IPeripheralBatteryBackend).IsAssignableFrom(type) ||
            type == typeof(PeripheralBatteryService) || type.Namespace == "DevOverlay.Peripherals.VendorBackends");
        var fixture = Create();
        fixture.ViewModel.ForgetPeripheral(fixture.Stale.Identity);
        Assert.Single(fixture.Changes); Assert.Single(fixture.Forgotten);
    }

    private sealed class FakeBackend(Func<CancellationToken, Task<IReadOnlyList<PeripheralObservation>>> collect) : IPeripheralBatteryBackend
    {
        public string Name => "Fake";
        public Task<IReadOnlyList<PeripheralObservation>> CollectAsync(CancellationToken token) => collect(token);
    }
}
