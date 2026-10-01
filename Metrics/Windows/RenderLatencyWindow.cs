using System.Diagnostics;

namespace DevOverlay.Metrics.Windows;

/// <summary>Bounded two-second means of a single target's frame-query values, selected by swap chain.</summary>
internal sealed class RenderLatencyWindow
{
    private readonly Sample[] _samples = new Sample[8192];
    private int _head;
    private int _count;

    internal void Clear() { _head = _count = 0; }

    internal void Add(ulong qpc, double latency, ulong now, ulong swapChain = 0)
    {
        if (!double.IsFinite(latency) || latency < 0 || qpc == 0 || qpc > now ||
            now - qpc > (ulong)(Stopwatch.Frequency * 2)) return;
        if (_count == _samples.Length) RemoveOldest();
        _samples[(_head + _count) % _samples.Length] = new Sample(qpc, latency, swapChain);
        _count++;
    }

    internal double? Mean(ulong now, ulong? swapChain = null)
    {
        while (_count > 0 && now >= _samples[_head].Qpc &&
            now - _samples[_head].Qpc > (ulong)(Stopwatch.Frequency * 2)) RemoveOldest();
        double sum = 0;
        var validCount = 0;
        for (var index = 0; index < _count; index++)
        {
            var sample = _samples[(_head + index) % _samples.Length];
            // Delivery can interleave swap chains; individually filter timestamps as well.
            if ((swapChain.HasValue && sample.SwapChain != swapChain.Value) || sample.Qpc > now ||
                now - sample.Qpc > (ulong)(Stopwatch.Frequency * 2)) continue;
            sum += sample.Value;
            validCount++;
        }
        return validCount == 0 ? null : sum / validCount;
    }

    private void RemoveOldest()
    {
        _head = (_head + 1) % _samples.Length;
        _count--;
    }

    private readonly record struct Sample(ulong Qpc, double Value, ulong SwapChain);
}
