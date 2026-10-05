using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.SensorHostProtocol;

namespace DevOverlay.Presentation;

/// <summary>Small settings surface for the metrics that currently exist in the runtime.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private bool _cpuEnabled;
    private bool _gpuEnabled;
    private bool _networkEnabled;
    private bool _storageEnabled;
    private bool _fpsEnabled;
    private bool _latencyEnabled;
    private bool _aiUsageEnabled;
    private bool _cpuUsageEnabled;
    private bool _cpuTemperatureEnabled;
    private bool _cpuPowerEnabled;
    private bool _gpuUsageEnabled;
    private bool _gpuTemperatureEnabled;
    private bool _gpuPowerEnabled;
    private bool _gpuVramEnabled;
    private bool _networkDownloadEnabled;
    private bool _networkUploadEnabled;
    private bool _networkTodayTotalEnabled;
    private bool _storageReadEnabled;
    private bool _storageWriteEnabled;
    private bool _framesPerSecondEnabled;
    private bool _onePercentLowEnabled;
    private bool _frameTimeEnabled;
    private bool _renderLatencyEnabled;
    private bool _codexAccountLimitsEnabled;
    private bool _claudeUsageEnabled;
    private string _codexStatusText = "Codex: Disabled";
    private string _codexSetupHelpText = "Codex CLI is optional. Enable Codex Account Limits when you want DevOverlay to read account limits.";
    private string _claudeStatusText = "Claude: Disabled";
    private string _claudeSetupHelpText = "Claude Code is optional. Enable Claude Usage, then configure the official statusLine bridge.";
    private string _gpuDeviceId;
    private string _networkDeviceId;
    private string _storageDeviceId;
    private string _fpsTargetId;
    private int _refreshIntervalMs;
    private string _refreshIntervalText;
    private ObservableCollection<DeviceChoice> _gpuDevices;
    private ObservableCollection<DeviceChoice> _networkDevices;
    private ObservableCollection<DeviceChoice> _storageDevices;
    private ObservableCollection<DeviceChoice> _fpsTargets;
    private string _presentMonStatusText = "PresentMon Service: Checking...";
    private string _fpsTelemetryStatusText = "FPS telemetry: Waiting for target";
    private OverlaySettings _baseSettings;
    private OverlayPosition _position;
    private readonly ObservableCollection<GroupOrderChoice> _groupOrder;
    private OverlayAppearance _appearance;
    private string _backgroundColor;
    private double _backgroundOpacityPercent;
    private string _valueTextColor;
    private string _labelTextColor;
    private double _spacing;
    private readonly bool _isOverlayVisible;
    private bool _isUpdatingDeviceChoices;
    private string _pawnIoStatusText = "Checking PawnIO...";
    private bool _canInstallPawnIo;
    private string _pawnIoActionText = "Install PawnIO";
    private string _pawnIoDriverStatusText = "Driver access: Checking...";
    private string _cpuSensorStatusText = "CPU Package sensors: Checking...";
    private bool _canRetryCpuSensors;
    private string _sensorHostStatusText = "Sensor Host: Not running";
    private bool _canStartSensorHost = true;
    private string _sensorHostActionText = "Start CPU Sensor Host";
    private string _sensorServiceStatusText = "Sensor Service: Checking...";
    private string _sensorServiceConnectionText = "Service connection: Waiting...";
    private string _sensorServiceActionMessage = string.Empty;
    private bool _canInstallSensorService;
    private bool _canRepairSensorService;
    private bool _canUninstallSensorService;
    private bool _sensorServiceActionBusy;
    private SensorServiceStatus _lastSensorServiceStatus;
    private OverlayHotkey _hotkey;
    private string _hotkeyStatusText = "Global hotkey: Checking...";

    public SettingsViewModel(OverlaySettings settings)
    {
        _baseSettings = settings;
        _position = settings.Position;
        _groupOrder = new ObservableCollection<GroupOrderChoice>(
            OverlaySettings.NormalizeGroupOrder(settings.GroupOrder).Select(category => new GroupOrderChoice(category)));
        UpdateOrderControls();
        _appearance = settings.Appearance.Normalize();
        _backgroundColor = _appearance.BackgroundColor;
        _backgroundOpacityPercent = _appearance.BackgroundOpacity * 100;
        _valueTextColor = _appearance.ValueTextColor;
        _labelTextColor = _appearance.LabelTextColor;
        _spacing = _appearance.Spacing;
        _cpuEnabled = settings.EnabledGroups.Contains(MetricCategory.Cpu);
        _gpuEnabled = settings.EnabledGroups.Contains(MetricCategory.Gpu);
        _networkEnabled = settings.EnabledGroups.Contains(MetricCategory.Network);
        _storageEnabled = settings.EnabledGroups.Contains(MetricCategory.Storage);
        _fpsEnabled = settings.EnabledGroups.Contains(MetricCategory.Frame);
        _latencyEnabled = settings.EnabledGroups.Contains(MetricCategory.Latency);
        _aiUsageEnabled = settings.EnabledGroups.Contains(MetricCategory.AiUsage);
        _cpuUsageEnabled = settings.EnabledMetrics.Contains(MetricId.CpuUtilization);
        _cpuTemperatureEnabled = settings.EnabledMetrics.Contains(MetricId.CpuTemperature);
        _cpuPowerEnabled = settings.EnabledMetrics.Contains(MetricId.CpuPower);
        _gpuUsageEnabled = settings.EnabledMetrics.Contains(MetricId.GpuUtilization);
        _gpuTemperatureEnabled = settings.EnabledMetrics.Contains(MetricId.GpuTemperature);
        _gpuPowerEnabled = settings.EnabledMetrics.Contains(MetricId.GpuPower);
        _gpuVramEnabled = settings.EnabledMetrics.Contains(MetricId.GpuVramUsed);
        _networkDownloadEnabled = settings.EnabledMetrics.Contains(MetricId.NetworkDownload);
        _networkUploadEnabled = settings.EnabledMetrics.Contains(MetricId.NetworkUpload);
        _networkTodayTotalEnabled = settings.EnabledMetrics.Contains(MetricId.NetworkTodayTotal);
        _storageReadEnabled = settings.EnabledMetrics.Contains(MetricId.StorageRead);
        _storageWriteEnabled = settings.EnabledMetrics.Contains(MetricId.StorageWrite);
        _framesPerSecondEnabled = settings.EnabledMetrics.Contains(MetricId.FramesPerSecond);
        _onePercentLowEnabled = settings.EnabledMetrics.Contains(MetricId.OnePercentLow);
        _frameTimeEnabled = settings.EnabledMetrics.Contains(MetricId.FrameTime);
        _renderLatencyEnabled = settings.EnabledMetrics.Contains(MetricId.Latency);
        _codexAccountLimitsEnabled = settings.EnabledMetrics.Contains(MetricId.CodexPrimaryRateLimit);
        _claudeUsageEnabled = settings.EnabledMetrics.Contains(MetricId.ClaudePrimaryRateLimit);
        _gpuDeviceId = ToDeviceId(settings.GpuDeviceSelection);
        _networkDeviceId = ToDeviceId(settings.NetworkDeviceSelection);
        _storageDeviceId = ToDeviceId(settings.StorageDeviceSelection);
        _fpsTargetId = ToDeviceId(settings.FpsTargetSelection);
        _refreshIntervalMs = OverlaySettings.NormalizeRefreshIntervalMs(settings.RefreshIntervalMs);
        _refreshIntervalText = _refreshIntervalMs.ToString(CultureInfo.InvariantCulture);
        _isOverlayVisible = settings.IsVisible;
        _hotkey = settings.Hotkey.Normalize();
        _gpuDevices = CreateDeviceChoices([], _gpuDeviceId);
        _networkDevices = CreateDeviceChoices([], _networkDeviceId);
        _storageDevices = CreateDeviceChoices([], _storageDeviceId, includeSystemDrive: true);
        _fpsTargets = CreateDeviceChoices([], _fpsTargetId, isFpsTarget: true);
    }

    public event Action<OverlaySettings>? SettingsChanged;
    public event Action? MoveOverlayRequested;
    public event Action? PawnIoInstallRequested;
    public event Action? CpuSensorRetryRequested;
    public event Action? SensorHostStartRequested;
    public event Action? PresentMonInstallRequested;
    public event Action? CodexRecheckRequested;
    public event Action? ClaudeRecheckRequested;
    internal event Action<SensorServiceAction>? SensorServiceActionRequested;
    internal event Action<OverlayHotkey>? HotkeyChangeRequested;
    internal event Action<bool>? HotkeyCaptureChanged;

    public ObservableCollection<DeviceChoice> GpuDevices => _gpuDevices;
    public ObservableCollection<DeviceChoice> NetworkDevices => _networkDevices;
    public ObservableCollection<DeviceChoice> StorageDevices => _storageDevices;
    public ObservableCollection<DeviceChoice> FpsTargets => _fpsTargets;
    public ObservableCollection<GroupOrderChoice> GroupOrderChoices => _groupOrder;
    // All saved devices. The Settings list shows VisiblePeripheralDevices: only connected ones unless the user asks for the rest.
    public ObservableCollection<PeripheralDeviceViewModel> PeripheralDevices { get; } = [];
    public ObservableCollection<PeripheralDeviceViewModel> VisiblePeripheralDevices { get; } = [];
    private bool _showDisconnectedPeripherals;
    public bool ShowDisconnectedPeripherals
    {
        get => _showDisconnectedPeripherals;
        set { if (value == _showDisconnectedPeripherals) return; _showDisconnectedPeripherals = value; OnPropertyChanged(); RefreshVisiblePeripherals(); }
    }
    public string PeripheralListHintText => VisiblePeripheralDevices.Count > 0 ? string.Empty
        : PeripheralDevices.Count == 0 ? "No peripherals detected yet."
        : "No connected peripherals. Tick \"Show disconnected saved devices\" to manage saved ones.";
    private void RefreshVisiblePeripherals()
    {
        var wanted = PeripheralDevices.Where(row => ShowDisconnectedPeripherals || row.IsConnected).ToList();
        for (var index = VisiblePeripheralDevices.Count - 1; index >= 0; index--)
            if (!wanted.Contains(VisiblePeripheralDevices[index])) VisiblePeripheralDevices.RemoveAt(index);
        for (var index = 0; index < wanted.Count; index++)
        {
            if (index < VisiblePeripheralDevices.Count && VisiblePeripheralDevices[index] == wanted[index]) continue;
            var existing = VisiblePeripheralDevices.IndexOf(wanted[index]);
            if (existing >= 0) VisiblePeripheralDevices.Move(existing, index); else VisiblePeripheralDevices.Insert(index, wanted[index]);
        }
        OnPropertyChanged(nameof(PeripheralListHintText));
    }
    internal Func<CancellationToken, Task<string>>? PeripheralDiagnosticsFactory { get; set; }

    internal event Action? UpdateCheckRequested;
    internal event Action<string>? LinkOpenRequested;
    internal event Action<string>? PeripheralForgotten;
    private string _updateStatusText = "Checking for updates...";
    private string _updateDetailText = string.Empty;
    private bool _canViewRelease;
    private bool _canCheckForUpdates;
    private string? _releaseUrl;
    public string AboutNameText => DevOverlay.Updates.AppLinks.ProductName;
    public string VersionText { get; } = DevOverlay.Updates.ReleaseVersion.Running(typeof(SettingsViewModel).Assembly) is { } running
        ? $"Version {running}" : "Version unknown";
    public string UpdateStatusText { get => _updateStatusText; private set => SetStatusProperty(ref _updateStatusText, value); }
    public string UpdateDetailText { get => _updateDetailText; private set => SetStatusProperty(ref _updateDetailText, value); }
    public bool CanViewRelease { get => _canViewRelease; private set => SetStatusProperty(ref _canViewRelease, value); }
    public bool CanCheckForUpdates { get => _canCheckForUpdates; private set => SetStatusProperty(ref _canCheckForUpdates, value); }
    internal void RequestUpdateCheck() { if (CanCheckForUpdates) UpdateCheckRequested?.Invoke(); }
    internal void RequestOpenRepository() => LinkOpenRequested?.Invoke(DevOverlay.Updates.AppLinks.RepositoryUrl);
    internal void RequestOpenReleases() => LinkOpenRequested?.Invoke(DevOverlay.Updates.AppLinks.ReleasesUrl);
    internal void RequestOpenLatestRelease() { if (_releaseUrl is not null) LinkOpenRequested?.Invoke(_releaseUrl); }
    internal void UpdateUpdateStatus(DevOverlay.Updates.UpdateStatus status)
    {
        _releaseUrl = status.State == DevOverlay.Updates.UpdateState.UpdateAvailable ? status.ReleaseUrl : null;
        CanViewRelease = _releaseUrl is not null;
        CanCheckForUpdates = status.State != DevOverlay.Updates.UpdateState.Checking;
        var latest = status.Latest is { } version ? $"v{version}" : null;
        (UpdateStatusText, UpdateDetailText) = status.State switch
        {
            DevOverlay.Updates.UpdateState.Checking => ("Checking for updates...", string.Empty),
            DevOverlay.Updates.UpdateState.UpdateAvailable => ($"A new version is available: {latest}", string.Empty),
            DevOverlay.Updates.UpdateState.UpToDate => ("You're up to date.", status.BuildIsNewer
                ? $"This build is newer than the latest published release ({latest})." : $"Latest version: {latest}"),
            _ => ("Could not check for updates.", UpdateFailureText(status))
        };
    }

    private static string UpdateFailureText(DevOverlay.Updates.UpdateStatus status) => status.Failure switch
    {
        DevOverlay.Updates.UpdateFailure.Offline => "GitHub could not be reached.",
        DevOverlay.Updates.UpdateFailure.Timeout => "GitHub did not respond in time.",
        DevOverlay.Updates.UpdateFailure.RateLimited => "GitHub's request limit was reached. Try again later.",
        DevOverlay.Updates.UpdateFailure.HttpStatus => $"GitHub returned HTTP {status.StatusCode}.",
        DevOverlay.Updates.UpdateFailure.NoRelease => "No published release was found.",
        DevOverlay.Updates.UpdateFailure.UnknownInstalledVersion => "The installed version could not be determined.",
        _ => "GitHub sent an unexpected response."
    };
    // Removes only this identity's saved DevOverlay settings. No device, driver, pairing or receiver is touched.
    internal bool ForgetPeripheral(string identity)
    {
        var row = PeripheralDevices.FirstOrDefault(item => item.Identity == identity);
        if (row is null || !row.CanForget) return false;
        _baseSettings = _baseSettings with { PeripheralDevices = _baseSettings.PeripheralDevices
            .Where(item => item.Identity != identity).ToArray() };
        PeripheralDevices.Remove(row);
        RefreshVisiblePeripherals();
        // The runtime memory goes first so a refresh in between cannot re-add the saved entry.
        PeripheralForgotten?.Invoke(identity);
        SettingsChanged?.Invoke(BuildSettings());
        return true;
    }
    public bool PeripheralBatteriesEnabled
    {
        get => _baseSettings.PeripheralBatteriesEnabled;
        set { if (value == PeripheralBatteriesEnabled) return; _baseSettings = _baseSettings with { PeripheralBatteriesEnabled = value };
            OnPropertyChanged(); SettingsChanged?.Invoke(BuildSettings()); }
    }
    public void UpdatePeripheralDevices(IReadOnlyList<DevOverlay.Peripherals.PeripheralBatteryReading> readings,
        IReadOnlyList<DevOverlay.Peripherals.PeripheralPreference> preferences)
    {
        _baseSettings = _baseSettings with { PeripheralDevices = preferences };
        var identities = preferences.Select(item => item.Identity).ToHashSet();
        foreach (var previous in PeripheralDevices.Where(item => !identities.Contains(item.Identity)).ToArray())
            PeripheralDevices.Remove(previous);
        foreach (var preference in preferences.OrderBy(item => item.Type).ThenBy(item => item.Identity, StringComparer.Ordinal))
        {
            var row = PeripheralDevices.FirstOrDefault(item => item.Identity == preference.Identity);
            if (row is null)
            {
                row = new(preference, changed =>
                {
                    _baseSettings = _baseSettings with { PeripheralDevices = _baseSettings.PeripheralDevices
                        .Select(item => item.Identity == changed.Identity ? changed : item).ToArray() };
                    SettingsChanged?.Invoke(BuildSettings());
                });
                PeripheralDevices.Add(row);
            }
            row.UpdatePreference(preference);
            row.Update(readings.FirstOrDefault(item => item.Identity == preference.Identity));
        }
        var ordered = PeripheralDevices.OrderBy(item => preferences.First(preference => preference.Identity == item.Identity).Type)
            .ThenBy(item => item.Identity, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        { var previous = PeripheralDevices.IndexOf(ordered[index]); if (previous != index) PeripheralDevices.Move(previous, index); }
        RefreshVisiblePeripherals();
    }

    public bool CpuEnabled { get => _cpuEnabled; set => SetAndNotify(ref _cpuEnabled, value); }
    public bool GpuEnabled { get => _gpuEnabled; set => SetAndNotify(ref _gpuEnabled, value); }
    public bool NetworkEnabled { get => _networkEnabled; set => SetAndNotify(ref _networkEnabled, value); }
    public bool StorageEnabled { get => _storageEnabled; set => SetAndNotify(ref _storageEnabled, value); }
    public bool FpsEnabled { get => _fpsEnabled; set => SetAndNotify(ref _fpsEnabled, value); }
    public bool LatencyEnabled { get => _latencyEnabled; set => SetAndNotify(ref _latencyEnabled, value); }
    public bool AiUsageEnabled { get => _aiUsageEnabled; set => SetAndNotify(ref _aiUsageEnabled, value); }
    public bool CpuUsageEnabled { get => _cpuUsageEnabled; set => SetAndNotify(ref _cpuUsageEnabled, value); }
    public bool CpuTemperatureEnabled { get => _cpuTemperatureEnabled; set => SetAndNotify(ref _cpuTemperatureEnabled, value); }
    public bool CpuPowerEnabled { get => _cpuPowerEnabled; set => SetAndNotify(ref _cpuPowerEnabled, value); }
    public bool GpuUsageEnabled { get => _gpuUsageEnabled; set => SetAndNotify(ref _gpuUsageEnabled, value); }
    public bool GpuTemperatureEnabled { get => _gpuTemperatureEnabled; set => SetAndNotify(ref _gpuTemperatureEnabled, value); }
    public bool GpuPowerEnabled { get => _gpuPowerEnabled; set => SetAndNotify(ref _gpuPowerEnabled, value); }
    public bool GpuVramEnabled { get => _gpuVramEnabled; set => SetAndNotify(ref _gpuVramEnabled, value); }
    public bool NetworkDownloadEnabled { get => _networkDownloadEnabled; set => SetAndNotify(ref _networkDownloadEnabled, value); }
    public bool NetworkUploadEnabled { get => _networkUploadEnabled; set => SetAndNotify(ref _networkUploadEnabled, value); }
    public bool NetworkTodayTotalEnabled { get => _networkTodayTotalEnabled; set => SetAndNotify(ref _networkTodayTotalEnabled, value); }
    public bool StorageReadEnabled { get => _storageReadEnabled; set => SetAndNotify(ref _storageReadEnabled, value); }
    public bool StorageWriteEnabled { get => _storageWriteEnabled; set => SetAndNotify(ref _storageWriteEnabled, value); }
    public bool FramesPerSecondEnabled { get => _framesPerSecondEnabled; set => SetAndNotify(ref _framesPerSecondEnabled, value); }
    public bool OnePercentLowEnabled { get => _onePercentLowEnabled; set => SetAndNotify(ref _onePercentLowEnabled, value); }
    public bool FrameTimeEnabled { get => _frameTimeEnabled; set => SetAndNotify(ref _frameTimeEnabled, value); }
    public bool RenderLatencyEnabled { get => _renderLatencyEnabled; set => SetAndNotify(ref _renderLatencyEnabled, value); }
    public bool CodexAccountLimitsEnabled { get => _codexAccountLimitsEnabled; set => SetAndNotify(ref _codexAccountLimitsEnabled, value); }
    public bool ClaudeUsageEnabled { get => _claudeUsageEnabled; set => SetAndNotify(ref _claudeUsageEnabled, value); }
    public string CodexStatusText { get => _codexStatusText; private set => SetStatusProperty(ref _codexStatusText, value); }
    public string CodexSetupHelpText { get => _codexSetupHelpText; private set => SetStatusProperty(ref _codexSetupHelpText, value); }
    public string ClaudeStatusText { get => _claudeStatusText; private set => SetStatusProperty(ref _claudeStatusText, value); }
    public string ClaudeSetupHelpText { get => _claudeSetupHelpText; private set => SetStatusProperty(ref _claudeSetupHelpText, value); }
    /// <summary>Exact persisted value. Slider interaction is rounded to its 50 ms step; direct text input is not.</summary>
    public int RefreshIntervalMs { get => _refreshIntervalMs; set => SetRefreshInterval(value); }
    public int RefreshIntervalSliderValue { get => _refreshIntervalMs; set => SetRefreshIntervalFromSlider(value); }
    public string RefreshIntervalText { get => _refreshIntervalText; set => SetRefreshIntervalText(value); }
    public int RefreshIntervalMinimumMs => OverlaySettings.MinimumRefreshIntervalMs;
    public int RefreshIntervalMaximumMs => OverlaySettings.MaximumRefreshIntervalMs;
    public string PresentMonStatusText { get => _presentMonStatusText; private set => SetStatusProperty(ref _presentMonStatusText, value); }
    public string FpsTelemetryStatusText { get => _fpsTelemetryStatusText; private set => SetStatusProperty(ref _fpsTelemetryStatusText, value); }
    public string BackgroundColor { get => _backgroundColor; set => SetColor(ref _backgroundColor, value); }
    public double BackgroundOpacityPercent { get => _backgroundOpacityPercent; set => SetOpacity(value); }
    public string ValueTextColor { get => _valueTextColor; set => SetColor(ref _valueTextColor, value); }
    public string LabelTextColor { get => _labelTextColor; set => SetColor(ref _labelTextColor, value); }
    /// <summary>Gap density as a percentage; lower is tighter.</summary>
    public double SpacingPercent { get => Math.Round(_spacing * 100, 2); set => SetSpacing(value / 100); }
    public double SpacingMinimumPercent => OverlayAppearance.MinSpacing * 100;
    public double SpacingMaximumPercent => OverlayAppearance.MaxSpacing * 100;
    public string PositionModeText => _position.Mode == OverlayPositionMode.Custom ? "Custom" : "Auto";
    public string PawnIoStatusText { get => _pawnIoStatusText; private set => SetStatusProperty(ref _pawnIoStatusText, value); }
    public bool CanInstallPawnIo { get => _canInstallPawnIo; private set => SetStatusProperty(ref _canInstallPawnIo, value); }
    public string PawnIoActionText { get => _pawnIoActionText; private set => SetStatusProperty(ref _pawnIoActionText, value); }
    public string PawnIoDriverStatusText { get => _pawnIoDriverStatusText; private set => SetStatusProperty(ref _pawnIoDriverStatusText, value); }
    public string CpuSensorStatusText { get => _cpuSensorStatusText; private set => SetStatusProperty(ref _cpuSensorStatusText, value); }
    public bool CanRetryCpuSensors { get => _canRetryCpuSensors; private set => SetStatusProperty(ref _canRetryCpuSensors, value); }
    public string SensorHostStatusText { get => _sensorHostStatusText; private set => SetStatusProperty(ref _sensorHostStatusText, value); }
    public bool CanStartSensorHost { get => _canStartSensorHost; private set => SetStatusProperty(ref _canStartSensorHost, value); }
    public string SensorHostActionText { get => _sensorHostActionText; private set => SetStatusProperty(ref _sensorHostActionText, value); }
    public string SensorServiceStatusText { get => _sensorServiceStatusText; private set => SetStatusProperty(ref _sensorServiceStatusText, value); }
    public string SensorServiceConnectionText { get => _sensorServiceConnectionText; private set => SetStatusProperty(ref _sensorServiceConnectionText, value); }
    public string SensorServiceActionMessage { get => _sensorServiceActionMessage; private set => SetStatusProperty(ref _sensorServiceActionMessage, value); }
    public string HotkeyText => FormatHotkey(_hotkey);
    public string HotkeyStatusText { get => _hotkeyStatusText; private set => SetStatusProperty(ref _hotkeyStatusText, value); }
    public bool CanInstallSensorService { get => _canInstallSensorService; private set => SetStatusProperty(ref _canInstallSensorService, value); }
    public bool CanRepairSensorService { get => _canRepairSensorService; private set => SetStatusProperty(ref _canRepairSensorService, value); }
    public bool CanUninstallSensorService { get => _canUninstallSensorService; private set => SetStatusProperty(ref _canUninstallSensorService, value); }
    public string GpuDeviceId { get => _gpuDeviceId; set => SetDeviceId(ref _gpuDeviceId, value); }
    public string NetworkDeviceId { get => _networkDeviceId; set => SetDeviceId(ref _networkDeviceId, value); }
    public string StorageDeviceId { get => _storageDeviceId; set => SetDeviceId(ref _storageDeviceId, value); }
    public string FpsTargetId { get => _fpsTargetId; set => SetDeviceId(ref _fpsTargetId, value); }
    public DeviceChoice SelectedGpuDevice { get => ResolveChoice(GpuDevices, GpuDeviceId); set { if (value is not null) GpuDeviceId = value.Id; } }
    public DeviceChoice SelectedNetworkDevice { get => ResolveChoice(NetworkDevices, NetworkDeviceId); set { if (value is not null) NetworkDeviceId = value.Id; } }
    public DeviceChoice SelectedStorageDevice { get => ResolveChoice(StorageDevices, StorageDeviceId); set { if (value is not null) StorageDeviceId = value.Id; } }
    public DeviceChoice SelectedFpsTarget { get => ResolveChoice(FpsTargets, FpsTargetId); set { if (value is not null) FpsTargetId = value.Id; } }

    public void UpdateGpuDevices(IEnumerable<DeviceDescriptor> devices)
    {
        UpdateDeviceChoices(_gpuDevices, devices, GpuDeviceId, nameof(SelectedGpuDevice));
    }

    public void UpdateNetworkDevices(IEnumerable<DeviceDescriptor> devices)
    {
        UpdateDeviceChoices(_networkDevices, devices, NetworkDeviceId, nameof(SelectedNetworkDevice));
    }

    public void UpdateStorageDevices(IEnumerable<DeviceDescriptor> devices)
    {
        UpdateDeviceChoices(_storageDevices, devices, StorageDeviceId, nameof(SelectedStorageDevice), includeSystemDrive: true);
    }

    public void UpdateFpsTargets(IEnumerable<DeviceDescriptor> targets) =>
        UpdateDeviceChoices(_fpsTargets, targets, FpsTargetId, nameof(SelectedFpsTarget), isFpsTarget: true);

    internal void UpdatePresentMonStatus(PresentMonStatus status) => PresentMonStatusText = status.State switch
    {
        PresentMonRunState.NotInstalled => "PresentMon Service: Not installed",
        PresentMonRunState.Stopped => "PresentMon Service: Stopped",
        PresentMonRunState.Starting => "PresentMon Service: Starting",
        PresentMonRunState.Running => $"PresentMon Service: Running ({status.Version ?? "version unknown"})",
        _ => $"PresentMon Service: Unavailable ({status.Version ?? "unknown version"})"
    };

    internal void UpdateFpsTelemetryAvailability(bool available) => FpsTelemetryStatusText = available
        ? "FPS telemetry: Available" : "FPS telemetry: Unavailable / no presenting target";

    public void RequestMoveOverlay()
    {
        MoveOverlayRequested?.Invoke();
    }
    public void RequestPawnIoInstall() => PawnIoInstallRequested?.Invoke();
    public void RequestCpuSensorRetry() => CpuSensorRetryRequested?.Invoke();
    public void RequestSensorHostStart() => SensorHostStartRequested?.Invoke();
    public void RequestPresentMonInstall() => PresentMonInstallRequested?.Invoke();
    public void RequestCodexRecheck() => CodexRecheckRequested?.Invoke();
    public void RequestClaudeRecheck() => ClaudeRecheckRequested?.Invoke();
    internal void RequestSensorServiceAction(SensorServiceAction action) => SensorServiceActionRequested?.Invoke(action);
    internal void RequestHotkeyChange(OverlayHotkey hotkey) => HotkeyChangeRequested?.Invoke(hotkey);
    internal void SetHotkeyCaptureActive(bool active) => HotkeyCaptureChanged?.Invoke(active);

    internal void UpdateHotkey(OverlayHotkey hotkey, string status)
    {
        _hotkey = hotkey.Normalize();
        _baseSettings = _baseSettings with { Hotkey = _hotkey };
        OnPropertyChanged(nameof(HotkeyText));
        HotkeyStatusText = status;
    }

    internal void UpdateHotkeyStatus(string status) => HotkeyStatusText = status;

    internal void UpdateCodexStatus(CodexRateLimitState state, string? detail = null)
    {
        CodexStatusText = state switch
        {
            CodexRateLimitState.Disabled => "Codex: Disabled",
            CodexRateLimitState.Starting => "Codex: Starting...",
            CodexRateLimitState.CliFound => "Codex: Found, not connected",
            CodexRateLimitState.Connected => "Codex: Connected",
            CodexRateLimitState.CliNotFound => "Codex: CLI not found",
            CodexRateLimitState.ProtocolUnsupported => "Codex: App Server unsupported",
            CodexRateLimitState.Unavailable => "Codex: N/A",
            _ => "Codex: Connection error"
        };
        CodexSetupHelpText = state switch
        {
            CodexRateLimitState.Connected => "HUD percentages show remaining account quota. Codex CLI manages its own sign-in; DevOverlay does not read or store API keys, tokens, cookies, or auth files.",
            CodexRateLimitState.CliFound => "Codex CLI를 찾았습니다. Codex Account Limits를 켜고 Recheck Codex로 로컬 App Server를 확인하세요.",
            CodexRateLimitState.CliNotFound => "Codex CLI가 없습니다. 아래 설정 가이드를 확인하세요.",
            CodexRateLimitState.ProtocolUnsupported => "Codex App Server를 사용할 수 없습니다. Codex CLI를 업데이트한 뒤 Recheck Codex를 누르세요.",
            CodexRateLimitState.Unavailable or CodexRateLimitState.Error => "현재 account quota를 읽을 수 없습니다. codex doctor를 확인한 뒤 Recheck Codex를 누르세요.",
            _ => "Codex CLI is optional. Enable Codex Account Limits when you want DevOverlay to read account limits."
        };
    }

    internal void UpdateClaudeStatus(ClaudeUsageState state, string? detail = null)
    {
        ClaudeStatusText = state switch
        {
            ClaudeUsageState.Disabled => "Claude: Disabled",
            ClaudeUsageState.CliNotFound => "Claude: CLI not found",
            ClaudeUsageState.Querying => "Claude: Refreshing account quota",
            ClaudeUsageState.Fresh => "Claude: Account quota refreshed",
            ClaudeUsageState.Cached => "Claude: Showing cached account quota",
            ClaudeUsageState.QueryFailed => "Claude: Account quota query failed",
            _ => "Claude: Unavailable"
        };
        ClaudeSetupHelpText = state == ClaudeUsageState.CliNotFound
            ? "Install Claude Code using Anthropic's official instructions, then choose Recheck Claude. DevOverlay never reads transcripts, prompts, auth files, API keys, or cookies."
            : string.IsNullOrWhiteSpace(detail)
                ? "Account quota is read from Claude Code's local /usage command. HUD percentages show remaining quota."
                : detail;
    }

    internal void UpdateSensorServiceStatus(SensorServiceStatus status)
    {
        _lastSensorServiceStatus = status;
        SensorServiceStatusText = status.State switch
        {
            SensorServiceRunState.NotInstalled => "Sensor Service: Not installed",
            SensorServiceRunState.Stopped => "Sensor Service: Stopped",
            SensorServiceRunState.StartPending => "Sensor Service: Starting",
            SensorServiceRunState.Running => "Sensor Service: Running",
            SensorServiceRunState.StopPending => "Sensor Service: Stopping",
            SensorServiceRunState.Broken => $"Sensor Service: Broken — {status.Detail}",
            _ => $"Sensor Service: Unknown — {status.Detail}"
        };
        CanInstallSensorService = !_sensorServiceActionBusy && status.State == SensorServiceRunState.NotInstalled;
        CanRepairSensorService = !_sensorServiceActionBusy && (status.State is
            SensorServiceRunState.Stopped or SensorServiceRunState.Running or SensorServiceRunState.Broken);
        CanUninstallSensorService = !_sensorServiceActionBusy && (status.State is
            SensorServiceRunState.Stopped or SensorServiceRunState.Running or SensorServiceRunState.Broken);
    }

    public void UpdateSensorServiceConnection(SensorHostState state, string? detail)
    {
        SensorServiceConnectionText = state switch
        {
            SensorHostState.Ready => "Service connection: CPU Package sensors ready",
            SensorHostState.CpuSensorsUnavailable => "Service connection: Package sensors unavailable",
            SensorHostState.PawnIoUnavailable => "Service connection: PawnIO unavailable",
            SensorHostState.PawnIoAccessDenied => "Service connection: PawnIO access denied",
            SensorHostState.CpuHardwareUnavailable => "Service connection: CPU not detected",
            SensorHostState.ProtocolMismatch => "Service connection: Incompatible protocol",
            SensorHostState.Connecting => "Service connection: Connecting",
            SensorHostState.Disconnected => "Service connection: Disconnected",
            SensorHostState.NotRunning => "Service connection: Waiting for service",
            _ => "Service connection: Unavailable"
        };
        if (state is SensorHostState.ProtocolMismatch or SensorHostState.Failed)
            SensorServiceActionMessage = detail ?? string.Empty;
    }

    internal void UpdateSensorServiceActionProgress(SensorServiceAction action)
    {
        _sensorServiceActionBusy = true;
        UpdateSensorServiceStatus(_lastSensorServiceStatus);
        SensorServiceActionMessage = $"{action} in progress; approve Windows administrator prompt.";
    }

    internal void UpdateSensorServiceActionResult(SensorServiceActionResult result)
    {
        _sensorServiceActionBusy = false;
        UpdateSensorServiceStatus(_lastSensorServiceStatus);
        SensorServiceActionMessage = result.Succeeded ? "Sensor Service action completed." :
            result.Detail ?? (result.ElevationCancelled ? "Administrator approval cancelled." : "Sensor Service action failed.");
    }

    public void UpdateSensorHostStatus(SensorHostState state)
    {
        SensorHostStatusText = $"Sensor Host: {state switch
        {
            SensorHostState.NotRunning => "Not running",
            SensorHostState.Starting => "Waiting for administrator approval...",
            SensorHostState.ElevationCancelled => "Elevation cancelled",
            SensorHostState.Connecting => "Connecting...",
            SensorHostState.Connected => "Connected",
            SensorHostState.Ready => "Connected — package sensors ready",
            SensorHostState.PawnIoUnavailable => "Connected — PawnIO unavailable",
            SensorHostState.PawnIoAccessDenied => "Connected — PawnIO access denied",
            SensorHostState.CpuHardwareUnavailable => "Connected — CPU not detected",
            SensorHostState.CpuSensorsUnavailable => "Connected — package sensors unavailable",
            SensorHostState.ProtocolMismatch => "Incompatible helper version",
            SensorHostState.Disconnected => "Disconnected",
            _ => "Failed"
        }}";
        CanStartSensorHost = state is SensorHostState.NotRunning or SensorHostState.ElevationCancelled or
            SensorHostState.Disconnected or SensorHostState.Failed or SensorHostState.ProtocolMismatch or
            SensorHostState.PawnIoUnavailable or SensorHostState.PawnIoAccessDenied or
            SensorHostState.CpuHardwareUnavailable or SensorHostState.CpuSensorsUnavailable;
        SensorHostActionText = state is SensorHostState.PawnIoUnavailable or SensorHostState.PawnIoAccessDenied or
            SensorHostState.CpuHardwareUnavailable or SensorHostState.CpuSensorsUnavailable
            ? "Restart CPU Sensor Host" : "Start CPU Sensor Host";
        CanRetryCpuSensors = state is SensorHostState.Ready or SensorHostState.CpuSensorsUnavailable ||
            (_pawnIoDriverStatusText == "Driver access: Available" && state == SensorHostState.NotRunning);
    }

    public void UpdateCpuSensorAvailability(bool temperatureAvailable, bool powerAvailable)
    {
        CpuSensorStatusText = $"CPU Package sensors: temperature {(temperatureAvailable ? "available" : "unavailable")}, " +
                              $"power {(powerAvailable ? "available" : "unavailable")}";
    }

    public void UpdatePawnIoStatus(PawnIoStatus status)
    {
        PawnIoStatusText = status.State switch
        {
            PawnIoState.NotInstalled => "PawnIO: Not installed — CPU package sensors unavailable",
            PawnIoState.Outdated => $"PawnIO: Outdated ({status.Version})",
            PawnIoState.Installed => $"PawnIO: Installed ({status.Version})",
            PawnIoState.DriverAccessible => $"PawnIO: Installed ({status.Version})",
            PawnIoState.InstalledButDriverUnavailable => $"PawnIO: Installed ({status.Version})",
            PawnIoState.Installing => "PawnIO: Installing... approve the Windows administrator prompt",
            PawnIoState.InstalledButSensorUnavailable => $"PawnIO: Installed ({status.Version}), but package sensors unavailable",
            PawnIoState.InstallFailed => $"PawnIO: Installation failed — {status.Detail}",
            _ => "PawnIO: Unknown"
        };
        PawnIoActionText = status.State == PawnIoState.Outdated ? "Update PawnIO" : "Install PawnIO";
        CanInstallPawnIo = status.State is PawnIoState.NotInstalled or PawnIoState.Outdated or PawnIoState.InstallFailed;
        PawnIoDriverStatusText = status.State switch
        {
            PawnIoState.DriverAccessible or PawnIoState.InstalledButSensorUnavailable => "Driver access: Available",
            PawnIoState.InstalledButDriverUnavailable when status.Detail == "Windows error 5" =>
                "Driver access: Denied (administrator-only device)",
            PawnIoState.InstalledButDriverUnavailable => $"Driver access: Unavailable ({status.Detail})",
            PawnIoState.Installed => "Driver access: Not checked",
            _ => "Driver access: Unavailable"
        };
        CanRetryCpuSensors = status.State is PawnIoState.DriverAccessible or PawnIoState.InstalledButSensorUnavailable;
    }

    public void ResetPosition()
    {
        _position = OverlayPosition.CreateDefault();
        OnPropertyChanged(nameof(PositionModeText));
        SettingsChanged?.Invoke(BuildSettings());
    }

    public void MoveGroup(MetricCategory category, int direction)
    {
        var current = _groupOrder.ToList().FindIndex(choice => choice.Category == category);
        var target = current + direction;
        if (current < 0 || target < 0 || target >= _groupOrder.Count) return;
        _groupOrder.Move(current, target);
        UpdateOrderControls();
        SettingsChanged?.Invoke(BuildSettings());
    }

    private void UpdateOrderControls()
    {
        for (var index = 0; index < _groupOrder.Count; index++)
        {
            _groupOrder[index].CanMoveUp = index > 0;
            _groupOrder[index].CanMoveDown = index < _groupOrder.Count - 1;
        }
    }

    public void ResetAppearance()
    {
        _appearance = OverlayAppearance.CreateDefault();
        _backgroundColor = _appearance.BackgroundColor;
        _backgroundOpacityPercent = _appearance.BackgroundOpacity * 100;
        _valueTextColor = _appearance.ValueTextColor;
        _labelTextColor = _appearance.LabelTextColor;
        _spacing = _appearance.Spacing;
        OnPropertyChanged(nameof(SpacingPercent));
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(BackgroundOpacityPercent));
        OnPropertyChanged(nameof(ValueTextColor));
        OnPropertyChanged(nameof(LabelTextColor));
        SettingsChanged?.Invoke(BuildSettings());
    }

    public void UpdatePosition(OverlayPosition position)
    {
        _position = position;
        _baseSettings = _baseSettings with { Position = position };
        OnPropertyChanged(nameof(PositionModeText));
    }

    private void SetAndNotify<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (!_isUpdatingDeviceChoices)
        {
            if (propertyName == nameof(SpacingPercent))
                RuntimeDiagnostics.Write($"[Spacing] ViewModel={SpacingPercent} Settings={_spacing}");
            SettingsChanged?.Invoke(BuildSettings());
        }
    }

    private void SetStatusProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void SetColor(ref string field, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!OverlayColor.TryNormalize(value, out var normalized))
        {
            OnPropertyChanged(propertyName);
            return;
        }

        SetAndNotify(ref field, normalized, propertyName);
    }

    private void SetOpacity(double value)
    {
        var clamped = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : OverlayAppearance.CreateDefault().BackgroundOpacity * 100;
        SetAndNotify(ref _backgroundOpacityPercent, clamped, nameof(BackgroundOpacityPercent));
    }

    private void SetSpacing(double value) => SetAndNotify(ref _spacing,
        double.IsFinite(value) ? Math.Round(Math.Clamp(value, OverlayAppearance.MinSpacing, OverlayAppearance.MaxSpacing), 4)
            : OverlayAppearance.DefaultSpacing, nameof(SpacingPercent));

    private void SetRefreshIntervalFromSlider(int value)
    {
        var offset = value - OverlaySettings.MinimumRefreshIntervalMs;
        var stepped = OverlaySettings.MinimumRefreshIntervalMs + (int)Math.Round(offset / 50d, MidpointRounding.AwayFromZero) * 50;
        SetRefreshInterval(stepped);
    }

    private void SetRefreshIntervalText(string? value)
    {
        value ??= string.Empty;
        if (string.Equals(_refreshIntervalText, value, StringComparison.Ordinal)) return;
        _refreshIntervalText = value;
        OnPropertyChanged(nameof(RefreshIntervalText));
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= OverlaySettings.MinimumRefreshIntervalMs && parsed <= OverlaySettings.MaximumRefreshIntervalMs)
            SetRefreshInterval(parsed);
    }

    /// <summary>Commits a text edit, clamping an integer or restoring the last valid value for temporary invalid text.</summary>
    public void CommitRefreshIntervalText()
    {
        if (int.TryParse(_refreshIntervalText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            SetRefreshInterval(OverlaySettings.NormalizeRefreshIntervalMs(parsed));
            return;
        }

        _refreshIntervalText = _refreshIntervalMs.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(RefreshIntervalText));
    }

    private void SetRefreshInterval(int value)
    {
        var normalized = OverlaySettings.NormalizeRefreshIntervalMs(value);
        var text = normalized.ToString(CultureInfo.InvariantCulture);
        var changed = _refreshIntervalMs != normalized;
        _refreshIntervalMs = normalized;
        if (!string.Equals(_refreshIntervalText, text, StringComparison.Ordinal))
        {
            _refreshIntervalText = text;
            OnPropertyChanged(nameof(RefreshIntervalText));
        }
        if (!changed) return;
        OnPropertyChanged(nameof(RefreshIntervalMs));
        OnPropertyChanged(nameof(RefreshIntervalSliderValue));
        if (!_isUpdatingDeviceChoices) SettingsChanged?.Invoke(BuildSettings());
    }

    private void SetDeviceId(ref string field, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        // A list refresh can temporarily clear SelectedItem; only a real choice changes settings.
        if (string.IsNullOrWhiteSpace(value) || _isUpdatingDeviceChoices)
        {
            return;
        }

        var choices = propertyName switch
        {
            nameof(GpuDeviceId) => GpuDevices,
            nameof(NetworkDeviceId) => NetworkDevices,
            nameof(StorageDeviceId) => StorageDevices,
            nameof(FpsTargetId) => FpsTargets,
            _ => throw new InvalidOperationException("Unknown device selection property.")
        };
        if (choices.All(choice => !string.Equals(choice.Id, value, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new DeviceChoice(value, propertyName == nameof(FpsTargetId)
                ? Path.GetFileName(value) : $"Unavailable ({value})"));
        }

        SetAndNotify(ref field, value, propertyName);
        var selectedPropertyName = propertyName switch
        {
            nameof(GpuDeviceId) => nameof(SelectedGpuDevice),
            nameof(NetworkDeviceId) => nameof(SelectedNetworkDevice),
            nameof(StorageDeviceId) => nameof(SelectedStorageDevice),
            nameof(FpsTargetId) => nameof(SelectedFpsTarget),
            _ => null
        };
        if (selectedPropertyName is not null)
        {
            OnPropertyChanged(selectedPropertyName);
        }
    }

    private static string FormatHotkey(OverlayHotkey hotkey)
    {
        var parts = new List<string>();
        if (hotkey.Modifiers.HasFlag(OverlayHotkeyModifiers.Control)) parts.Add("Ctrl");
        if (hotkey.Modifiers.HasFlag(OverlayHotkeyModifiers.Shift)) parts.Add("Shift");
        if (hotkey.Modifiers.HasFlag(OverlayHotkeyModifiers.Alt)) parts.Add("Alt");
        if (hotkey.Modifiers.HasFlag(OverlayHotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)hotkey.VirtualKey).ToString());
        return string.Join(" + ", parts);
    }

    private OverlaySettings BuildSettings()
    {
        var featureSettings = OverlaySettings.CreateForCurrentFeatures(
        CpuEnabled,
        GpuEnabled,
        NetworkEnabled,
        StorageEnabled,
        ToSelection(GpuDeviceId),
        ToSelection(NetworkDeviceId),
        ToSelection(StorageDeviceId),
        _isOverlayVisible,
        CpuUsageEnabled,
        GpuUsageEnabled,
        GpuTemperatureEnabled,
        GpuPowerEnabled,
        GpuVramEnabled,
        NetworkDownloadEnabled,
        NetworkUploadEnabled,
        StorageReadEnabled,
        StorageWriteEnabled,
        CpuTemperatureEnabled,
        CpuPowerEnabled,
        NetworkTodayTotalEnabled,
        FpsEnabled,
        FramesPerSecondEnabled,
        OnePercentLowEnabled,
        FrameTimeEnabled,
        ToSelection(FpsTargetId),
        LatencyEnabled,
        RenderLatencyEnabled,
        AiUsageEnabled,
        CodexAccountLimitsEnabled,
        ClaudeUsageEnabled);
        _baseSettings = _baseSettings with
        {
            EnabledGroups = featureSettings.EnabledGroups,
            EnabledMetrics = featureSettings.EnabledMetrics,
            GpuDeviceSelection = featureSettings.GpuDeviceSelection,
            NetworkDeviceSelection = featureSettings.NetworkDeviceSelection,
            StorageDeviceSelection = featureSettings.StorageDeviceSelection,
            FpsTargetSelection = featureSettings.FpsTargetSelection,
            RefreshIntervalMs = _refreshIntervalMs,
            Position = _position,
            GroupOrder = _groupOrder.Select(choice => choice.Category).ToArray(),
            Appearance = new OverlayAppearance(
            _backgroundColor,
            _backgroundOpacityPercent / 100,
            _valueTextColor,
            _labelTextColor,
            _spacing).Normalize(),
        };
        return _baseSettings;
    }

    private void UpdateDeviceChoices(
        ObservableCollection<DeviceChoice> destination,
        IEnumerable<DeviceDescriptor> devices,
        string selectedDeviceId,
        string selectedChoicePropertyName,
        bool includeSystemDrive = false,
        bool isFpsTarget = false)
    {
        var desired = CreateDeviceChoices(devices, selectedDeviceId, includeSystemDrive, isFpsTarget);
        _isUpdatingDeviceChoices = true;
        try
        {
            foreach (var existing in destination.ToArray())
            {
                if (desired.All(choice => !string.Equals(choice.Id, existing.Id, StringComparison.OrdinalIgnoreCase)))
                    destination.Remove(existing);
            }
            foreach (var choice in desired)
            {
                var existing = destination.FirstOrDefault(item =>
                    string.Equals(item.Id, choice.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is null) destination.Add(choice);
                else existing.DisplayName = choice.DisplayName;
            }
            OnPropertyChanged(selectedChoicePropertyName);
        }
        finally
        {
            _isUpdatingDeviceChoices = false;
        }
    }

    private static DeviceChoice ResolveChoice(IEnumerable<DeviceChoice> choices, string selectedId) =>
        choices.First(choice => string.Equals(choice.Id, selectedId, StringComparison.OrdinalIgnoreCase));

    private static ObservableCollection<DeviceChoice> CreateDeviceChoices(
        IEnumerable<DeviceDescriptor> devices, string selectedDeviceId, bool includeSystemDrive = false,
        bool isFpsTarget = false)
    {
        var choices = new ObservableCollection<DeviceChoice>();
        if (includeSystemDrive)
        {
            var volume = SystemDriveResolver.GetVolumeName();
            choices.Add(new DeviceChoice(DeviceChoice.SystemDriveId,
                volume is null ? "System (unavailable)" : $"System ({volume})"));
        }
        choices.Add(new DeviceChoice(DeviceChoice.AutoId, "Auto"));
        foreach (var device in devices
            .Where(device => device.Id != DeviceChoice.AutoId)
            .DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            choices.Add(new DeviceChoice(device.Id, device.DisplayName));
        }

        if (selectedDeviceId != DeviceChoice.AutoId &&
            choices.All(choice => !string.Equals(choice.Id, selectedDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new DeviceChoice(selectedDeviceId, isFpsTarget
                ? Path.GetFileName(selectedDeviceId) : $"Unavailable ({selectedDeviceId})"));
        }

        return choices;
    }

    private static string ToDeviceId(DeviceSelection selection) =>
        selection is SpecificDeviceSelection specific ? specific.DeviceId :
            selection is SystemDriveDeviceSelection ? DeviceChoice.SystemDriveId : DeviceChoice.AutoId;

    private static DeviceSelection ToSelection(string deviceId) =>
        deviceId == DeviceChoice.AutoId ? DeviceSelection.Auto :
            deviceId == DeviceChoice.SystemDriveId ? DeviceSelection.SystemDrive : DeviceSelection.Specific(deviceId);
}

public sealed class DeviceChoice(string id, string displayName) : ObservableObject
{
    private string _displayName = displayName;
    public string Id { get; } = id;
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            OnPropertyChanged();
        }
    }
    // Nonempty UI-only value: WPF treats an empty SelectedValue as no visible selection.
    public const string AutoId = "dev-overlay:auto";
    public const string SystemDriveId = "dev-overlay:system-drive";
}

public sealed class GroupOrderChoice(MetricCategory category) : ObservableObject
{
    private bool _canMoveUp;
    private bool _canMoveDown;
    public MetricCategory Category { get; } = category;
    public string Label => Category switch
    {
        MetricCategory.Cpu => "CPU",
        MetricCategory.Gpu => "GPU",
        MetricCategory.Storage => "DISK",
        MetricCategory.Network => "NET",
        MetricCategory.Frame => "FPS",
        MetricCategory.Latency => "LAT",
        MetricCategory.AiUsage => "AI",
        MetricCategory.PeripheralBattery => "Peripheral batteries",
        _ => Category.ToString().ToUpperInvariant()
    };
    public bool CanMoveUp { get => _canMoveUp; set { if (_canMoveUp == value) return; _canMoveUp = value; OnPropertyChanged(); } }
    public bool CanMoveDown { get => _canMoveDown; set { if (_canMoveDown == value) return; _canMoveDown = value; OnPropertyChanged(); } }
}
