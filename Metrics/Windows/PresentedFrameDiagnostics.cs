using System.Diagnostics;

namespace DevOverlay.Metrics.Windows;

/// <summary>Opt-in comparison only; never supplies HUD values or changes source freshness.</summary>
internal sealed class PresentedFrameDiagnostics
{
    private readonly Queue<Sample> _samples = new();
    private bool _truncated;
    private long _received;
    private long _rejected;
    private long _rawDropped;
    private long _rawGenerated;

    internal void Observe(ulong qpc, double ms, bool accepted, bool? dropped, int? frameType)
    {
        _received++;
        if (dropped == true) _rawDropped++;
        if (frameType is 50 or 100) _rawGenerated++;
        if (!accepted) { _rejected++; return; }
        _samples.Enqueue(new(qpc, ms, dropped, frameType));
        if (_samples.Count > 65536) { _samples.Dequeue(); _truncated = true; }
    }

    internal FrameStatistics Statistics(ulong now, int? seconds)
    {
        var samples = _samples.Where(sample => sample.Qpc <= now &&
            (!seconds.HasValue || now - sample.Qpc <= (ulong)(Stopwatch.Frequency * seconds.Value))).ToArray();
        var times = samples.Select(sample => sample.Ms).Order().ToArray();
        if (times.Length == 0) return default;
        var slowCount = (int)Math.Ceiling(times.Length * 0.01);
        var slowMean = times.TakeLast(slowCount).Average();
        var median = times.Length % 2 == 1 ? times[times.Length / 2] :
            (times[times.Length / 2 - 1] + times[times.Length / 2]) / 2;
        return new(times.Length, slowCount, times[0], median, times[^1], slowMean, 1000 / slowMean,
            samples.Count(sample => sample.Dropped == true),
            samples.Count(sample => sample.FrameType is 50 or 100),
            samples.Count(sample => sample.FrameType is null or 0 or 1));
    }

    internal string Report(ulong now)
    {
        var stats = Statistics(now, 15);
        var horizons = new int?[] { 5, 10, 15, 30, 60, null }.Select(seconds =>
            $"{(seconds.HasValue ? seconds.Value.ToString() : "SinceAcquisition")}={Statistics(now, seconds).LowFps:F2}");
        return $"Source=PM_METRIC_BETWEEN_PRESENTS Received={_received} Rejected={_rejected} RawDropped={_rawDropped} RawKnownGenerated={_rawGenerated} N={stats.Count} SlowCount={stats.SlowCount} MinMs={stats.MinMs:F3} MedianMs={stats.MedianMs:F3} MaxMs={stats.MaxMs:F3} SlowMeanMs={stats.SlowMeanMs:F3} Low={stats.LowFps:F2} DroppedOrNonDisplayed={stats.Dropped} KnownGenerated={stats.Generated} UnknownType={stats.UnknownType} ZeroDisplay=NotSeparatelyExposed SinceTruncated={_truncated} Windows:{string.Join(';', horizons)}";
    }

    private readonly record struct Sample(ulong Qpc, double Ms, bool? Dropped, int? FrameType);
    internal readonly record struct FrameStatistics(int Count, int SlowCount, double MinMs, double MedianMs,
        double MaxMs, double SlowMeanMs, double LowFps, int Dropped, int Generated, int UnknownType);
}
