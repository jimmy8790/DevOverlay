using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Presentation;
using DevOverlay.Platform.Windows;
using DevOverlay.UI;
using Xunit;

namespace DevOverlay.Tests;

public sealed class HudLayoutAndFormattingTests
{
    [Theory]
    [InlineData(MetricId.FramesPerSecond, 999, "", "999")]
    [InlineData(MetricId.OnePercentLow, 999, "", "1% 999")]
    [InlineData(MetricId.FrameTime, 100, "ms", "FT 100.0ms")]
    [InlineData(MetricId.Latency, 100, "ms", "100.0ms")]
    public void FpsValuesUseSeparatePrefixAndStableValueField(MetricId id, double value, string unit, string expected)
    {
        var item = new MetricItemViewModel(new MetricSnapshot(id, MetricCategory.Frame, "x", value, unit, true, DateTimeOffset.UtcNow));
        Assert.Equal(expected, item.Text);
        Assert.Equal(expected.Split(' ').Last(), item.ValueText);
        Assert.Equal(id is MetricId.FramesPerSecond or MetricId.Latency ? string.Empty : expected.Split(' ')[0], item.Prefix);
        Assert.True(item.DisplayWidth > 0);
    }

    [Theory]
    [InlineData(MetricId.FramesPerSecond, "N/A")]
    [InlineData(MetricId.OnePercentLow, "1% N/A")]
    [InlineData(MetricId.FrameTime, "FT N/A")]
    [InlineData(MetricId.Latency, "N/A")]
    [InlineData(MetricId.NetworkTodayTotal, "DAY N/A")]
    [InlineData(MetricId.StorageRead, "R N/A")]
    public void UnavailableValuesKeepPrefixOutsideTheReservedValueField(MetricId id, string expected)
    {
        var item = new MetricItemViewModel(MetricSnapshot.Unavailable(id, MetricCategory.Frame, "x", ""));
        Assert.Equal(expected, item.Text);
        Assert.Equal("N/A", item.ValueText);
        Assert.Equal("N/A", MetricTextFormatter.UnavailableMarker);
    }

    [Fact]
    public void FrameReservedWidthsAreAutomaticallyMeasuredAndStable()
    {
        Assert.Equal(MetricDisplayLayout.GetTextWidth(MetricId.FramesPerSecond), MetricDisplayLayout.GetTextWidth(MetricId.OnePercentLow));
        Assert.Equal(MetricDisplayLayout.GetTextWidth(MetricId.FrameTime), MetricDisplayLayout.GetTextWidth(MetricId.Latency));
        Assert.True(MetricDisplayLayout.GetTextWidth(MetricId.FrameTime) > MetricDisplayLayout.GetTextWidth(MetricId.FramesPerSecond));
    }

    [Fact]
    public void GroupOrderAndSeparatorsRemainIndependentOfAppearance()
    {
        var settings = OverlaySettings.CreateDefault() with { Appearance = OverlayAppearance.CreateDefault() with { Spacing = 1.6 } };
        var viewModel = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        viewModel.ApplyMetrics([
            MetricSnapshot.Unavailable(MetricId.CpuUtilization, MetricCategory.Cpu, "CPU", "%"),
            MetricSnapshot.Unavailable(MetricId.FramesPerSecond, MetricCategory.Frame, "FPS", "")]);
        Assert.Equal(new[] { MetricCategory.Cpu, MetricCategory.Frame }, viewModel.Groups.Select(group => group.Category));
        Assert.True(viewModel.Groups[0].HasFollowingGroup);
        Assert.False(viewModel.Groups[1].HasFollowingGroup);
    }

    [Fact]
    public void AiUsageHudOmitsRedundantGroupTitleAndKeepsProviderPrefixes()
    {
        var settings = OverlaySettings.CreateDefault() with
        {
            EnabledGroups = new HashSet<MetricCategory> { MetricCategory.AiUsage },
            EnabledMetrics = new HashSet<MetricId>
            {
                MetricId.CodexPrimaryRateLimit,
                MetricId.ClaudePrimaryRateLimit
            }
        };
        var viewModel = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        viewModel.ApplyMetrics([
            new MetricSnapshot(MetricId.CodexPrimaryRateLimit, MetricCategory.AiUsage, "CX 5H", 75, "%", true, DateTimeOffset.UtcNow),
            new MetricSnapshot(MetricId.ClaudePrimaryRateLimit, MetricCategory.AiUsage, "CL 5H", 60, "%", true, DateTimeOffset.UtcNow)
        ]);

        var group = Assert.Single(viewModel.Groups);
        Assert.Equal(string.Empty, group.Title);
        Assert.Equal(new[] { "CX 5H", "CL 5H" }, group.Items.Select(item => item.Prefix));
    }

