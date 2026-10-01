namespace DevOverlay.Metrics.Windows;

internal readonly record struct ByteRateDisplay(double Value, string Unit);

/// <summary>Formats a rate already expressed in bytes per second for the compact overlay.</summary>
internal static class ByteRateFormatter
{
    private static readonly string[] Units = ["B/s", "KB/s", "MB/s", "GB/s"];

    public static ByteRateDisplay Format(double bytesPerSecond)
    {
        var value = Math.Max(0, bytesPerSecond);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return new ByteRateDisplay(value, Units[unitIndex]);
    }
}
