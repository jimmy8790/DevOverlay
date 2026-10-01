using System.IO;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class AppearanceSettingsTests
{
    [Fact]
    public void OldUiScaleDataLoadsSafelyButDoesNotAffectAppearance()
    {
        WithStore((store, path) =>
        {
            File.WriteAllText(path, "{\"Appearance\":{\"BackgroundColor\":\"#102030\",\"UiScale\":1.6,\"Spacing\":0.55}}");
            var loaded = store.Load().Appearance;
            Assert.Equal("#102030", loaded.BackgroundColor);
            Assert.Equal(.55, loaded.Spacing);

            store.Save(OverlaySettings.CreateDefault() with { Appearance = loaded });
            Assert.DoesNotContain("UiScale", File.ReadAllText(path));
        });
    }

    [Fact]
    public void SpacingPersistsAndViewModelHasNoScaleSetting()
    {
        WithStore((store, _) =>
        {
            var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
            viewModel.SettingsChanged += store.Save;
            viewModel.SpacingPercent = 160;
            Assert.Equal(1.6, store.Load().Appearance.Spacing);
            Assert.Null(typeof(SettingsViewModel).GetProperty("UiScalePercent"));
            Assert.Null(typeof(OverlayAppearance).GetProperty("UiScale"));
        });
    }

    [Fact]
    public void ResetAppearanceResetsSpacingAndPreservesUnrelatedSettings()
    {
        var settings = OverlaySettings.CreateDefault() with
        {
            Position = OverlayPosition.CreateCustom(20, 30),
            GroupOrder = [MetricCategory.Network, MetricCategory.Frame, MetricCategory.Latency, MetricCategory.Cpu, MetricCategory.Gpu, MetricCategory.Storage],
            Appearance = new OverlayAppearance("#000000", 0, "#010101", "#020202", 1.6),
            GpuDeviceSelection = DeviceSelection.Specific("gpu-id"),
            FpsTargetSelection = DeviceSelection.Specific("game.exe")
        };
        var viewModel = new SettingsViewModel(settings);
        OverlaySettings? changed = null;
        viewModel.SettingsChanged += value => changed = value;
        viewModel.ResetAppearance();

        Assert.Equal(OverlayAppearance.CreateDefault(), changed!.Appearance);
        Assert.Equal(settings.Position, changed.Position);
        Assert.Equal(OverlaySettings.NormalizeGroupOrder(settings.GroupOrder), changed.GroupOrder);
        Assert.Equal(settings.GpuDeviceSelection, changed.GpuDeviceSelection);
        Assert.Equal(settings.FpsTargetSelection, changed.FpsTargetSelection);
    }

    [Fact]
    public void ColorEditsAndSpacingLeaveEachOtherAlone()
    {
        var viewModel = new SettingsViewModel(OverlaySettings.CreateDefault());
        OverlaySettings? changed = null;
        viewModel.SettingsChanged += value => changed = value;
        viewModel.BackgroundColor = "#123456";
        viewModel.SpacingPercent = 55;
        Assert.Equal("#123456", changed!.Appearance.BackgroundColor);
        Assert.Equal(.55, changed.Appearance.Spacing);
    }

    private static void WithStore(Action<OverlaySettingsStore, string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"DevOverlay-appearance-{Guid.NewGuid():N}.json");
        try { test(new OverlaySettingsStore(path), path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
