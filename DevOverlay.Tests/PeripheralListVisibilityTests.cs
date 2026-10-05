using DevOverlay.Configuration;
using DevOverlay.Peripherals;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class PeripheralListVisibilityTests
{
    private static PeripheralObservation Observation(string name, PeripheralType type, bool connected) =>
        new("iface-" + name, Guid.NewGuid().ToString(), name, type, connected, connected ? 70 : null, null,
            PeripheralBatterySource.Vendor, DateTimeOffset.UtcNow, "Ready");
    private static PeripheralPreference Preference(PeripheralObservation observation, string label) =>
        new(observation.Identity, observation.FriendlyName, observation.Type, label, Show: true);

    private sealed record Fixture(SettingsViewModel ViewModel, PeripheralObservation Mouse, PeripheralObservation Keyboard,
        PeripheralObservation Old, PeripheralPreference[] Preferences);
    private static Fixture Create(bool mouseConnected = true)
    {
        var mouse = Observation("Wireless mouse", PeripheralType.Mouse, mouseConnected);
        var keyboard = Observation("Gaming Keyboard", PeripheralType.Keyboard, connected: false);
        var old = Observation("Old receiver", PeripheralType.Other, connected: false);
        var preferences = new[] { Preference(mouse, "MSE"), Preference(keyboard, "KB"), Preference(old, "DEV") };
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault() with { PeripheralDevices = preferences });
        viewModel.UpdatePeripheralDevices(PeripheralBatteryMerge.Merge([mouse, keyboard]), preferences); // `old` is not enumerated at all
        return new(viewModel, mouse, keyboard, old, preferences);
    }

    [Fact]
    public void ListShowsOnlyConnectedDevicesByDefaultButKeepsEverySavedDevice()
    {
        var fixture = Create();
        Assert.Equal(3, fixture.ViewModel.PeripheralDevices.Count);
        var visible = Assert.Single(fixture.ViewModel.VisiblePeripheralDevices);
        Assert.Equal(fixture.Mouse.Identity, visible.Identity); Assert.True(visible.IsConnected);
        Assert.False(fixture.ViewModel.ShowDisconnectedPeripherals);
        Assert.Equal(string.Empty, fixture.ViewModel.PeripheralListHintText);
    }

    [Fact]
    public void TickingShowDisconnectedListsEveryDeviceInTheSameOrderAndUntickingHidesThemAgain()
    {
        var fixture = Create();
        fixture.ViewModel.ShowDisconnectedPeripherals = true;
        Assert.Equal(fixture.ViewModel.PeripheralDevices.Select(row => row.Identity), fixture.ViewModel.VisiblePeripheralDevices.Select(row => row.Identity));
        Assert.Equal(3, fixture.ViewModel.VisiblePeripheralDevices.Count);
        fixture.ViewModel.ShowDisconnectedPeripherals = false;
        Assert.Single(fixture.ViewModel.VisiblePeripheralDevices);
    }

    [Fact]
    public void ADeviceLeavesTheListWhenItDisconnectsAndReturnsWhenItReconnects()
    {
        var fixture = Create();
        var row = fixture.ViewModel.VisiblePeripheralDevices.Single();
        fixture.ViewModel.UpdatePeripheralDevices(PeripheralBatteryMerge.Merge([fixture.Mouse with { Connected = false, Percentage = null }, fixture.Keyboard]), fixture.Preferences);
        Assert.Empty(fixture.ViewModel.VisiblePeripheralDevices);
        Assert.Contains("No connected peripherals", fixture.ViewModel.PeripheralListHintText);
        fixture.ViewModel.UpdatePeripheralDevices(PeripheralBatteryMerge.Merge([fixture.Mouse, fixture.Keyboard]), fixture.Preferences);
        Assert.Same(row, Assert.Single(fixture.ViewModel.VisiblePeripheralDevices)); // same row object: edits and focus survive refreshes
    }

    [Fact]
    public void RefreshesDoNotRecreateRowsAndTheHintExplainsAnEmptyList()
    {
        var fixture = Create();
        var rows = fixture.ViewModel.VisiblePeripheralDevices.ToArray();
        for (var refresh = 0; refresh < 3; refresh++)
            fixture.ViewModel.UpdatePeripheralDevices(PeripheralBatteryMerge.Merge([fixture.Mouse, fixture.Keyboard]), fixture.Preferences);
        Assert.Equal(rows, fixture.ViewModel.VisiblePeripheralDevices.ToArray());

        var empty = new SettingsViewModel(OverlaySettings.CreateDefault());
        Assert.Equal("No peripherals detected yet.", empty.PeripheralListHintText);
    }

    [Fact]
    public void ForgetStaysReachableThroughTheDisconnectedToggleAndLeavesBothListsConsistent()
    {
        var fixture = Create();
        Assert.DoesNotContain(fixture.ViewModel.VisiblePeripheralDevices, row => row.Identity == fixture.Old.Identity);
        fixture.ViewModel.ShowDisconnectedPeripherals = true;
        var stale = fixture.ViewModel.VisiblePeripheralDevices.Single(row => row.Identity == fixture.Old.Identity);
        Assert.True(stale.CanForget);
        Assert.True(fixture.ViewModel.ForgetPeripheral(stale.Identity));
        Assert.DoesNotContain(fixture.ViewModel.PeripheralDevices, row => row.Identity == fixture.Old.Identity);
        Assert.DoesNotContain(fixture.ViewModel.VisiblePeripheralDevices, row => row.Identity == fixture.Old.Identity);
        Assert.Equal(2, fixture.ViewModel.VisiblePeripheralDevices.Count);
        Assert.True(fixture.ViewModel.VisiblePeripheralDevices.Single(row => row.Identity == fixture.Mouse.Identity).IsConnected);
    }

    [Fact]
    public void HudAndSavedSettingsAreNotAffectedByWhatTheListHides()
    {
        var fixture = Create();
        var changes = new List<OverlaySettings>(); fixture.ViewModel.SettingsChanged += changes.Add;
        fixture.ViewModel.ShowDisconnectedPeripherals = true; fixture.ViewModel.ShowDisconnectedPeripherals = false;
        Assert.Empty(changes);                                       // the toggle is session-only and never writes settings
        Assert.Equal(3, fixture.ViewModel.PeripheralDevices.Count);  // hidden disconnected devices keep their saved names/visibility
        Assert.All(fixture.ViewModel.PeripheralDevices, row => Assert.True(row.Show));
    }
}
