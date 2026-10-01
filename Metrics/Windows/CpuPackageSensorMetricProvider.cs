using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace DevOverlay.Metrics.Windows;

/// <summary>Reads LHM's Intel/AMD package sensors; never substitutes core, SoC, or power-limit values.</summary>
public sealed class CpuPackageSensorMetricProvider : IMetricProvider, IAsyncDisposable
{
    private readonly ICpuPackageSensorSource? _source;
    private Computer? _computer;
    private IHardware? _cpu;
    private ISensor? _temperature;
    private ISensor? _power;
    private bool _initialized;
    private int _reinitializeRequested;
    private int _availableSensorCount;
    private int _contextGeneration;

    public string Name => "CPU package sensors";
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(2);
    public bool HasTemperatureReading => (Volatile.Read(ref _availableSensorCount) & 1) != 0;
    public bool HasPowerReading => (Volatile.Read(ref _availableSensorCount) & 2) != 0;
    public bool HasPackageReadings => Volatile.Read(ref _availableSensorCount) == 3;
    internal int ContextGeneration => Volatile.Read(ref _contextGeneration);

    public CpuPackageSensorMetricProvider(ICpuPackageSensorSource? source = null) => _source = source;

    public void RequestReinitialize() => Interlocked.Exchange(ref _reinitializeRequested, 1);

    public async Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_source is not null)
            {
                var readings = await _source.ReadAsync(cancellationToken).ConfigureAwait(false);
                return CreateSnapshots(readings.TemperatureCelsius, readings.PackagePowerWatts);
            }
            if (Interlocked.Exchange(ref _reinitializeRequested, 0) == 1)
            {
                CloseComputer();
            }
            InitializeOnce();
            _cpu?.Update();
            return CreateSnapshots(_temperature?.Value, _power?.Value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Volatile.Write(ref _availableSensorCount, 0);
            Debug.WriteLine($"CPU package sensors unavailable: {exception}");
            return Unavailable();
        }
    }

    private IReadOnlyCollection<MetricSnapshot> CreateSnapshots(double? rawTemperature, double? rawPower)
    {
        var temperature = CpuSensorSelection.ValidTemperature(rawTemperature);
        var power = CpuSensorSelection.ValidPower(rawPower);
        Volatile.Write(ref _availableSensorCount, (temperature.HasValue ? 1 : 0) | (power.HasValue ? 2 : 0));
        var timestamp = DateTimeOffset.UtcNow;
        return
        [
            new(MetricId.CpuTemperature, MetricCategory.Cpu, "CPU 패키지 온도", temperature, "°C", temperature.HasValue, timestamp),
            new(MetricId.CpuPower, MetricCategory.Cpu, "CPU 패키지 전력", power, "W", power.HasValue, timestamp)
        ];
    }

    private void InitializeOnce()
    {
        if (_initialized) return;
        _initialized = true;
        _computer = new Computer { IsCpuEnabled = true };
        _computer.Open();
        Interlocked.Increment(ref _contextGeneration);
        _cpu = _computer.Hardware.FirstOrDefault(hardware => hardware.HardwareType == HardwareType.Cpu);
        if (_cpu is null) return;
        _cpu.Update();
        (_temperature, _power) = CpuSensorSelection.FindPackageSensors(_cpu.Sensors);
    }

    public ValueTask DisposeAsync()
    {
        CloseComputer();
        return ValueTask.CompletedTask;
    }

    private void CloseComputer()
    {
        _computer?.Close();
        _computer = null;
        _cpu = null;
        _temperature = null;
        _power = null;
        _initialized = false;
        Volatile.Write(ref _availableSensorCount, 0);
    }

    private static IReadOnlyCollection<MetricSnapshot> Unavailable() =>
    [
        MetricSnapshot.Unavailable(MetricId.CpuTemperature, MetricCategory.Cpu, "CPU 패키지 온도", "°C"),
        MetricSnapshot.Unavailable(MetricId.CpuPower, MetricCategory.Cpu, "CPU 패키지 전력", "W")
    ];
}
