using System.Diagnostics;

namespace DevOverlay.Metrics.Windows;

/// <summary>Why a raw Present row did or did not enter the production 1% window (diagnostics only).</summary>
internal enum FrameAdmission : byte
{
    Accepted = 0,
    ZeroQpc = 1,
    /// <summary>Same QPC as the previous accepted row: an extra display-instance row of an already counted Present.</summary>
    DuplicateQpc = 2,
    NonAdvancingQpc = 3,
    FutureQpc = 4,
    TooOld = 5,
    InvalidInterval = 6,
    /// <summary>Presented before tracking of this PID started; skipped before the window is consulted.</summary>
    BeforeTrackingCutoff = 7
}

/// <summary>
/// One tracked process's raw frames. Admission and source freshness follow the Present stream; the 1% Low samples are
/// the display-change intervals of those admitted Presents (what reaches the screen, matching NVIDIA-style 1% Low).
/// </summary>
internal sealed class PresentedFrameWindow
{
    /// <summary>
    /// 1% Low covers the displayed frames of the last 3 s. Measured against NVIDIA Overlay (Modern Warships, 2026-10-02):
    /// 3 s was the closest window without adding output delay, and a 15 s window kept one hitch on the HUD for 15 s.
    /// </summary>
    internal static readonly TimeSpan OnePercentLowWindow = TimeSpan.FromSeconds(3);
    private static readonly ulong WindowTicks = (ulong)(Stopwatch.Frequency * OnePercentLowWindow.TotalSeconds);
    private readonly Queue<(ulong Qpc, double Ms)> _samples = new();
    internal ulong LastQpc { get; private set; }

    internal bool Add(ulong qpc, double presentMilliseconds, double displayMilliseconds, ulong now) =>
        Admit(qpc, presentMilliseconds, displayMilliseconds, now) == FrameAdmission.Accepted;

    /// <summary>
    /// Production admission rule, decided on the Present row only:
    /// <c>qpc == 0 || qpc &lt;= LastQpc || qpc &gt; now || age &gt; 15 s || !finite || presentMs &lt;= 0</c> rejects.
    /// An admitted Present always advances <see cref="LastQpc"/>; it adds a 1% sample only when it has a finite,
    /// positive display-change interval (a Present that never reached the screen has none).
    /// </summary>
    internal FrameAdmission Admit(ulong qpc, double presentMilliseconds, double displayMilliseconds, ulong now)
    {
        var reason = qpc == 0 ? FrameAdmission.ZeroQpc
            : qpc == LastQpc ? FrameAdmission.DuplicateQpc
            : qpc < LastQpc ? FrameAdmission.NonAdvancingQpc
            : qpc > now ? FrameAdmission.FutureQpc
            : now - qpc > (ulong)(Stopwatch.Frequency * 15) ? FrameAdmission.TooOld
            : !double.IsFinite(presentMilliseconds) || presentMilliseconds <= 0 ? FrameAdmission.InvalidInterval
            : FrameAdmission.Accepted;
        if (reason != FrameAdmission.Accepted) return reason;
        LastQpc = qpc;
        if (IsDisplayInterval(displayMilliseconds)) _samples.Enqueue((qpc, displayMilliseconds));
        Evict(now);
        return FrameAdmission.Accepted;
    }

    internal static bool IsDisplayInterval(double milliseconds) => double.IsFinite(milliseconds) && milliseconds > 0;

    internal double? Calculate(ulong now)
    {
        Evict(now);
        if (_samples.Count == 0) return null;
        var times = _samples.Select(sample => sample.Ms).ToArray();
        Array.Sort(times);
        var slowCount = Math.Max(1, (int)Math.Ceiling(times.Length * 0.01));
        double sum = 0;
        for (var index = times.Length - slowCount; index < times.Length; index++) sum += times[index];
        return 1000.0 / (sum / slowCount);
    }

    private void Evict(ulong now)
    {
        while (_samples.TryPeek(out var sample) && (now - sample.Qpc > WindowTicks || _samples.Count > 65536))
            _samples.Dequeue();
    }
}
