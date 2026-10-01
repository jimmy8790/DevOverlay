using System.Windows;
using System.Diagnostics;
using System.IO;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;
using DevOverlay.UI;
using DevOverlay.SensorHostProtocol;

namespace DevOverlay;

public partial class App : System.Windows.Application
{
    private readonly SemaphoreSlim _providerReconfigurationGate = new(1, 1);
    private readonly SemaphoreSlim _sensorServiceActionGate = new(1, 1);
    private OverlaySettingsStore? _settingsStore;
    private OverlaySettings _settings = OverlaySettings.CreateDefault();
    private MetricUpdateService? _metricUpdateService;
    private MetricUpdateService? _fpsUpdateService;
    private FpsMetricProvider? _fpsProvider;
    private CodexRateLimitMetricProvider? _codexRateLimitProvider;
    private ClaudeUsageMetricProvider? _claudeUsageProvider;
    private OverlayViewModel? _overlayViewModel;
    private OverlayWindow? _overlayWindow;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsViewModel;
    private System.Windows.Threading.DispatcherTimer? _settingsStatusTimer;
    private TrayIconController? _trayIcon;
    private readonly PawnIoPrerequisiteService _pawnIoPrerequisite = new();
    private readonly SensorServiceClient _sensorServiceClient = new();
    private readonly SensorServiceStatusReader _sensorServiceStatusReader = new();
    private readonly SensorServiceManagement _sensorServiceManagement = new();
    private readonly GlobalHotkeyService _globalHotkeyService = new();
    private string _hotkeyStatusText = "Global hotkey: Checking...";
    private OverlaySettings? _providerSettings;
#if DEBUG
    private bool _runtimeProbe;
#endif

    protected override void OnStartup(StartupEventArgs e)
    {
        if (ClaudeStatusLineBridge.IsInvocation(e.Args))
        {
            ClaudeStatusLineBridge.Run(Console.In);
            return;
        }
        base.OnStartup(e);

        _settingsStore = new OverlaySettingsStore();
        _sensorServiceClient.StateChanged += (state, detail) => Dispatcher.BeginInvoke(() =>
        {
            _settingsViewModel?.UpdateSensorServiceConnection(state, detail);
            if (_settingsViewModel is not null)
                _settingsViewModel.UpdateSensorServiceStatus(_sensorServiceStatusReader.Read());
            if (state is SensorHostState.Disconnected or SensorHostState.Failed or SensorHostState.ProtocolMismatch or
                SensorHostState.NotRunning)
            {
                _overlayViewModel?.ApplyMetrics([
                    MetricSnapshot.Unavailable(MetricId.CpuTemperature, MetricCategory.Cpu, "CPU 패키지 온도", "°C"),
                    MetricSnapshot.Unavailable(MetricId.CpuPower, MetricCategory.Cpu, "CPU 패키지 전력", "W")
                ]);
                _settingsViewModel?.UpdateCpuSensorAvailability(false, false);
            }
        });
        _settings = _settingsStore.Load();
        RuntimeDiagnostics.Write($"[Startup] ExePath={Environment.ProcessPath} PID={Environment.ProcessId} Version={typeof(App).Assembly.GetName().Version} BuildId={typeof(App).Module.ModuleVersionId} SettingsPath={_settingsStore.FilePath} FpsVisible={_settings.EnabledGroups.Contains(MetricCategory.Frame)} LatencyVisible={_settings.EnabledGroups.Contains(MetricCategory.Latency)}");
#if DEBUG
        _runtimeProbe = e.Args.Contains("--diagnose-runtime", StringComparer.OrdinalIgnoreCase) ||
            e.Args.Contains("--diagnose-startup", StringComparer.OrdinalIgnoreCase);
#endif

        _overlayViewModel = new OverlayViewModel(_settings, Dispatcher);
        _overlayWindow = new OverlayWindow(_overlayViewModel, new OverlayPositioningService());
        _overlayWindow.CustomPositionChanged += OnCustomPositionChanged;
        MainWindow = _overlayWindow;
        _globalHotkeyService.Pressed += ToggleOverlayVisibility;
        _hotkeyStatusText = InitializeOverlay(_overlayWindow, _globalHotkeyService, _settings);
        RuntimeDiagnostics.Write($"[Startup] Hotkey={_hotkeyStatusText} OverlayVisible={_overlayWindow.IsVisible}");
        StartMetricUpdateService(_settings);
        _fpsProvider = new FpsMetricProvider(_settings);
        _fpsUpdateService = new MetricUpdateService([_fpsProvider], TimeSpan.FromMilliseconds(_settings.RefreshIntervalMs));
        _fpsUpdateService.MetricsUpdated += metrics => Dispatcher.BeginInvoke(() =>
        {
            _overlayViewModel?.ApplyMetrics(metrics);
            _settingsViewModel?.UpdateFpsTelemetryAvailability(metrics.Any(metric =>
                metric.Id == MetricId.FramesPerSecond && metric.IsAvailable));
        });
        _fpsUpdateService.ProviderFaulted += (provider, exception) =>
            Debug.WriteLine($"Metric provider '{provider.Name}' failed: {exception}");
        _fpsUpdateService.Start();

        _trayIcon = new TrayIconController(OpenSettings, ToggleOverlayVisibility, ExitApplication);
#if DEBUG
        if (e.Args.Contains("--diagnose-startup", StringComparer.OrdinalIgnoreCase)) _ = ProbeStartupAsync();
        else if (_runtimeProbe) _ = ProbeRuntimeAsync();
#endif
    }