    [Fact]
    public void OverlayWindowAppliesSpacingToActualTemplateResourcesWithoutResizingFontsOrValues()
    {
        RunOnSta(() =>
        {
            var compact = OverlayAppearance.CreateDefault() with { Spacing = .4 };
            var wide = OverlayAppearance.CreateDefault() with { Spacing = 1.6 };
            var viewModel = new OverlayViewModel(OverlaySettings.CreateDefault() with { Appearance = compact },
                System.Windows.Threading.Dispatcher.CurrentDispatcher);
            var window = new OverlayWindow(viewModel, new OverlayPositioningService());
            try
            {
                var compactMetricMargin = Assert.IsType<System.Windows.Thickness>(window.Resources["OverlayMetricMargin"]);
                var compactPrefixMargin = Assert.IsType<System.Windows.Thickness>(window.Resources["OverlayPrefixMargin"]);
                var valueFont = Assert.IsType<double>(window.Resources["OverlayValueFontSize"]);
                var separatorHeight = Assert.IsType<double>(window.Resources["OverlaySeparatorHeight"]);

                window.ApplySettings(OverlayPosition.CreateDefault(), wide);

                var wideMetricMargin = Assert.IsType<System.Windows.Thickness>(window.Resources["OverlayMetricMargin"]);
                var widePrefixMargin = Assert.IsType<System.Windows.Thickness>(window.Resources["OverlayPrefixMargin"]);
                Assert.True(wideMetricMargin.Right > compactMetricMargin.Right);
                Assert.Equal(compactPrefixMargin, widePrefixMargin);
                Assert.Equal(valueFont, Assert.IsType<double>(window.Resources["OverlayValueFontSize"]));
                Assert.Equal(separatorHeight, Assert.IsType<double>(window.Resources["OverlaySeparatorHeight"]));
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

    [Fact]
    public void LiveSliderChangesRenderedMarginsAndWindowWidth()
    {
        RunOnSta(() =>
        {
            var settings = OverlaySettings.CreateDefault();
            var overlay = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            overlay.ApplyMetrics([
                MetricSnapshot.Unavailable(MetricId.FramesPerSecond, MetricCategory.Frame, "FPS", ""),
                MetricSnapshot.Unavailable(MetricId.OnePercentLow, MetricCategory.Frame, "1%", ""),
                MetricSnapshot.Unavailable(MetricId.FrameTime, MetricCategory.Frame, "FT", "ms"),
                MetricSnapshot.Unavailable(MetricId.Latency, MetricCategory.Latency, "LAT", "ms")]);
            var window = new OverlayWindow(overlay, new OverlayPositioningService());
            var editor = new SettingsViewModel(settings);
            var settingsWindow = new SettingsWindow(editor);
            var applied = new List<double>();
            Exception? applyFailure = null;
            editor.SettingsChanged += next =>
            {
                try { applied.Add(next.Appearance.Spacing); overlay.ApplySettings(next); window.ApplySettings(next.Position, next.Appearance); }
                catch (Exception exception) { applyFailure = exception; }
            };
            try
            {
                window.Show();
                settingsWindow.Show();
                Flush();
                var slider = Descendants(settingsWindow).OfType<System.Windows.Controls.Slider>().Single(element =>
                    System.Windows.Data.BindingOperations.GetBinding(element, System.Windows.Controls.Primitives.RangeBase.ValueProperty)?.Path.Path == nameof(SettingsViewModel.SpacingPercent));
                slider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, 40d);
                Flush();
                Assert.Equal(40, editor.SpacingPercent);
                Assert.True(applyFailure is null, applyFailure?.ToString());
                var compact = Descendants(window).OfType<System.Windows.Controls.TextBlock>().Single(element => element.Text == "FPS");
                var compactMargin = compact.Margin;
                var prefix = Descendants(window).OfType<System.Windows.Controls.TextBlock>().Single(element => element.Text == "FT");
                var prefixMargin = prefix.Margin;
                var metricPanel = Descendants(window).OfType<System.Windows.Controls.StackPanel>().Single(element =>
                    element.DataContext is MetricItemViewModel item && item.Id == MetricId.FrameTime);
                var metricMargin = metricPanel.Margin;
                var groupPanel = Descendants(window).OfType<System.Windows.Controls.StackPanel>().Single(element =>
                    element.DataContext is MetricGroupViewModel group && group.Category == MetricCategory.Frame && element.Margin.Left > 0);
                var groupMargin = groupPanel.Margin;
                var separator = Descendants(window).OfType<System.Windows.Controls.Border>().Single(element =>
                    element.DataContext is MetricGroupViewModel group && group.Category == MetricCategory.Frame && element.Height == 14);
                var separatorMargin = separator.Margin;
                var width = window.ActualWidth;
                var height = window.ActualHeight;
                slider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, 160d);
                Flush();
                Assert.Equal(160, editor.SpacingPercent);
                Assert.True(applyFailure is null, applyFailure?.ToString());
                Assert.Equal(compactMargin, compact.Margin);
                Assert.True(window.ActualWidth > width + 40, $"width {width} -> {window.ActualWidth}");
                Assert.Equal(height, window.ActualHeight);
                Assert.Equal(prefixMargin, prefix.Margin);
                Assert.Equal(metricMargin, metricPanel.Margin); // The final pair intentionally has no trailing item gap.
                Assert.True(groupPanel.Margin.Left > groupMargin.Left && groupPanel.Margin.Right > groupMargin.Right);
                Assert.True(separator.Margin.Left > separatorMargin.Left && separator.Margin.Right > separatorMargin.Right);
                Assert.Equal(11, compact.FontSize);
                Assert.Equal(MetricDisplayLayout.GetTextWidth(MetricId.Latency), Descendants(window).OfType<System.Windows.Controls.TextBlock>().Single(element =>
                    element.DataContext is MetricItemViewModel item && item.Id == MetricId.Latency && !double.IsNaN(element.Width)).Width);
            }
            finally { settingsWindow.Close(); window.Close(); }
        });
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [Fact]
    public void ActualTemplateKeepsInnerGeometryFixedWhileFpsAndNetworkPairsMoveApart()
    {
        RunOnSta(() =>
        {
            var settings = OverlaySettings.CreateDefault() with
                { Appearance = OverlayAppearance.CreateDefault() with { Spacing = .4 } };
            var vm = new OverlayViewModel(settings, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            MetricSnapshot Sample(MetricId id, MetricCategory category, double value, string unit) =>
                new(id, category, "test", value, unit, true, DateTimeOffset.UtcNow);
            var samples = new[]
            {
                Sample(MetricId.CpuUtilization, MetricCategory.Cpu, 18, "%"),
                Sample(MetricId.GpuUtilization, MetricCategory.Gpu, 92, "%"),
                Sample(MetricId.FramesPerSecond, MetricCategory.Frame, 144, ""),
                Sample(MetricId.OnePercentLow, MetricCategory.Frame, 118, ""),
                Sample(MetricId.FrameTime, MetricCategory.Frame, 6.9, "ms"),
                Sample(MetricId.Latency, MetricCategory.Latency, 3.1, "ms"),
                Sample(MetricId.StorageRead, MetricCategory.Storage, 150, "MB/s"),
                Sample(MetricId.StorageWrite, MetricCategory.Storage, 80, "MB/s"),
                Sample(MetricId.NetworkDownload, MetricCategory.Network, 12.3, "MB/s"),
                Sample(MetricId.NetworkUpload, MetricCategory.Network, 3.2, "MB/s"),
                Sample(MetricId.NetworkTodayTotal, MetricCategory.Network, 18.4, "GB")
            };
            vm.ApplyMetrics(samples);
            var window = new OverlayWindow(vm, new OverlayPositioningService());
            try
            {
                window.Show();
                Flush();
                System.Windows.Controls.TextBlock Value(MetricId id) => Descendants(window)
                    .OfType<System.Windows.Controls.TextBlock>().Single(text =>
                        text.DataContext is MetricItemViewModel item && item.Id == id && !double.IsNaN(text.Width));
                System.Windows.Controls.TextBlock Prefix(MetricId id) => Descendants(window)
                    .OfType<System.Windows.Controls.TextBlock>().Single(text =>
                        text.DataContext is MetricItemViewModel item && item.Id == id && double.IsNaN(text.Width));
                System.Windows.Controls.TextBlock Title(string title) => Descendants(window)
                    .OfType<System.Windows.Controls.TextBlock>().Single(text => text.Text == title);
                double X(System.Windows.FrameworkElement element) =>
                    element.TransformToAncestor(window).Transform(new System.Windows.Point()).X;
                double Gap(System.Windows.FrameworkElement left, System.Windows.FrameworkElement right) =>
                    X(right) - X(left) - left.ActualWidth;

                var innerPairs = new[]
                {
                    (Title("FPS"), Value(MetricId.FramesPerSecond)),
                    (Prefix(MetricId.OnePercentLow), Value(MetricId.OnePercentLow)),
                    (Prefix(MetricId.FrameTime), Value(MetricId.FrameTime)),
                    (Title("LAT"), Value(MetricId.Latency)),
                    (Title("CPU"), Value(MetricId.CpuUtilization)),
                    (Title("GPU"), Value(MetricId.GpuUtilization)),
                    (Prefix(MetricId.StorageRead), Value(MetricId.StorageRead)),
                    (Prefix(MetricId.StorageWrite), Value(MetricId.StorageWrite)),
                    (Prefix(MetricId.NetworkDownload), Value(MetricId.NetworkDownload)),
                    (Prefix(MetricId.NetworkUpload), Value(MetricId.NetworkUpload)),
                    (Prefix(MetricId.NetworkTodayTotal), Value(MetricId.NetworkTodayTotal))
                };
                var outerPairs = new[]
                {
                    (Value(MetricId.FramesPerSecond), Prefix(MetricId.OnePercentLow)),
                    (Value(MetricId.OnePercentLow), Prefix(MetricId.FrameTime)),
                    (Value(MetricId.NetworkDownload), Prefix(MetricId.NetworkUpload)),
                    (Value(MetricId.NetworkUpload), Prefix(MetricId.NetworkTodayTotal))
                };
                var inner = innerPairs.Select(pair => Gap(pair.Item1, pair.Item2)).ToArray();
                var outer = outerPairs.Select(pair => Gap(pair.Item1, pair.Item2)).ToArray();
                var widths = samples.Select(sample => Value(sample.Id).ActualWidth).ToArray();
                Assert.All(inner, gap => Assert.InRange(gap, 1.3, 2.7));
                Assert.All(samples, sample => Assert.Equal(System.Windows.TextAlignment.Left, Value(sample.Id).TextAlignment));

                window.ApplySettings(settings.Position, settings.Appearance with { Spacing = 1.6 });
                Flush();
                for (var index = 0; index < innerPairs.Length; index++)
                    Assert.InRange(Math.Abs(Gap(innerPairs[index].Item1, innerPairs[index].Item2) - inner[index]), 0, .7);
                for (var index = 0; index < outerPairs.Length; index++)
                    Assert.True(Gap(outerPairs[index].Item1, outerPairs[index].Item2) - outer[index] > 0);
                Assert.Equal(widths, samples.Select(sample => Value(sample.Id).ActualWidth).ToArray());

                var stableWidth = window.ActualWidth;
                vm.ApplyMetrics(samples.Select(sample => MetricSnapshot.Unavailable(sample.Id, sample.Category, "test", sample.Unit)).ToArray());
                Flush();
                Assert.Equal(stableWidth, window.ActualWidth);
                Assert.All(samples, sample =>
                {
                    var text = Value(sample.Id);
                    Assert.Equal("N/A", text.Text);
                    var measured = new System.Windows.Media.FormattedText(text.Text,
                        System.Globalization.CultureInfo.CurrentCulture, text.FlowDirection,
                        new System.Windows.Media.Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                        text.FontSize, text.Foreground, System.Windows.Media.VisualTreeHelper.GetDpi(text).PixelsPerDip);
                    Assert.True(measured.Width <= text.Width);
                });
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(MetricId.CpuUtilization, "100.0%")]
    [InlineData(MetricId.CpuTemperature, "100°C")]
    [InlineData(MetricId.CpuPower, "100W")]
    [InlineData(MetricId.GpuUtilization, "100.0%")]
    [InlineData(MetricId.GpuTemperature, "100°C")]
    [InlineData(MetricId.GpuPower, "999W")]
    [InlineData(MetricId.GpuVramUsed, "15.9GB")]
    [InlineData(MetricId.FramesPerSecond, "999")]
    [InlineData(MetricId.OnePercentLow, "999")]
    [InlineData(MetricId.FrameTime, "100.0ms")]
    [InlineData(MetricId.Latency, "100.0ms")]
    [InlineData(MetricId.NetworkDownload, "999.9MB/s")]
    [InlineData(MetricId.NetworkUpload, "999.9MB/s")]
    [InlineData(MetricId.StorageRead, "999.9MB/s")]
    [InlineData(MetricId.StorageWrite, "999.9MB/s")]
    [InlineData(MetricId.NetworkTodayTotal, "1023GB")]
    public void ReservedWidthsFitRepresentativeUpperValuesInActualHudFont(MetricId id, string text)
    {
        RunOnSta(() =>
        {
            var measured = new System.Windows.Media.FormattedText(text,
                System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe UI"),
                    System.Windows.FontStyles.Normal, System.Windows.FontWeights.SemiBold, System.Windows.FontStretches.Normal),
                12, System.Windows.Media.Brushes.White, 1.0);
            Assert.True(measured.Width <= MetricDisplayLayout.GetTextWidth(id),
                $"{id}: '{text}' requires {measured.Width:0.00} DIP; reserved {MetricDisplayLayout.GetTextWidth(id)}");
        });
    }

    private static void Flush()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
