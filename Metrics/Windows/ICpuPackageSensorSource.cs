namespace DevOverlay.Metrics.Windows;

public readonly record struct CpuPackageReadings(double? TemperatureCelsius, double? PackagePowerWatts);

public interface ICpuPackageSensorSource
{
    Task<CpuPackageReadings> ReadAsync(CancellationToken cancellationToken);
}