    internal static IMetricProvider[] CreateRuntimeProviders(OverlaySettings settings, ICpuPackageSensorSource? cpuSensorSource = null,
        CodexRateLimitMetricProvider? codexRateLimitProvider = null, ClaudeUsageMetricProvider? claudeUsageProvider = null) =>
    [
        new CpuUtilizationMetricProvider(),
        new CpuPackageSensorMetricProvider(cpuSensorSource),
        new NvidiaGpuMetricProvider(settings.GpuDeviceSelection),
        new PhysicalDiskThroughputMetricProvider(settings.StorageDeviceSelection),
        new NetworkThroughputMetricProvider(settings.NetworkDeviceSelection),
        new NetworkTodayMetricProvider(settings.NetworkDeviceSelection),
        codexRateLimitProvider ?? new CodexRateLimitMetricProvider(settings),
        claudeUsageProvider ?? new ClaudeUsageMetricProvider(settings)
    ];

    internal static string InitializeOverlay(OverlayWindow window, GlobalHotkeyService hotkeys, OverlaySettings settings)
    {
        var registered = hotkeys.Attach(window, settings.Hotkey, out var error);
        if (settings.IsVisible) window.ShowWithoutActivation();
        return registered ? "Global hotkey: active" : error ?? "Global hotkey: unavailable.";
    }

