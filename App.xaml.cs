using System.Windows;
using System.Diagnostics;
using DevOverlay.Configuration;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Demo;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.Presentation;
using DevOverlay.UI;

namespace DevOverlay;

public partial class App : Application
{
    private MetricUpdateService? _metricUpdateService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = OverlaySettings.CreateDefault();
        var providers = new IMetricProvider[]
        {
            new CpuUtilizationMetricProvider(),
            new NvidiaGpuMetricProvider(),
            new DemoMetricProvider()
        };
        _metricUpdateService = new MetricUpdateService(providers);

        var viewModel = new OverlayViewModel(settings, Dispatcher);
        _metricUpdateService.MetricsUpdated += viewModel.ApplyMetrics;
        _metricUpdateService.ProviderFaulted += (provider, exception) =>
            Debug.WriteLine($"Metric provider '{provider.Name}' failed: {exception}");

        var window = new OverlayWindow(viewModel, new OverlayPositioningService());
        MainWindow = window;
        window.Show();

        _metricUpdateService.Start();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_metricUpdateService is not null)
        {
            await _metricUpdateService.DisposeAsync();
        }

        base.OnExit(e);
    }
}
