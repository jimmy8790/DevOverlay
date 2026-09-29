using System.Collections.ObjectModel;
using System.Windows.Threading;
using DevOverlay.Configuration;
using DevOverlay.Metrics;

namespace DevOverlay.Presentation;

public sealed class OverlayViewModel : ObservableObject
{
    private readonly OverlaySettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<MetricCategory, MetricGroupViewModel> _groupsByCategory = [];
    private readonly Dictionary<MetricId, MetricItemViewModel> _itemsById = [];

    public OverlayViewModel(OverlaySettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
    }

    public ObservableCollection<MetricGroupViewModel> Groups { get; } = [];
    public OverlaySettings Settings => _settings;

    public void ApplyMetrics(IReadOnlyCollection<MetricSnapshot> metrics)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => ApplyMetrics(metrics));
            return;
        }

        foreach (var metric in metrics.Where(IsEnabled))
        {
            if (!_groupsByCategory.TryGetValue(metric.Category, out var group))
            {
                group = new MetricGroupViewModel(metric.Category, GetGroupTitle(metric.Category));
                _groupsByCategory.Add(metric.Category, group);
                Groups.Insert(GetGroupInsertionIndex(metric.Category), group);
            }

            if (_itemsById.TryGetValue(metric.Id, out var item))
            {
                item.Update(metric);
            }
            else
            {
                item = new MetricItemViewModel(metric);
                _itemsById.Add(metric.Id, item);
                group.Items.Insert(GetMetricInsertionIndex(group.Items, metric.Id), item);
            }
        }
    }

    private bool IsEnabled(MetricSnapshot metric) =>
        _settings.EnabledGroups.Contains(metric.Category) && _settings.EnabledMetrics.Contains(metric.Id);

    private static string GetGroupTitle(MetricCategory category) => category switch
    {
        MetricCategory.Cpu => "CPU",
        MetricCategory.Gpu => "GPU",
        MetricCategory.Frame => "성능",
        MetricCategory.Latency => "LAT",
        _ => category.ToString()
    };

    private int GetGroupInsertionIndex(MetricCategory category) =>
        Groups.TakeWhile(group => group.Category < category).Count();

    private static int GetMetricInsertionIndex(
        IEnumerable<MetricItemViewModel> items,
        MetricId metricId) =>
        items.TakeWhile(item => item.Id < metricId).Count();
}