    private void StartMetricUpdateService(OverlaySettings settings)
    {
        var codexProvider = new CodexRateLimitMetricProvider(settings);
        var claudeProvider = new ClaudeUsageMetricProvider(settings);
        var service = new MetricUpdateService(CreateRuntimeProviders(settings, _sensorServiceClient, codexProvider, claudeProvider), TimeSpan.FromMilliseconds(settings.RefreshIntervalMs));
        service.MetricsUpdated += metrics => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_metricUpdateService, service)) return;
            _overlayViewModel?.ApplyMetrics(metrics);
            if (metrics.Any(metric => metric.Id is MetricId.CpuTemperature or MetricId.CpuPower))
            {
                var cpuProvider = service.Providers.OfType<CpuPackageSensorMetricProvider>().FirstOrDefault();
                if (cpuProvider is not null)
                    _settingsViewModel?.UpdateCpuSensorAvailability(
                        cpuProvider.HasTemperatureReading, cpuProvider.HasPowerReading);
            }
        });
        service.ProviderFaulted += (provider, exception) =>
            Debug.WriteLine($"Metric provider '{provider.Name}' failed: {exception}");
        codexProvider.MetricsUpdated += metrics => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_codexRateLimitProvider, codexProvider)) return;
            _overlayViewModel?.ApplyMetrics(metrics);
        });
        codexProvider.StateChanged += (state, detail) => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_codexRateLimitProvider, codexProvider))
                _settingsViewModel?.UpdateCodexStatus(state, detail);
        });
        claudeProvider.StateChanged += (state, detail) => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_claudeUsageProvider, claudeProvider)) _settingsViewModel?.UpdateClaudeStatus(state, detail);
        });
        _metricUpdateService = service;
        _codexRateLimitProvider = codexProvider;
        _claudeUsageProvider = claudeProvider;
        _providerSettings = settings;
        service.Start();
    }

    private void OpenSettings()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OpenSettings);
            return;
        }

        if (_settingsWindow is not null)
        {
            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }

            _settingsWindow.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(_settings);
        viewModel.SettingsChanged += async settings =>
        {
            try { await ApplySettingsAsync(settings); }
            catch (Exception exception) { RuntimeDiagnostics.Write($"[Settings] Apply failed: {exception}"); }
        };
        viewModel.MoveOverlayRequested += EnableOverlayPositionEditing;
        viewModel.PawnIoInstallRequested += () => _ = InstallPawnIoAsync(viewModel);
        viewModel.SensorServiceActionRequested += action => _ = RunSensorServiceActionAsync(viewModel, action);
        viewModel.HotkeyChangeRequested += hotkey => ChangeGlobalHotkey(viewModel, hotkey);
        viewModel.HotkeyCaptureChanged += _globalHotkeyService.SetCaptureActive;
        viewModel.PresentMonInstallRequested += InstallPresentMon;
        viewModel.CodexRecheckRequested += () => _ = RecheckCodexAsync(viewModel);
        viewModel.ClaudeRecheckRequested += () => _ = RecheckClaudeAsync(viewModel);
        viewModel.UpdatePawnIoStatus(_pawnIoPrerequisite.Diagnose());
        viewModel.UpdateSensorServiceStatus(_sensorServiceStatusReader.Read());
        viewModel.UpdateSensorServiceConnection(_sensorServiceClient.State, _sensorServiceClient.Detail);
        viewModel.UpdatePresentMonStatus(PresentMonServiceInfo.Read());
        var cpuProvider = _metricUpdateService?.Providers.OfType<CpuPackageSensorMetricProvider>().FirstOrDefault();
        viewModel.UpdateCpuSensorAvailability(cpuProvider?.HasTemperatureReading == true, cpuProvider?.HasPowerReading == true);
        viewModel.UpdateHotkey(_settings.Hotkey, _hotkeyStatusText);
        viewModel.UpdateCodexStatus(_codexRateLimitProvider?.State ?? CodexRateLimitState.Disabled, _codexRateLimitProvider?.Detail);
        viewModel.UpdateClaudeStatus(_claudeUsageProvider?.State ?? ClaudeUsageState.Disabled, _claudeUsageProvider?.Detail);
        var window = new SettingsWindow(viewModel);
        window.Closed += (_, _) =>
        {
            _globalHotkeyService.SetCaptureActive(false);
            _settingsStatusTimer?.Stop();
            _settingsStatusTimer = null;
            _settingsWindow = null;
            _settingsViewModel = null;
        };

        _settingsViewModel = viewModel;
        _settingsWindow = window;
        _settingsStatusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _settingsStatusTimer.Tick += (_, _) => viewModel.UpdatePresentMonStatus(PresentMonServiceInfo.Read());
        _settingsStatusTimer.Start();
        window.Show();
        _ = PopulateDeviceChoicesAsync(viewModel);
    }

    private async Task RecheckCodexAsync(SettingsViewModel viewModel)
    {
        var provider = _codexRateLimitProvider;
        if (provider is null) return;
        try
        {
            await provider.RecheckAsync();
            viewModel.UpdateCodexStatus(provider.State, provider.Detail);
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Write($"[Codex] Explicit recheck failed Type={exception.GetType().Name}");
            viewModel.UpdateCodexStatus(CodexRateLimitState.Error);
        }
    }

    private async Task RecheckClaudeAsync(SettingsViewModel viewModel)
    {
        var provider = _claudeUsageProvider;
        if (provider is null) return;
        try
        {
            await provider.RecheckAsync();
            viewModel.UpdateClaudeStatus(provider.State, provider.Detail);
        }
        catch (Exception exception)
        {
            RuntimeDiagnostics.Write($"[Claude] Explicit recheck failed Type={exception.GetType().Name}");
            viewModel.UpdateClaudeStatus(ClaudeUsageState.Error, "Claude quota refresh failed.");
        }
    }

    private async Task PopulateDeviceChoicesAsync(SettingsViewModel viewModel)
    {
        var providers = _metricUpdateService?.Providers ?? [];
        var gpuProvider = providers.OfType<NvidiaGpuMetricProvider>().FirstOrDefault();
        var networkProvider = providers.OfType<NetworkThroughputMetricProvider>().FirstOrDefault();
        var storageProvider = providers.OfType<PhysicalDiskThroughputMetricProvider>().FirstOrDefault();

        var gpuDevicesTask = Task.Run(() => GetAvailableDevices(gpuProvider));
        var networkDevicesTask = Task.Run(() => GetAvailableDevices(networkProvider));
        var storageDevicesTask = Task.Run(() => GetAvailableDevices(storageProvider));
        var fpsTargetsTask = Task.Run(GetAvailableFpsTargets);
        await Task.WhenAll(gpuDevicesTask, networkDevicesTask, storageDevicesTask, fpsTargetsTask);

        if (_settingsViewModel != viewModel)
        {
            return;
        }

        viewModel.UpdateGpuDevices(await gpuDevicesTask);
        viewModel.UpdateNetworkDevices(await networkDevicesTask);
        viewModel.UpdateStorageDevices(await storageDevicesTask);
        viewModel.UpdateFpsTargets(await fpsTargetsTask);
    }

    private static IReadOnlyCollection<DeviceDescriptor> GetAvailableDevices(ISelectableDeviceProvider? provider)
    {
        try
        {
            return provider?.GetAvailableDevices() ?? [];
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static IReadOnlyCollection<DeviceDescriptor> GetAvailableFpsTargets()
    {
        try { return new WindowsFpsProcessSource().ListRunning(); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return []; }
    }

    private async Task ApplySettingsAsync(OverlaySettings settings)
    {
        // Visibility is controlled by the tray, not by the settings form.
        settings = settings with { IsVisible = _settings.IsVisible };
        var previousSettings = _settings;
        _settings = settings;
        _overlayViewModel?.ApplySettings(settings);
        _fpsProvider?.Configure(settings);
        if (_codexRateLimitProvider is not null)
            await _codexRateLimitProvider.ConfigureAsync(settings);
        _claudeUsageProvider?.Configure(settings);
        if (previousSettings.RefreshIntervalMs != settings.RefreshIntervalMs)
        {
            var interval = TimeSpan.FromMilliseconds(settings.RefreshIntervalMs);
            _metricUpdateService?.SetRefreshInterval(interval);
            _fpsUpdateService?.SetRefreshInterval(interval);
            RuntimeDiagnostics.Write($"[RefreshInterval] Old={previousSettings.RefreshIntervalMs} New={settings.RefreshIntervalMs} SchedulerUpdated=True");
        }
        var presentMonTargetChanged = previousSettings.FpsTargetSelection != settings.FpsTargetSelection;
        if (presentMonTargetChanged ||
            previousSettings.EnabledGroups.Contains(MetricCategory.Frame) != settings.EnabledGroups.Contains(MetricCategory.Frame) ||
            new[] { MetricId.FramesPerSecond, MetricId.OnePercentLow, MetricId.FrameTime }.Any(id =>
                previousSettings.EnabledMetrics.Contains(id) != settings.EnabledMetrics.Contains(id)))
            _overlayViewModel?.MarkCategoryUnavailable(MetricCategory.Frame);
        if (presentMonTargetChanged ||
            previousSettings.EnabledGroups.Contains(MetricCategory.Latency) != settings.EnabledGroups.Contains(MetricCategory.Latency) ||
            previousSettings.EnabledMetrics.Contains(MetricId.Latency) != settings.EnabledMetrics.Contains(MetricId.Latency))
            _overlayViewModel?.MarkCategoryUnavailable(MetricCategory.Latency);
        _overlayWindow?.ApplySettings(settings.Position, settings.Appearance);
        RuntimeDiagnostics.Write($"[Spacing] ApplySettings=true Percent={settings.Appearance.Spacing * 100:0}");
        SaveSettings();

        if (HasProviderSelectionChanged(previousSettings, settings))
        {
            if (previousSettings.GpuDeviceSelection != settings.GpuDeviceSelection)
                _overlayViewModel?.MarkCategoryUnavailable(MetricCategory.Gpu);
            if (previousSettings.NetworkDeviceSelection != settings.NetworkDeviceSelection)
                _overlayViewModel?.MarkCategoryUnavailable(MetricCategory.Network);
            if (previousSettings.StorageDeviceSelection != settings.StorageDeviceSelection)
                _overlayViewModel?.MarkCategoryUnavailable(MetricCategory.Storage);
            await ReconfigureProvidersAsync();
        }
    }

    private async Task ReconfigureProvidersAsync()
    {
        await _providerReconfigurationGate.WaitAsync();
        try
        {
            if (_providerSettings is not null && !HasProviderSelectionChanged(_providerSettings, _settings)) return;
            var previousService = _metricUpdateService;
            _metricUpdateService = null;
            _codexRateLimitProvider = null;
            if (previousService is not null)
            {
                await previousService.DisposeAsync();
            }

            // Several rapid selections can queue behind this gate; use the newest committed settings.
            StartMetricUpdateService(_settings);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Metric providers could not be reconfigured: {exception}");
        }
        finally
        {
            _providerReconfigurationGate.Release();
        }
    }

    internal static bool HasProviderSelectionChanged(OverlaySettings previous, OverlaySettings current) =>
        previous.GpuDeviceSelection != current.GpuDeviceSelection ||
        previous.NetworkDeviceSelection != current.NetworkDeviceSelection ||
        previous.StorageDeviceSelection != current.StorageDeviceSelection;

    private async Task InstallPawnIoAsync(SettingsViewModel viewModel)
    {
        if (System.Windows.MessageBox.Show(_settingsWindow,
                "PawnIO is a signed kernel driver required for CPU package sensors. Windows will request administrator approval. Continue?",
                "Install PawnIO", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        viewModel.UpdatePawnIoStatus(new PawnIoStatus(PawnIoState.Installing));
        var status = await _pawnIoPrerequisite.InstallAsync();
        if (status.State == PawnIoState.Installed)
        {
            status = _pawnIoPrerequisite.Diagnose();
            // The persistent service retries its own backend; the non-elevated app does not open PawnIO.
        }
        if (_settingsViewModel == viewModel) viewModel.UpdatePawnIoStatus(status);
    }

    private async Task RunSensorServiceActionAsync(SettingsViewModel viewModel, SensorServiceAction action)
    {
        if (!_sensorServiceActionGate.Wait(0)) return;
        try
        {
            string verb = action switch
            {
                SensorServiceAction.Install => "install",
                SensorServiceAction.Repair => "repair",
                SensorServiceAction.Uninstall => "uninstall",
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            string message = action == SensorServiceAction.Uninstall
                ? "Uninstall only the DevOverlay CPU Sensor Service? PawnIO will remain installed. CPU Package telemetry will become unavailable."
                : $"{char.ToUpperInvariant(verb[0])}{verb[1..]} the DevOverlay CPU Sensor Service? Windows will ask for administrator approval once. The main application stays non-elevated.";
            if (System.Windows.MessageBox.Show(_settingsWindow, message, "DevOverlay Sensor Service",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            viewModel.UpdateSensorServiceActionProgress(action);
            var result = await _sensorServiceManagement.ExecuteAsync(action);
            if (_settingsViewModel != viewModel) return;
            viewModel.UpdateSensorServiceStatus(_sensorServiceStatusReader.Read());
            viewModel.UpdateSensorServiceActionResult(result);
        }
        finally { _sensorServiceActionGate.Release(); }
    }

    private void InstallPresentMon()
    {
        if (System.Windows.MessageBox.Show(_settingsWindow,
                "Open Intel's official PresentMon 2.6.0 release page? Install its service using the official installer. " +
                "DevOverlay will not request elevation or install it automatically.",
                "Install PresentMon", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(PresentMonServiceInfo.OfficialReleaseUrl) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            System.Windows.MessageBox.Show(_settingsWindow, exception.Message, "PresentMon", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ToggleOverlayVisibility()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ToggleOverlayVisibility);
            return;
        }

        SetOverlayVisibility(!_settings.IsVisible);
    }

    private void SetOverlayVisibility(bool isVisible)
    {
        _settings = _settings with { IsVisible = isVisible };
        _fpsProvider?.Configure(_settings);
        ApplyOverlayVisibility();
        SaveSettings();
    }

    private void ApplyOverlayVisibility()
    {
        if (_overlayWindow is null) return;
        if (_settings.IsVisible)
        {
            if (!_overlayWindow.IsVisible) _overlayWindow.ShowWithoutActivation();
            _overlayWindow.Dispatcher.BeginInvoke(_overlayWindow.ReapplyPosition,
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else _overlayWindow.Hide();
    }

    private void ChangeGlobalHotkey(SettingsViewModel viewModel, OverlayHotkey hotkey)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ChangeGlobalHotkey(viewModel, hotkey));
            return;
        }

        if (!_globalHotkeyService.TryReplace(hotkey, out var error))
        {
            _hotkeyStatusText = error ?? "단축키를 변경하지 못했습니다.";
            viewModel.UpdateHotkeyStatus(_hotkeyStatusText);
            return;
        }

        _settings = _settings with { Hotkey = hotkey.Normalize() };
        _hotkeyStatusText = "Global hotkey: active";
        viewModel.UpdateHotkey(_settings.Hotkey, _hotkeyStatusText);
        SaveSettings();
    }

    private void EnableOverlayPositionEditing()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(EnableOverlayPositionEditing);
            return;
        }

        if (_overlayWindow?.IsVisible == true) _overlayWindow.EnablePositionEditing();
    }

    private void OnCustomPositionChanged(OverlayPosition position)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnCustomPositionChanged(position));
            return;
        }

        _settings = _settings with { Position = position };
        _settingsViewModel?.UpdatePosition(position);
        _overlayWindow?.ApplySettings(position, _settings.Appearance);
        SaveSettings();
    }

    private void SaveSettings()
    {
#if DEBUG
        // The explicit runtime probe exercises production wiring without changing user JSON.
        if (_runtimeProbe) return;
#endif
        try
        {
            _settingsStore?.Save(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Settings could not be saved: {exception}");
        }
    }

    private void ExitApplication()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ExitApplication);
            return;
        }

        Shutdown();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _globalHotkeyService.Pressed -= ToggleOverlayVisibility;
        _globalHotkeyService.Dispose();
        if (_overlayWindow is not null)
        {
            _overlayWindow.CustomPositionChanged -= OnCustomPositionChanged;
            _overlayWindow.Close();
            _overlayWindow = null;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_metricUpdateService is not null)
        {
            await _metricUpdateService.DisposeAsync();
        }
        if (_fpsUpdateService is not null) await _fpsUpdateService.DisposeAsync();
        await _sensorServiceClient.DisposeAsync();

        base.OnExit(e);
    }

