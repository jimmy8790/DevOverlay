namespace DevOverlay.Metrics.Windows;

internal static class NetworkThroughputFormatter
{
    public static ByteRateDisplay Format(double bytesPerSecond) => ByteRateFormatter.Format(bytesPerSecond);
}
