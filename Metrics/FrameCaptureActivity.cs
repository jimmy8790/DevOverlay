using System.Diagnostics;

namespace DevOverlay.Metrics;

/// <summary>
/// DevOverlay's own periodic work (provider collections, HUD updates, PresentMon polls) recorded only while a
/// diagnostic frame capture is running, so slow game frames can be tested for coincidence with that work.
/// Inactive cost is one volatile read. Bounded; never touches the filesystem.
/// </summary>
internal static class FrameCaptureActivity
{
    internal const int MaxMarks = 100_000;
    private static readonly object Gate = new();
    private static volatile bool _active;
    private static List<ActivityMark> _marks = [];
    private static bool _truncated;

    internal static bool IsActive => _active;

    internal static void Begin()
    {
        lock (Gate)
        {
            _marks = new List<ActivityMark>(4096);
            _truncated = false;
            _active = true;
        }
    }

    internal static (ActivityMark[] Marks, bool Truncated) End()
    {
        lock (Gate)
        {
            _active = false;
            var marks = _marks.ToArray();
            _marks = [];
            return (marks, _truncated);
        }
    }

    internal static void Mark(string source)
    {
        if (!_active) return;
        Mark(source, (ulong)Stopwatch.GetTimestamp());
    }

    internal static void Mark(string source, ulong qpc)
    {
        if (!_active) return;
        lock (Gate)
        {
            if (!_active) return;
            if (_marks.Count < MaxMarks) _marks.Add(new ActivityMark(qpc, source));
            else _truncated = true;
        }
    }
}

internal readonly record struct ActivityMark(ulong Qpc, string Source);
