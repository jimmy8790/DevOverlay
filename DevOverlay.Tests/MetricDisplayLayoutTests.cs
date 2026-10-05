using DevOverlay.Metrics;
using DevOverlay.Presentation;
using Xunit;

namespace DevOverlay.Tests;

[Collection(WpfCollection.Name)]
public sealed class MetricDisplayLayoutTests
{
    [Theory]
    [InlineData(MetricId.CpuUtilization)]
    [InlineData(MetricId.GpuUtilization)]
    [InlineData(MetricId.GpuTemperature)]
    [InlineData(MetricId.GpuPower)]
    [InlineData(MetricId.GpuVramUsed)]
    [InlineData(MetricId.NetworkDownload)]
    [InlineData(MetricId.NetworkUpload)]
    [InlineData(MetricId.StorageRead)]
    [InlineData(MetricId.StorageWrite)]
    public void RealMetricFields_UseAutomaticallyMeasuredStableWidths(MetricId id)
    {
        Assert.True(MetricDisplayLayout.GetTextWidth(id) > 0);
    }

    [Theory]
    [InlineData(MetricId.CpuUtilization, 100, "%")]
    [InlineData(MetricId.NetworkDownload, 125, "MB/s")]
    [InlineData(MetricId.StorageWrite, 18.4, "MB/s")]
    public void AvailableAndUnavailableValues_KeepTheSameDisplayWidth(MetricId id, double value, string unit)
    {
        var available = new MetricItemViewModel(new MetricSnapshot(
            id,
            GetCategory(id),
            "test",
            value,
            unit,
            true,
            DateTimeOffset.UtcNow));
        var unavailable = new MetricItemViewModel(MetricSnapshot.Unavailable(id, GetCategory(id), "test", unit));

        Assert.Equal(available.DisplayWidth, unavailable.DisplayWidth);
        Assert.NotEqual(available.Text, unavailable.Text);
    }

    [Theory]
    [InlineData(MetricId.CpuUtilization, 10.1)]
    [InlineData(MetricId.CpuUtilization, 99.9)]
    [InlineData(MetricId.GpuUtilization, 10.1)]
    public void UtilizationDecimalValuesFitTheirStableReservedField(MetricId id, double value)
    {
        RunOnSta(() =>
        {
            var item = new MetricItemViewModel(new MetricSnapshot(id, GetCategory(id), "test", value, "%", true, DateTimeOffset.UtcNow));
            var measured = new System.Windows.Media.FormattedText(item.ValueText,
                System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe UI"), System.Windows.FontStyles.Normal,
                    System.Windows.FontWeights.SemiBold, System.Windows.FontStretches.Normal),
                12, System.Windows.Media.Brushes.White, 1.0);
            Assert.True(measured.Width <= item.DisplayWidth, $"{item.ValueText} exceeds {item.DisplayWidth} DIP.");
        });
    }

    [Fact]
    public void ThroughputUnitTransitions_UseOneFieldWidthPerDirection()
    {
        Assert.Equal(
            MetricDisplayLayout.GetTextWidth(MetricId.NetworkDownload),
            MetricDisplayLayout.GetTextWidth(MetricId.NetworkUpload));
        Assert.Equal(
            MetricDisplayLayout.GetTextWidth(MetricId.StorageRead),
            MetricDisplayLayout.GetTextWidth(MetricId.StorageWrite));
    }

    private static MetricCategory GetCategory(MetricId id) => id switch
    {
        MetricId.CpuUtilization => MetricCategory.Cpu,
        MetricId.NetworkDownload => MetricCategory.Network,
        MetricId.StorageWrite => MetricCategory.Storage,
        _ => MetricCategory.Gpu
    };

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
