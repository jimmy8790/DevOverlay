using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;

namespace DevOverlay.Platform.Windows;

/// <summary>
/// Always-on, bounded record of rare lifecycle and failure events: startup, shutdown reason, overlay visibility changes,
/// unhandled managed exceptions and provider faults. <see cref="RuntimeDiagnostics"/> is Debug-only, so before this log a
/// release build that terminated left no evidence of why. Never use it for per-sample or per-frame data; writing never throws.
/// </summary>
internal sealed class LifecycleLog
{
    internal const long DefaultMaxBytes = 256 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset LastWritten, int Suppressed)> _throttle = [];
    private readonly Func<DateTimeOffset> _clock;
    private readonly long _maxBytes;

    internal LifecycleLog(string path, Func<DateTimeOffset>? clock = null, long maxBytes = DefaultMaxBytes)
    {
        Path = path;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _maxBytes = maxBytes;
    }

    internal static string DefaultDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevOverlay", "Diagnostics");

    // Settable so the test assembly can redirect it and never write into the user's real log.
    internal static LifecycleLog Current { get; set; } = new(System.IO.Path.Combine(DefaultDirectory, "lifecycle.log"));

    internal string Path { get; }

    // Slowly changing facts (current FPS target, selected GPU) that explain a later crash. Updated only on change.
    private static readonly ConcurrentDictionary<string, string> Context = new();
    internal static void SetContext(string key, string? value)
    {
        if (value is null) Context.TryRemove(key, out _);
        else Context[key] = value;
    }
    internal static string DescribeContext() =>
        Context.IsEmpty ? "None" : string.Join(' ', Context.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

    internal void Write(string message)
    {
        try
        {
            var line = $"{_clock():O} [{Environment.ProcessId}:{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                Rotate();
                File.AppendAllText(Path, line);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException)
        {
            Debug.WriteLine($"Lifecycle log unavailable: {exception.Message}");
        }
    }

    /// <summary>Repeating faults (for example a provider failing every poll) are written at most once per interval.</summary>
    internal void WriteThrottled(string key, string message, TimeSpan interval)
    {
        int suppressed;
        lock (_gate)
        {
            var now = _clock();
            if (_throttle.TryGetValue(key, out var state) && now - state.LastWritten < interval)
            {
                _throttle[key] = (state.LastWritten, state.Suppressed + 1);
                return;
            }
            suppressed = state.Suppressed;
            _throttle[key] = (now, 0);
        }
        Write(suppressed > 0 ? $"{message} (+{suppressed} similar suppressed)" : message);
    }

    /// <summary>
    /// For AppDomain.UnhandledException. This fires for fatal MANAGED exceptions. It does NOT fire for a hardware access
    /// violation (verified on .NET 8: the process dies first), so native-interop crashes are covered by the Windows
    /// Application log (event 1026 carries the stack) plus <see cref="SessionMarker"/> on the next start, not by this method.
    /// </summary>
    internal void WriteFatal(object? exceptionObject, bool isTerminating)
    {
        var process = Process.GetCurrentProcess();
        Write($"[Fatal] Terminating={isTerminating} Uptime={(DateTimeOffset.Now - process.StartTime):c} " +
              $"WorkingSetMB={process.WorkingSet64 / (1024 * 1024)} Context=[{DescribeContext()}] Exception={exceptionObject}");
    }

    private void Rotate()
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length < _maxBytes) return;
        File.Move(Path, Path + ".old", overwrite: true);
    }
}

/// <summary>
/// Detects that the previous process died without a clean exit (crash, kill, power loss) even when no handler could run,
/// and preserves what it was doing: a small file is written at start, refreshed by a low-rate heartbeat with the current
/// context, and deleted by a clean exit. The next start reports a leftover file.
/// </summary>
internal sealed class SessionMarker(string path, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private DateTimeOffset _started;
    private int _processId;
    private string _version = "";

    internal string Path { get; } = path;

    /// <summary>Describes a leftover marker from an earlier process, or null when the last session ended cleanly.</summary>
    internal string? DescribePreviousUncleanSession(int currentProcessId, Func<int, bool> isProcessRunning)
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var values = File.ReadAllLines(Path).Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            if (!values.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, CultureInfo.InvariantCulture, out var pid)) return "unreadable marker";
            if (pid == currentProcessId || isProcessRunning(pid)) return null; // not an earlier session
            values.TryGetValue("started", out var started);
            values.TryGetValue("heartbeat", out var heartbeat);
            values.TryGetValue("version", out var version);
            values.TryGetValue("context", out var context);
            return $"pid={pid} version={version} started={started} lastHeartbeat={heartbeat} context=[{context}]";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException)
        {
            Debug.WriteLine($"Session marker unreadable: {exception.Message}");
            return null;
        }
    }

    internal void Begin(int processId, string version)
    {
        _processId = processId;
        _version = version;
        _started = _clock();
        Heartbeat();
    }

    internal void Heartbeat()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllLines(Path,
            [
                $"pid={_processId}", $"version={_version}", $"started={_started:O}", $"heartbeat={_clock():O}",
                $"context={LifecycleLog.DescribeContext().ReplaceLineEndings(" ")}"
            ]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException)
        {
            Debug.WriteLine($"Session marker unavailable: {exception.Message}");
        }
    }

    internal void MarkClean()
    {
        try { File.Delete(Path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine($"Session marker not removed: {exception.Message}");
        }
    }
}
