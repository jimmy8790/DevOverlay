using System.Diagnostics;

namespace DevOverlay.Metrics.Windows;

internal readonly record struct NetworkCounterSample(
    string InterfaceSignature,
    ulong DownloadBytes,
    ulong UploadBytes,
    long Timestamp);

internal readonly record struct NetworkThroughput(double DownloadBytesPerSecond, double UploadBytesPerSecond);

internal static class NetworkThroughputCalculator
{
    public static bool TryCalculate(
        NetworkCounterSample? previous,
        NetworkCounterSample current,
        out NetworkThroughput throughput)
    {
        throughput = default;
        if (previous is null || previous.Value.InterfaceSignature != current.InterfaceSignature)
        {
            return false;
        }

        var elapsed = Stopwatch.GetElapsedTime(previous.Value.Timestamp, current.Timestamp);
        if (elapsed <= TimeSpan.Zero ||
            current.DownloadBytes < previous.Value.DownloadBytes ||
            current.UploadBytes < previous.Value.UploadBytes)
        {
            return false;
        }

        throughput = new NetworkThroughput(
            (current.DownloadBytes - previous.Value.DownloadBytes) / elapsed.TotalSeconds,
            (current.UploadBytes - previous.Value.UploadBytes) / elapsed.TotalSeconds);
        return true;
    }
}