#if DEBUG
    private async Task ProbeStartupAsync()
    {
        try
        {
            await Task.Delay(1500);
            var window = _overlayWindow!;
            var providers = _metricUpdateService;
            var fps = _fpsProvider;
            RuntimeDiagnostics.Write($"[StartupProbe] InitialVisible={window.IsVisible} Visibility={window.Visibility} HWND={new System.Windows.Interop.WindowInteropHelper(window).Handle} Bounds={window.Left},{window.Top},{window.ActualWidth},{window.ActualHeight} Hotkey={_hotkeyStatusText}");
            OpenSettings();
            await Task.Delay(300);
            RuntimeDiagnostics.Write($"[StartupProbe] SettingsVisible={_settingsWindow?.IsVisible} CaptureField={_settingsWindow?.FindName("HotkeyCaptureTextBox") is not null} Status={_settingsViewModel?.HotkeyStatusText}");
            var before = _settings.IsVisible;
            var handled = _globalHotkeyService.ProcessWindowMessage(0x0312, (nint)0x444F);
            RuntimeDiagnostics.Write($"[StartupProbe] HotkeyHandled={handled} Before={before} After={window.IsVisible}");
            if (handled) _globalHotkeyService.ProcessWindowMessage(0x0312, (nint)0x444F);
            await Task.Delay(300);
            RuntimeDiagnostics.Write($"[StartupProbe] Restored={window.IsVisible == before} SameWindow={ReferenceEquals(window, _overlayWindow)} SameProviders={ReferenceEquals(providers, _metricUpdateService)} SameFps={ReferenceEquals(fps, _fpsProvider)}");
        }
        catch (Exception exception) { RuntimeDiagnostics.Write($"[StartupProbeFailure] {exception}"); }
        finally { _settingsWindow?.Close(); Shutdown(); }
    }

    private async Task ProbeRuntimeAsync()
    {
        try
        {
            await Task.Delay(2000);
            OpenSettings();
            await Task.Delay(300);
            var refreshSlider = VisualDescendants(_settingsWindow!).OfType<System.Windows.Controls.Slider>().Single(element =>
                System.Windows.Data.BindingOperations.GetBinding(element, System.Windows.Controls.Primitives.RangeBase.ValueProperty)?.Path.Path == nameof(SettingsViewModel.RefreshIntervalSliderValue));
            var refreshInput = VisualDescendants(_settingsWindow!).OfType<System.Windows.Controls.TextBox>().Single(element =>
                System.Windows.Data.BindingOperations.GetBinding(element, System.Windows.Controls.TextBox.TextProperty)?.Path.Path == nameof(SettingsViewModel.RefreshIntervalText));
            foreach (var interval in new[] { 2000, 250 })
            {
                refreshSlider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, (double)interval);
                await Task.Delay(100);
                RuntimeDiagnostics.Write($"[RefreshInterval] Source=Slider Value={_settingsViewModel!.RefreshIntervalMs} NormalScheduler={_metricUpdateService?.RefreshInterval.TotalMilliseconds} FpsScheduler={_fpsUpdateService?.RefreshInterval.TotalMilliseconds}");
            }
            refreshInput.Text = "333";
            System.Windows.Data.BindingOperations.GetBindingExpression(refreshInput, System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            _settingsViewModel!.CommitRefreshIntervalText();
            await Task.Delay(100);
            RuntimeDiagnostics.Write($"[RefreshInterval] Source=Text Value={_settingsViewModel.RefreshIntervalMs} Slider={refreshSlider.Value} NormalScheduler={_metricUpdateService?.RefreshInterval.TotalMilliseconds} FpsScheduler={_fpsUpdateService?.RefreshInterval.TotalMilliseconds}");
            var slider = VisualDescendants(_settingsWindow!).OfType<System.Windows.Controls.Slider>().Single(element =>
                System.Windows.Data.BindingOperations.GetBinding(element, System.Windows.Controls.Primitives.RangeBase.ValueProperty)?.Path.Path == nameof(SettingsViewModel.SpacingPercent));
            foreach (var percent in new[] { 40d, 160d })
            {
                slider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, percent);
                await Dispatcher.InvokeAsync(() => _overlayWindow!.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                RuntimeDiagnostics.Write($"[Rendered] Slider={slider.Value} VM={_settingsViewModel!.SpacingPercent} Settings={_settings.Appearance.Spacing} Width={_overlayWindow!.ActualWidth} Height={_overlayWindow.ActualHeight}");
                foreach (var element in VisualDescendants(_overlayWindow).OfType<FrameworkElement>().Where(element =>
                    element is System.Windows.Controls.TextBlock or System.Windows.Controls.StackPanel or System.Windows.Controls.Border))
                    RuntimeDiagnostics.Write($"[Element] Type={element.GetType().Name} Text={(element as System.Windows.Controls.TextBlock)?.Text} Margin={element.Margin} Width={element.ActualWidth} Font={(element as System.Windows.Controls.TextBlock)?.FontSize}");
            }
            RuntimeDiagnostics.Write($"[Groups] {string.Join(',', _overlayViewModel!.Groups.Select(group => group.Title))} Connected={_fpsProvider!.HasConnection} Target={_fpsProvider.CurrentTarget?.Pid}");
            // Keep the acquired game while Settings owns foreground; never close the user's target.
            if (_fpsProvider.CurrentTarget is { } target)
            {
                _settingsViewModel!.FpsTargetId = target.Identity;
                await Task.Delay(12000);
                foreach (var mode in new[] { (Fps: true, Lat: true), (Fps: true, Lat: false), (Fps: false, Lat: true), (Fps: false, Lat: false), (Fps: true, Lat: true) })
                {
                    _settingsViewModel.FpsEnabled = mode.Fps;
                    _settingsViewModel.LatencyEnabled = mode.Lat;
                    await Task.Delay(mode.Fps && mode.Lat ? 8000 : 3000);
                    _overlayWindow!.UpdateLayout();
                    RuntimeDiagnostics.Write($"[Mode] FPS={mode.Fps} LAT={mode.Lat} Connected={_fpsProvider.HasConnection} Target={_fpsProvider.CurrentTarget?.Pid} Groups={string.Join(',', _overlayViewModel.Groups.Select(group => group.Title))}");
                    foreach (var group in _overlayViewModel.Groups.Where(group => group.Category is MetricCategory.Frame or MetricCategory.Latency))
                        RuntimeDiagnostics.Write($"[HUD] {group.Title} {string.Join(' ', group.Items.Select(item => item.Text))}");
                }
            }
        }
        catch (Exception exception) { RuntimeDiagnostics.Write($"[ProbeFailure] {exception}"); }
        finally { _settingsWindow?.Close(); Shutdown(); }
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }
#endif
}
