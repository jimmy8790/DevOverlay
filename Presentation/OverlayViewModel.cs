using System.Collections.ObjectModel;
using System.Windows.Threading;
using DevOverlay.Configuration;
using DevOverlay.Metrics;

namespace DevOverlay.Presentation;

public sealed class OverlayViewModel : ObservableObject
{
    private OverlaySettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<MetricCategory, MetricGroupViewModel> _groupsByCategory = [];
    private readonly Dictionary<MetricId, MetricItemViewModel> _itemsById = [];
    private readonly Dictionary<MetricId, MetricSnapshot> _latestMetrics = [];
    private IReadOnlyList<DevOverlay.Peripherals.PeripheralBatteryReading> _peripherals = [];
    private readonly Dictionary<string, MetricItemViewModel> _peripheralItems = [];

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

        foreach (var metric in metrics)
        {
            _latestMetrics[metric.Id] = metric;
            ApplyMetric(metric);
        }
#if DEBUG
        if (Environment.GetEnvironmentVariable("DEVOVERLAY_PRESENTMON_TRACE") == "1")
        {
            var frameMetrics = metrics.Where(metric => metric.Category is MetricCategory.Frame or MetricCategory.Latency).ToArray();
            if (frameMetrics.Length > 0) DevOverlay.Platform.Windows.RuntimeDiagnostics.Write(
                $"[PresentTrace] ViewModelReceived {string.Join(';', frameMetrics.Select(metric => $"{metric.Id}={metric.IsAvailable}:{metric.Value}"))}");
        }
#endif
        UpdateMetricPairSpacing();
    }

    public void ApplySettings(OverlaySettings settings)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => ApplySettings(settings));
            return;
        }

        _settings = settings;
        var visibleIds = _latestMetrics.Values.Where(IsEnabled).Select(metric => metric.Id).ToHashSet();
        foreach (var (id, item) in _itemsById.ToArray())
        {
            if (visibleIds.Contains(id)) continue;
            var category = _latestMetrics[id].Category;
            _groupsByCategory[category].Items.Remove(item);
            _itemsById.Remove(id);
        }
        foreach (var (category, group) in _groupsByCategory.ToArray())
        {
            if (group.Items.Count != 0) continue;
            Groups.Remove(group);
            _groupsByCategory.Remove(category);
        }
        foreach (var metric in _latestMetrics.Values.OrderBy(metric => metric.Id)) ApplyMetric(metric);
        ApplyPeripheralBatteries(_peripherals);
        ReorderGroups();
        UpdateGroupSeparators();
        UpdateMetricPairSpacing();

        OnPropertyChanged(nameof(Settings));
    }

    public void MarkCategoryUnavailable(MetricCategory category)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => MarkCategoryUnavailable(category));
            return;
        }

        foreach (var metric in _latestMetrics.Values.Where(metric => metric.Category == category).ToArray())
        {
            var unavailable = MetricSnapshot.Unavailable(metric.Id, metric.Category, metric.DisplayName, metric.Unit);
            _latestMetrics[metric.Id] = unavailable;
            ApplyMetric(unavailable);
        }
    }

    public void ApplyPeripheralBatteries(IReadOnlyList<DevOverlay.Peripherals.PeripheralBatteryReading> readings)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => ApplyPeripheralBatteries(readings)); return; }
        _peripherals = readings;
        var visible = _settings.PeripheralBatteriesEnabled ? _settings.PeripheralDevices.Where(item => item.Show)
            .OrderBy(item => item.Type).ThenBy(item => item.Identity, StringComparer.Ordinal).ToArray() : [];
        const MetricCategory category = MetricCategory.PeripheralBattery;
        if (visible.Length == 0)
        {
            if (_groupsByCategory.Remove(category, out var previous)) Groups.Remove(previous);
            _peripheralItems.Clear(); UpdateGroupSeparators(); return;
        }
        if (!_groupsByCategory.TryGetValue(category, out var group))
        {
            group = new(category, string.Empty); _groupsByCategory.Add(category, group);
            Groups.Insert(GetGroupInsertionIndex(category), group);
        }
        var ids = visible.Select(item => item.Identity).ToHashSet();
        foreach (var id in _peripheralItems.Keys.Except(ids).ToArray())
        { group.Items.Remove(_peripheralItems[id]); _peripheralItems.Remove(id); }
        for (var index = 0; index < visible.Length; index++)
        {
            var preference = visible[index];
            var reading = readings.FirstOrDefault(item => item.Identity == preference.Identity);
            var percentage = reading?.ValidPercentage;
            var metric = new MetricSnapshot(MetricId.PeripheralBattery, category, preference.HudLabel,
                percentage, "%", percentage.HasValue, reading?.UpdatedAt ?? DateTimeOffset.UtcNow);
            if (!_peripheralItems.TryGetValue(preference.Identity, out var item))
            { item = new(metric); _peripheralItems.Add(preference.Identity, item); group.Items.Insert(index, item); }
            else { item.Update(metric); var oldIndex = group.Items.IndexOf(item); if (oldIndex != index) group.Items.Move(oldIndex, index); }
        }
        UpdateGroupSeparators(); UpdateMetricPairSpacing();
    }

    private void ApplyMetric(MetricSnapshot metric)
    {
        if (!IsEnabled(metric))
        {
            return;
        }

        var isNewGroup = !_groupsByCategory.TryGetValue(metric.Category, out var group);
        if (isNewGroup)
        {
            group = new MetricGroupViewModel(metric.Category, GetGroupTitle(metric.Category));
            _groupsByCategory.Add(metric.Category, group);
        }
        if (group is null) throw new InvalidOperationException("Metric group was not created.");

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
        if (isNewGroup)
        {
            Groups.Insert(GetGroupInsertionIndex(metric.Category), group);
            UpdateGroupSeparators();
        }
        UpdateMetricPairSpacing();
    }

    private void UpdateGroupSeparators()
    {
        for (var index = 0; index < Groups.Count; index++)
        {
            Groups[index].SetHasFollowingGroup(index < Groups.Count - 1);
        }
    }

    private void UpdateMetricPairSpacing()
    {
        foreach (var group in Groups)
            for (var index = 0; index < group.Items.Count; index++)
                group.Items[index].SetHasFollowingItem(index < group.Items.Count - 1);
    }

    private bool IsEnabled(MetricSnapshot metric) =>
        _settings.EnabledGroups.Contains(metric.Category) && _settings.EnabledMetrics.Contains(metric.Id);

    private static string GetGroupTitle(MetricCategory category) => category switch
    {
        MetricCategory.Cpu => "CPU",
        MetricCategory.Gpu => "GPU",
        MetricCategory.Storage => "DISK",
        MetricCategory.Network => "NET",
        MetricCategory.Frame => "FPS",
        MetricCategory.Latency => "LAT",
        // Codex and Claude metrics already carry CX / CL prefixes in the HUD.
        MetricCategory.AiUsage => string.Empty,
        MetricCategory.PeripheralBattery => string.Empty,
        _ => category.ToString()
    };

    private int GetGroupInsertionIndex(MetricCategory category) =>
        Groups.TakeWhile(group => GroupRank(group.Category) < GroupRank(category)).Count();

    private int GroupRank(MetricCategory category)
    {
        var order = OverlaySettings.NormalizeGroupOrder(_settings.GroupOrder);
        var index = Array.IndexOf(order.ToArray(), category);
        return index >= 0 ? index : order.Count + (int)category;
    }

    private void ReorderGroups()
    {
        var ordered = Groups.OrderBy(group => GroupRank(group.Category)).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var current = Groups.IndexOf(ordered[index]);
            if (current != index) Groups.Move(current, index);
        }
    }

    private static int GetMetricInsertionIndex(
        IEnumerable<MetricItemViewModel> items,
        MetricId metricId) =>
        items.TakeWhile(item => item.Id < metricId).Count();
}
