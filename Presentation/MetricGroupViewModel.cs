using System.Collections.ObjectModel;
using System.Windows;
using DevOverlay.Metrics;

namespace DevOverlay.Presentation;

public sealed class MetricGroupViewModel(MetricCategory category, string title) : ObservableObject
{
    private bool _hasFollowingGroup;

    public MetricCategory Category { get; } = category;
    public string Title { get; } = title;
    public ObservableCollection<MetricItemViewModel> Items { get; } = [];
    public bool HasFollowingGroup => _hasFollowingGroup;
    public Visibility SeparatorVisibility => _hasFollowingGroup ? Visibility.Visible : Visibility.Collapsed;

    internal void SetHasFollowingGroup(bool value)
    {
        if (_hasFollowingGroup == value)
        {
            return;
        }

        _hasFollowingGroup = value;
        OnPropertyChanged(nameof(HasFollowingGroup));
        OnPropertyChanged(nameof(SeparatorVisibility));
    }
}

public sealed class MetricItemViewModel(MetricSnapshot metric) : ObservableObject
{
    private MetricSnapshot _metric = metric;
    private bool _hasFollowingItem;

    public MetricId Id => _metric.Id;
    public double DisplayWidth => MetricDisplayLayout.GetTextWidth(Id);
    public double PrefixWidth => MetricDisplayLayout.GetPrefixWidth(Id);
    public string Text => MetricTextFormatter.Format(_metric);
    public string Prefix => MetricTextFormatter.GetPrefix(_metric);
    public string ValueText => MetricTextFormatter.FormatValue(_metric);
    public Visibility PrefixVisibility => Id is MetricId.BatteryRemaining || !string.IsNullOrEmpty(Prefix) ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Only non-final metric pairs consume the configurable inter-item gap.</summary>
    public bool HasFollowingItem => _hasFollowingItem;

    internal void SetHasFollowingItem(bool value)
    {
        if (_hasFollowingItem == value) return;
        _hasFollowingItem = value;
        OnPropertyChanged(nameof(HasFollowingItem));
    }

    public void Update(MetricSnapshot metric)
    {
        _metric = metric;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Prefix));
        OnPropertyChanged(nameof(PrefixVisibility));
        OnPropertyChanged(nameof(ValueText));
    }
}
