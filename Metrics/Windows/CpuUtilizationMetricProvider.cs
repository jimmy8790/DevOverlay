using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DevOverlay.Metrics.Windows;

/// <summary>
/// Collects total Windows CPU utilization from cumulative idle, kernel, and user processor times.
/// The first sample establishes a baseline and is intentionally reported as unavailable.
/// </summary>
public sealed class CpuUtilizationMetricProvider : IMetricProvider
{
    private readonly object _syncRoot = new();
    private CpuTimes? _previousTimes;

    public string Name => "Windows CPU utilization";
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);

    public Task<IReadOnlyCollection<MetricSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentTimes = ReadCpuTimes();
        MetricSnapshot snapshot;

        lock (_syncRoot)
        {
            if (_previousTimes is null)
            {
                _previousTimes = currentTimes;
                snapshot = MetricSnapshot.Unavailable(
                    MetricId.CpuUtilization,
                    MetricCategory.Cpu,
                    "CPU",
                    "%");
            }
            else
            {
                snapshot = CreateUtilizationSnapshot(_previousTimes.Value, currentTimes);
                _previousTimes = currentTimes;
            }
        }

        IReadOnlyCollection<MetricSnapshot> metrics = [snapshot];
        return Task.FromResult(metrics);
    }

    private static MetricSnapshot CreateUtilizationSnapshot(CpuTimes previous, CpuTimes current)
    {
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User)
        {
            return MetricSnapshot.Unavailable(
                MetricId.CpuUtilization,
                MetricCategory.Cpu,
                "CPU",
                "%");
        }

        var idleDelta = current.Idle - previous.Idle;
        var kernelDelta = current.Kernel - previous.Kernel;
        var userDelta = current.User - previous.User;
        var totalDelta = kernelDelta + userDelta;

        // Kernel time already includes idle time, so only the non-idle portion is busy CPU time.
        if (totalDelta == 0 || idleDelta > totalDelta)
        {
            return MetricSnapshot.Unavailable(
                MetricId.CpuUtilization,
                MetricCategory.Cpu,
                "CPU",
                "%");
        }

        var utilization = 100d * (totalDelta - idleDelta) / totalDelta;
        return new MetricSnapshot(
            MetricId.CpuUtilization,
            MetricCategory.Cpu,
            "CPU",
            Math.Clamp(utilization, 0d, 100d),
            "%",
            true,
            DateTimeOffset.UtcNow);
    }

    private static CpuTimes ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemTimes failed.");
        }

        return new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime(uint lowDateTime, uint highDateTime)
    {
        public readonly uint LowDateTime = lowDateTime;
        public readonly uint HighDateTime = highDateTime;

        public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
}
