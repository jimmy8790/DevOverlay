using System.IO;
using System.Threading;
using DevOverlay.Configuration;
using DevOverlay.Presentation;
using DevOverlay.Platform.Windows;
using DevOverlay.UI;
using Xunit;

namespace DevOverlay.Tests;

[Collection(WpfCollection.Name)]
public sealed class OverlayCustomizationTests
{
    [Fact]
    public void DefaultsMatchCurrentHudAppearanceAndAutoPosition()
    {
        var settings = OverlaySettings.CreateDefault();

        Assert.Equal("#1A1D23", settings.Appearance.BackgroundColor);
        Assert.Equal(227d / 255d, settings.Appearance.BackgroundOpacity);
        Assert.Equal("#F5F7FA", settings.Appearance.ValueTextColor);
        Assert.Equal("#AFB9C8", settings.Appearance.LabelTextColor);
        Assert.Equal(OverlayPositionMode.AutoTopRight, settings.Position.Mode);
    }

    [Theory]
    [InlineData("#aBc123", "#ABC123")]
    [InlineData("A1B2C3", "#A1B2C3")]
    public void ColorsNormalizeToStableRgbHex(string source, string expected)
    {
        Assert.True(OverlayColor.TryNormalize(source, out var actual));
        Assert.Equal(expected, actual);
        Assert.False(OverlayColor.TryNormalize("#12345", out _));
        Assert.False(OverlayColor.TryNormalize("#GG0000", out _));
    }

    [Fact]
    public void AppearanceNormalizeClampsOpacityAndFallsBackOnlyInvalidFields()
    {
        var result = new OverlayAppearance("invalid", 999, "#123456", "#bad").Normalize();

        Assert.Equal(OverlayAppearance.CreateDefault().BackgroundColor, result.BackgroundColor);
        Assert.Equal(1, result.BackgroundOpacity);
        Assert.Equal("#123456", result.ValueTextColor);
        Assert.Equal(OverlayAppearance.CreateDefault().LabelTextColor, result.LabelTextColor);
    }

    [Fact]
    public void SaveLoadRoundTripsCustomizationWithoutChangingTelemetryPreferences()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DevOverlay-custom-{Guid.NewGuid():N}.json");
        try
        {
            var original = OverlaySettings.CreateDefault() with
            {
                Position = OverlayPosition.CreateCustom(123, 456),
                Appearance = new OverlayAppearance("#102030", .4, "#405060", "#708090"),
                GpuDeviceSelection = DeviceSelection.Specific("gpu-id"),
                NetworkDeviceSelection = DeviceSelection.Specific("network-id"),
                StorageDeviceSelection = DeviceSelection.Specific("disk-id"),
                EnabledMetrics = OverlaySettings.CreateDefault().EnabledMetrics.Except([DevOverlay.Metrics.MetricId.NetworkTodayTotal]).ToHashSet()
            };
            var store = new OverlaySettingsStore(path);
            store.Save(original);
            var restored = store.Load();

            Assert.Equal(original.Position, restored.Position);
            Assert.Equal(original.Appearance, restored.Appearance);
            Assert.Equal("gpu-id", Assert.IsType<SpecificDeviceSelection>(restored.GpuDeviceSelection).DeviceId);
            Assert.DoesNotContain(DevOverlay.Metrics.MetricId.NetworkTodayTotal, restored.EnabledMetrics);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MissingOrInvalidCustomizationDoesNotResetOtherSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DevOverlay-custom-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"GpuDeviceId\":\"gpu-id\",\"Appearance\":{\"BackgroundColor\":\"bad\",\"BackgroundOpacity\":-4,\"ValueTextColor\":\"#0a0b0c\"},\"PositionMode\":1,\"PositionOffsetX\":999999,\"PositionOffsetY\":-2}");
            var loaded = new OverlaySettingsStore(path).Load();

