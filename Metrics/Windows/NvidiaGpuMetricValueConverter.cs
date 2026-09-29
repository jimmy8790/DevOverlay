namespace DevOverlay.Metrics.Windows;

internal static class NvidiaGpuMetricValueConverter
{
    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;

    public static double ToPercentage(uint value) => Math.Clamp(value, 0, 100);

    public static double MilliwattsToWatts(uint milliwatts) => milliwatts / 1_000d;

    public static double BytesToGigabytes(ulong bytes) => bytes / BytesPerGigabyte;
}
