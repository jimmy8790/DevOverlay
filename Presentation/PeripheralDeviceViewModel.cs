using DevOverlay.Peripherals;

namespace DevOverlay.Presentation;

public sealed class PeripheralDeviceViewModel : ObservableObject
{
    private PeripheralPreference _preference;
    private PeripheralBatteryReading? _reading;
    private readonly Action<PeripheralPreference> _changed;
    internal PeripheralDeviceViewModel(PeripheralPreference preference, Action<PeripheralPreference> changed)
    { _preference = preference; _changed = changed; }
    public string Identity => _preference.Identity;
    public string FriendlyName => _preference.FriendlyName;
    public string DefaultLabel => _preference.DefaultLabel;
    public string StatusText => $"{_preference.Type} · {(_reading?.Connected == true ? "Connected" : "Disconnected")} · {(_reading?.ValidPercentage is { } value ? $"{value:0}%" : "N/A")} · {_reading?.Source.ToString() ?? "Unavailable"}";
    public string Detail => _reading?.Status ?? "Saved device; not currently detected";
    // A device that is currently connected is never forgettable; this only removes DevOverlay's saved settings.
    public bool IsConnected => _reading?.Connected == true;
    public bool CanForget => !IsConnected;
    public System.Windows.Visibility ForgetVisibility => CanForget ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public bool Show { get => _preference.Show; set { if (value == Show) return; _preference = _preference with { Show = value }; OnPropertyChanged(); _changed(_preference); } }
    public string CustomName
    {
        get => _preference.CustomName;
        set { var normalized = PeripheralPreference.NormalizeName(value); if (normalized == CustomName) return;
            _preference = _preference with { CustomName = normalized }; OnPropertyChanged(); _changed(_preference); }
    }
    internal void Update(PeripheralBatteryReading? reading)
    { _reading = reading; OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(CanForget)); OnPropertyChanged(nameof(ForgetVisibility)); }
    internal void UpdatePreference(PeripheralPreference preference)
    {
        _preference = preference;
        OnPropertyChanged(nameof(FriendlyName)); OnPropertyChanged(nameof(DefaultLabel));
        OnPropertyChanged(nameof(Show)); OnPropertyChanged(nameof(CustomName)); OnPropertyChanged(nameof(StatusText));
    }
}
