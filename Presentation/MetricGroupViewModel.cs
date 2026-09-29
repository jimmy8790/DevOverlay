using System.Collections.ObjectModel;
using DevOverlay.Metrics;

namespace DevOverlay.Presentation;

public sealed class MetricGroupViewModel(MetricCategory category, string title) : ObservableObject
{
    public MetricCategory Category { get; } = category;
    public string Title { get; } = title;
    public ObservableCollection<MetricItemViewModel> Items { get; } = [];
}

public sealed class MetricItemViewModel(MetricSnapshot metric) : ObservableObject
{
    private MetricSnapshot _metric = metric;

    public MetricId Id => _metric.Id;
    public string Text => !_metric.IsAvailable || _metric.Value is null
        ? "—"
        : $"{FormatValue(_metric.Value.Value)}{_metric.Unit}";

    public void Update(MetricSnapshot metric)
    {
        _metric = metric;
        OnPropertyChanged(nameof(Text));
    }

    private static string FormatValue(double value) => value % 1 == 0 ? value.ToString("0") : value.ToString("0.0");
}
