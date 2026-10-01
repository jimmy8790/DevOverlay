using System.Windows.Threading;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

public sealed class AppearanceIndependenceTests
{
    private static readonly MetricId[] FpsIds = [MetricId.FramesPerSecond, MetricId.OnePercentLow, MetricId.FrameTime];

    private static double[] Horizontal(OverlayLayoutMetrics value) =>
        [value.MetricGap, value.GroupPaddingLeft,
            value.GroupPaddingRight, value.SeparatorMarginLeft, value.SeparatorMarginRight];

    private static double[] Fixed(OverlayLayoutMetrics value) =>
        [value.ValueFontSize, value.LabelFontSize, value.BorderPaddingX, value.BorderPaddingY,
            value.CornerRadius, value.SeparatorHeight, value.GroupPaddingY, value.LabelGap, value.PrefixValueGap];

    [Fact]
    public void SpacingExtremesChangeOnlyOuterBlockGaps()
    {
        var compact = OverlayLayoutMetrics.Create(OverlayAppearance.CreateDefault() with { Spacing = .4 });
        var wide = OverlayLayoutMetrics.Create(OverlayAppearance.CreateDefault() with { Spacing = 1.6 });
        var compactGaps = Horizontal(compact);
        var wideGaps = Horizontal(wide);

        for (var index = 0; index < compactGaps.Length; index++)
        {
            Assert.True(wideGaps[index] > compactGaps[index], $"Horizontal value {index} did not grow.");
            Assert.True(wideGaps[index] - compactGaps[index] >= 4, $"Horizontal value {index} changed too little.");
        }
        Assert.True(wideGaps.Sum() - compactGaps.Sum() >= 35);
        Assert.Equal(2, compact.PrefixValueGap);
        Assert.Equal(compact.PrefixValueGap, wide.PrefixValueGap);
        Assert.Equal(compact.LabelGap, wide.LabelGap);
    }

    [Fact]
    public void SpacingNeverChangesFixedHudSizeOrReservedMetricWidths()
    {
        var compact = OverlayLayoutMetrics.Create(OverlayAppearance.CreateDefault() with { Spacing = .4 });
        var wide = OverlayLayoutMetrics.Create(OverlayAppearance.CreateDefault() with { Spacing = 1.6 });
        Assert.Equal(Fixed(compact), Fixed(wide));

        var viewModel = new OverlayViewModel(OverlaySettings.CreateDefault(), Dispatcher.CurrentDispatcher);
        viewModel.ApplyMetrics(FpsIds.Select(id => MetricSnapshot.Unavailable(id, MetricCategory.Frame, "x", "")).ToArray());
        var widths = viewModel.Groups.Single().Items.Select(item => item.DisplayWidth).ToArray();
        viewModel.ApplySettings(OverlaySettings.CreateDefault() with { Appearance = OverlayAppearance.CreateDefault() with { Spacing = 1.6 } });
        Assert.Equal(widths, viewModel.Groups.Single().Items.Select(item => item.DisplayWidth).ToArray());
    }

    [Fact]
    public void FixedProductDefaultsAreCompactAndStable()
    {
        var layout = OverlayLayoutMetrics.Create(OverlayAppearance.CreateDefault());
        Assert.Equal(12, layout.ValueFontSize);
        Assert.Equal(11, layout.LabelFontSize);
        Assert.Equal(5, layout.BorderPaddingY);
        Assert.Equal(7, layout.CornerRadius);
        Assert.Equal(14, layout.SeparatorHeight);
        Assert.Equal(OverlayAppearance.DefaultSpacing, OverlayAppearance.CreateDefault().Spacing);
    }
}
