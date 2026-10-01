using System.Diagnostics;
using DevOverlay.Metrics.Windows;
using DevOverlay.Platform.Windows;
using DevOverlay.SensorHostProtocol;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace DevOverlay.SensorHostCore;

internal sealed class CpuPackageReader : IDisposable
{
    private Computer? _computer;
    private IHardware? _cpu;
    private ISensor? _temperature;
    private ISensor? _power;
    private SensorHostState _state;
    private string? _detail;
    private bool _reportedTemperature;
    private bool _reportedPower;

    public SensorHostResponse Initialize()
    {
        Dispose();
        _computer = null;
        _cpu = null;
        _temperature = null;
        _power = null;
        _reportedTemperature = false;
        _reportedPower = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            if (key?.GetValue("DisplayVersion") is not string version || string.IsNullOrWhiteSpace(version))
                return Status(SensorHostState.PawnIoUnavailable, "PawnIO registry version is missing.");
            Trace.WriteLine($"SensorHost: PawnIO {version} installed.");
            var access = PawnIoDeviceAccessProbe.Probe();
            if (!access.IsAccessible)
                return Status(access.Win32Error == 5 ? SensorHostState.PawnIoAccessDenied : SensorHostState.PawnIoUnavailable,
                    $"PawnIO device open failed: Windows error {access.Win32Error}.");
            Trace.WriteLine("SensorHost: PawnIO device accessible.");

            _computer = new Computer { IsCpuEnabled = true };
            _computer.Open();
            _cpu = _computer.Hardware.FirstOrDefault(hardware => hardware.HardwareType == HardwareType.Cpu);
            if (_cpu is null) return Status(SensorHostState.CpuHardwareUnavailable, "LibreHardwareMonitor did not enumerate a CPU.");
            Trace.WriteLine($"SensorHost: CPU {_cpu.Name} ({_cpu.Identifier}).");
            _cpu.Update();
            (_temperature, _power) = CpuSensorSelection.FindPackageSensors(_cpu.Sensors);
            Trace.WriteLine($"SensorHost: package temperature {_temperature?.Identifier}; package power {_power?.Identifier}.");
            return Read();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"SensorHost initialization failed: {exception}");
            return Status(SensorHostState.Failed, exception.GetType().Name);
        }
    }

    public SensorHostResponse Read()
    {
        if (_cpu is null) return Status(_state, _detail);
        try
        {
            _cpu.Update();
            var temperature = CpuSensorSelection.ValidTemperature(_temperature?.Value);
            var power = CpuSensorSelection.ValidPower(_power?.Value);
            if (temperature.HasValue && !_reportedTemperature)
            {
                Trace.WriteLine("SensorHost: first valid CPU Package temperature obtained.");
                _reportedTemperature = true;
            }
            if (power.HasValue && !_reportedPower)
            {
                Trace.WriteLine("SensorHost: first valid CPU Package power obtained.");
                _reportedPower = true;
            }
            _state = temperature.HasValue && power.HasValue ? SensorHostState.Ready : SensorHostState.CpuSensorsUnavailable;
            return new SensorHostResponse(SensorHostWire.Version, SensorHostMessageKind.CpuPackageSnapshot,
                _state, temperature, power, DateTimeOffset.UtcNow,
                _state == SensorHostState.Ready ? null :
                    $"Package sensors found: temperature={_temperature is not null}, power={_power is not null}; " +
                    $"valid readings: temperature={temperature.HasValue}, power={power.HasValue}.",
                _temperature is not null, _power is not null);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"SensorHost CPU update failed: {exception}");
            return Status(SensorHostState.Failed, exception.GetType().Name);
        }
    }

    private SensorHostResponse Status(SensorHostState state, string? detail)
    {
        _state = state;
        _detail = detail;
        Trace.WriteLine($"SensorHost: {state}: {detail}");
        return new SensorHostResponse(SensorHostWire.Version, SensorHostMessageKind.CpuPackageSnapshot,
            state, TimestampUtc: DateTimeOffset.UtcNow, Detail: detail,
            TemperatureSensorFound: _temperature is not null, PowerSensorFound: _power is not null);
    }

    public void Dispose() => _computer?.Close();
}