            Assert.Equal(OverlayPositionMode.Custom, loaded.Position.Mode);
            Assert.Equal(999999, loaded.Position.OffsetX);
            Assert.Equal(-2, loaded.Position.OffsetY);
            Assert.Equal(0, loaded.Appearance.BackgroundOpacity);
            Assert.Equal("#0A0B0C", loaded.Appearance.ValueTextColor);
            Assert.Equal(OverlayAppearance.CreateDefault().BackgroundColor, loaded.Appearance.BackgroundColor);
            Assert.Equal("gpu-id", Assert.IsType<SpecificDeviceSelection>(loaded.GpuDeviceSelection).DeviceId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ResetActionsChangeOnlyTheirOwnCustomization()
    {
        var settings = OverlaySettings.CreateDefault() with
        {
            Position = OverlayPosition.CreateCustom(20, 30),
            Appearance = new OverlayAppearance("#000000", 0, "#010101", "#020202"),
            GpuDeviceSelection = DeviceSelection.Specific("gpu-id")
        };
        var viewModel = new SettingsViewModel(settings);
        OverlaySettings? changed = null;
        viewModel.SettingsChanged += value => changed = value;

        viewModel.ResetAppearance();
        Assert.Equal(OverlayAppearance.CreateDefault(), changed!.Appearance);
        Assert.Equal(settings.Position, changed.Position);
        Assert.Equal("gpu-id", Assert.IsType<SpecificDeviceSelection>(changed.GpuDeviceSelection).DeviceId);

        viewModel.ResetPosition();
        Assert.Equal(OverlayPosition.CreateDefault(), changed!.Position);
        Assert.Equal(OverlayAppearance.CreateDefault(), changed.Appearance);
    }

    [Fact]
    public void AppearanceOrPositionChangesDoNotRequireProviderRestart()
    {
        var before = OverlaySettings.CreateDefault();
        var after = before with
        {
            Position = OverlayPosition.CreateCustom(11, 12),
            Appearance = new OverlayAppearance("#010203", .5, "#040506", "#070809")
        };

        Assert.False(App.HasProviderSelectionChanged(before, after));
    }

    [Fact]
    public void AppearanceApplication_ChangesOnlyConfiguredBrushes()
    {
        RunOnSta(() =>
        {
            var settings = OverlaySettings.CreateDefault() with
            {
                Appearance = new OverlayAppearance("#010203", .25, "#040506", "#070809")
            };
            var viewModel = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            var window = new OverlayWindow(viewModel, new OverlayPositioningService());
            try
            {
                var background = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.Resources["OverlayBackgroundBrush"]);
                var values = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.Resources["OverlayValueBrush"]);
                var labels = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.Resources["OverlayLabelBrush"]);
                Assert.Equal(System.Windows.Media.Color.FromRgb(1, 2, 3), background.Color);
                Assert.Equal(.25, background.Opacity);
                Assert.Equal(System.Windows.Media.Color.FromRgb(4, 5, 6), values.Color);
                Assert.Equal(1, values.Opacity);
                Assert.Equal(System.Windows.Media.Color.FromRgb(7, 8, 9), labels.Color);
                Assert.Equal(1, labels.Opacity);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.5)]
    [InlineData(1)]
    public void BackgroundOpacity_MultipliesBorderButNotText(double opacity)
    {
        RunOnSta(() =>
        {
            var settings = OverlaySettings.CreateDefault() with
            {
                Appearance = OverlayAppearance.CreateDefault() with { BackgroundOpacity = opacity }
            };
            var viewModel = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            var window = new OverlayWindow(viewModel, new OverlayPositioningService());
            try
            {
                var background = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.Resources["OverlayBackgroundBrush"]);
                var border = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.Resources["OverlayBorderBrush"]);
                var values = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.Resources["OverlayValueBrush"]);
                var labels = Assert.IsType<System.Windows.Media.SolidColorBrush>(window.Resources["OverlayLabelBrush"]);
                Assert.Equal(opacity, background.Opacity);
                Assert.Equal(opacity, border.Opacity);
                Assert.Equal(0x73, border.Color.A);
                Assert.Equal(1, values.Opacity);
                Assert.Equal(1, labels.Opacity);
                window.ApplySettings(settings.Position, OverlayAppearance.CreateDefault());
                Assert.Equal(OverlayAppearance.CreateDefault().BackgroundOpacity, border.Opacity);
                Assert.Equal(0x73, border.Color.A);
            }
            finally { window.Close(); }
        });
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
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
