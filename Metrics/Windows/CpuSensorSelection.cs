using LibreHardwareMonitor.Hardware;

namespace DevOverlay.Metrics.Windows;

/// <summary>Only package-level sensors are interchangeable with the HUD's CPU readings.</summary>
internal static class CpuSensorSelection
{
    public static double? ValidTemperature(double? value) =>
        value is >= -20 and <= 150 && double.IsFinite(value.Value) ? value : null;

    public static double? ValidPower(double? value) =>
        value is > 0 and <= 1000 && double.IsFinite(value.Value) ? value : null;

    public static bool IsPackageSensorName(string name) =>
        string.Equals(name, "CPU Package", StringComparison.OrdinalIgnoreCase);

    public static (ISensor? Temperature, ISensor? Power) FindPackageSensors(IEnumerable<ISensor> sensors)
    {
        ISensor? temperature = null;
        ISensor? power = null;
        foreach (var sensor in sensors)
        {
            if (!IsPackageSensorName(sensor.Name)) continue;
            if (sensor.SensorType == SensorType.Temperature) temperature ??= sensor;
            else if (sensor.SensorType == SensorType.Power) power ??= sensor;
        }
        return (temperature, power);
    }
}
