using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using DevOverlay.Platform.Windows;

namespace DevOverlay.Metrics.Windows;

/// <summary>
/// Development-only, opt-in per-frame capture for the 1% Low investigation. Disabled unless
/// <c>DEVOVERLAY_FRAME_CAPTURE</c> is set in a Debug build. Never changes the production 1% population or formula.
/// </summary>
internal sealed record FrameCaptureOptions(
    TimeSpan Duration,
    TimeSpan StartDelay,
    string? ProcessFilter,
    string OutputDirectory,
    int MaxRecords = FrameCaptureOptions.DefaultMaxRecords)
{
    internal const string EnableVariable = "DEVOVERLAY_FRAME_CAPTURE";
    internal const string DelayVariable = "DEVOVERLAY_FRAME_CAPTURE_DELAY";
    internal const string ProcessVariable = "DEVOVERLAY_FRAME_CAPTURE_PROCESS";
    internal const int DefaultMaxRecords = 400_000;
    internal static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(60);
    // Default delay lets target acquisition and the production 1% window settle, so the end-of-capture value is comparable.
    internal static readonly TimeSpan DefaultStartDelay = TimeSpan.FromSeconds(15);

    internal static string DefaultOutputDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevOverlay", "Diagnostics");

    /// <summary>"1"/"on"/"true" = 60 s; a number of seconds (10–600) selects the duration; anything else = disabled.</summary>
    internal static FrameCaptureOptions? Parse(string? enable, string? delay, string? process, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(enable)) return null;
        var text = enable.Trim();
        TimeSpan duration;
        if (text is "1" || text.Equals("on", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase))
            duration = DefaultDuration;
        else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 10 && seconds <= 600)
            duration = TimeSpan.FromSeconds(seconds);
        else
            return null;
        var startDelay = int.TryParse(delay?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var delaySeconds) &&
            delaySeconds >= 0 && delaySeconds <= 600 ? TimeSpan.FromSeconds(delaySeconds) : DefaultStartDelay;
        var filter = string.IsNullOrWhiteSpace(process) ? null : Path.GetFileNameWithoutExtension(process.Trim());
        return new FrameCaptureOptions(duration, startDelay, filter, outputDirectory);
    }

    internal static FrameCaptureOptions? FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(EnableVariable),
        Environment.GetEnvironmentVariable(DelayVariable),
        Environment.GetEnvironmentVariable(ProcessVariable),
        DefaultOutputDirectory);

    internal static bool IsRequested =>
#if DEBUG
        FromEnvironment() is not null;
#else
        false;
#endif
}

internal enum CaptureColumn
{
    Dropped,
    FrameType,
    BetweenDisplayChange,
    UntilDisplayed,
    DisplayedTime,
    SyncInterval,
    PresentFlags,
    PresentMode,
    PresentRuntime,
    AllowsTearing,
    BetweenAppStart,
    CpuStartQpc
}

internal enum CaptureValueKind { Bool, Int32, UInt32, Double, UInt64 }

/// <summary>Extra frame-query columns, PresentMon v2.6.0 <c>PM_METRIC</c> ordinals (PresentMonAPI.h).</summary>
internal static class FrameCaptureSchema
{
    // 2: production 1% Low samples are MsBetweenDisplayChange (AcceptedForOnePercentLow/InSlowest1Pct follow it).
    internal const int Version = 2;

    // The client resolves every column by metric id; columns already in the production query are not registered twice.
    internal static readonly (CaptureColumn Column, int Metric, string Symbol, CaptureValueKind Kind)[] Columns =
    [
        (CaptureColumn.Dropped, 16, "PM_METRIC_DROPPED_FRAMES", CaptureValueKind.Bool),
        (CaptureColumn.FrameType, 63, "PM_METRIC_FRAME_TYPE", CaptureValueKind.Int32),
        (CaptureColumn.BetweenDisplayChange, 80, "PM_METRIC_BETWEEN_DISPLAY_CHANGE", CaptureValueKind.Double),
        (CaptureColumn.UntilDisplayed, 81, "PM_METRIC_UNTIL_DISPLAYED", CaptureValueKind.Double),
        (CaptureColumn.DisplayedTime, 17, "PM_METRIC_DISPLAYED_TIME", CaptureValueKind.Double),
        (CaptureColumn.SyncInterval, 18, "PM_METRIC_SYNC_INTERVAL", CaptureValueKind.Int32),
        (CaptureColumn.PresentFlags, 19, "PM_METRIC_PRESENT_FLAGS", CaptureValueKind.UInt32),
        (CaptureColumn.PresentMode, 20, "PM_METRIC_PRESENT_MODE", CaptureValueKind.Int32),
        (CaptureColumn.PresentRuntime, 21, "PM_METRIC_PRESENT_RUNTIME", CaptureValueKind.Int32),
        (CaptureColumn.AllowsTearing, 22, "PM_METRIC_ALLOWS_TEARING", CaptureValueKind.Bool),
        (CaptureColumn.BetweenAppStart, 86, "PM_METRIC_BETWEEN_APP_START", CaptureValueKind.Double),
        (CaptureColumn.CpuStartQpc, 7, "PM_METRIC_CPU_START_QPC", CaptureValueKind.UInt64)
    ];

    /// <summary>The minimal fallback tier that still answers the displayed-vs-present question.</summary>
    internal static readonly int[] CoreMetrics = [16, 63, 80];

    internal static string FrameTypeName(int value) => value switch
    {
        0 => "NOT_SET", 1 => "UNSPECIFIED", 2 => "APPLICATION", 3 => "REPEATED", 50 => "INTEL_XEFG", 100 => "AMD_AFMF",
        _ => value < 0 ? "UNKNOWN" : $"OTHER_{value}"
    };

    internal static string PresentModeName(int value) => value switch
    {
        0 => "UNKNOWN", 1 => "HARDWARE_LEGACY_FLIP", 2 => "HARDWARE_LEGACY_COPY_TO_FRONT_BUFFER",
        3 => "HARDWARE_INDEPENDENT_FLIP", 4 => "COMPOSED_FLIP", 5 => "COMPOSED_COPY_WITH_GPU_GDI",
        6 => "COMPOSED_COPY_WITH_CPU_GDI", 8 => "HARDWARE_COMPOSED_INDEPENDENT_FLIP",
        _ => value < 0 ? "UNAVAILABLE" : $"OTHER_{value}"
    };

    internal static string RuntimeName(int value) => value switch
    {
        0 => "UNKNOWN", 1 => "DXGI", 2 => "D3D9", _ => value < 0 ? "UNAVAILABLE" : $"OTHER_{value}"
    };
}

/// <summary>One raw Present row as consumed by the production query. Unknown/unexposed values: NaN, -1 or 0 (QPC).</summary>
internal readonly record struct FrameCaptureRecord(
    long PollIndex,
    ulong PollQpc,
    uint Pid,
    int TrackGeneration,
    ulong SwapChain,
    bool SelectedChain,
    ulong PresentQpc,
    double MsBetweenPresents,
    FrameAdmission Admission,
    double MsBetweenDisplayChange = double.NaN,
    double MsUntilDisplayed = double.NaN,
    double MsDisplayedTime = double.NaN,
    double MsBetweenAppStart = double.NaN,
    sbyte Dropped = -1,
    int FrameType = -1,
    int PresentMode = -1,
    int PresentRuntime = -1,
    int SyncInterval = -1,
    long PresentFlags = -1,
    sbyte AllowsTearing = -1,
    ulong CpuStartQpc = 0)
{
    /// <summary>Admitted Present rows of the captured target's selected swap chain (production freshness population).</summary>
    internal bool InPresentPopulation(uint primaryPid) =>
        Pid == primaryPid && SelectedChain && Admission == FrameAdmission.Accepted;

    /// <summary>Exactly the rows that add a sample to the production 1% window: admitted and actually displayed.</summary>
    internal bool InProductionPopulation(uint primaryPid) =>
        InPresentPopulation(primaryPid) && PresentedFrameWindow.IsDisplayInterval(MsBetweenDisplayChange);
}

internal enum FrameCaptureState { Armed, Delaying, Recording, Completed }

internal sealed record FrameCaptureColumnInfo(string Name, int Metric, ulong DataSize);

internal sealed record FrameCaptureResult(
    FrameCaptureOptions Options,
    long QpcFrequency,
    ulong StartQpc,
    ulong EndQpc,
    DateTimeOffset StartWallClock,
    uint PrimaryPid,
    string CompletionReason,
    bool Truncated,
    bool MarksTruncated,
    double? ProductionLowAtEnd,
    string QueryTier,
    IReadOnlyList<FrameCaptureColumnInfo> Columns,
    IReadOnlyDictionary<uint, string> ProcessNames,
    IReadOnlyList<FrameCaptureRecord> Records,
    IReadOnlyList<ActivityMark> Marks);

internal interface IFrameCaptureWriter
{
    /// <summary>Called once, on a thread-pool thread, after the capture completed. Returns the written paths.</summary>
    IReadOnlyList<string> Write(FrameCaptureResult result, FrameCaptureSummary summary);
}

/// <summary>
/// Bounded in-memory capture. The PresentMon polling thread only appends structs to a list; analysis and the single
/// file write happen once, asynchronously, after the capture ends. Writer failures are logged and never propagate.
/// Not thread-safe: all methods except the completion task run on the provider's polling thread.
/// </summary>
internal sealed class FrameCaptureSession
{
#if DEBUG
    // One capture per app run. A PresentMon reconnect creates a new client; it reuses the still-pending session
    // instead of losing it (a client discarded after a failed Connect must not consume the capture).
    private static FrameCaptureSession? s_shared;
#endif
    private readonly IFrameCaptureWriter _writer;
    private readonly Func<uint, string?> _processName;
    private readonly Dictionary<uint, string> _names = [];
    private List<FrameCaptureRecord> _records = new(32768);
    private ulong _startQpc;
    private ulong _endQpc;
    private ulong _recordAfterQpc;
    private DateTimeOffset _startWallClock;
    private bool _truncated;
    private string _queryTier = "NotRegistered";
    private IReadOnlyList<FrameCaptureColumnInfo> _columns = [];

    internal FrameCaptureSession(FrameCaptureOptions options, IFrameCaptureWriter? writer = null, Func<uint, string?>? processName = null)
    {
        Options = options;
        _writer = writer ?? new CsvFrameCaptureWriter();
        _processName = processName ?? DefaultProcessName;
    }

    internal FrameCaptureOptions Options { get; }
    internal FrameCaptureState State { get; private set; } = FrameCaptureState.Armed;
    internal bool IsRecording => State == FrameCaptureState.Recording;
    internal uint PrimaryPid { get; private set; }
    internal int RecordCount => _records.Count;
    internal string QueryTier => _queryTier;
    internal Task? Completion { get; private set; }

    internal static FrameCaptureSession? FromEnvironment()
    {
#if DEBUG
        if (s_shared is { } existing) return existing.State == FrameCaptureState.Completed ? null : existing;
        var options = FrameCaptureOptions.FromEnvironment();
        if (options is null) return null;
        RuntimeDiagnostics.Write($"[FrameCapture] Armed Duration={options.Duration.TotalSeconds:0}s Delay={options.StartDelay.TotalSeconds:0}s Process={options.ProcessFilter ?? "(current target)"} Output={options.OutputDirectory}");
        return s_shared = new FrameCaptureSession(options);
#else
        return null;
#endif
    }

    internal void SetQuery(string tier, IReadOnlyList<FrameCaptureColumnInfo> columns)
    {
        _queryTier = tier;
        _columns = columns;
    }

    /// <summary>Advances Armed → Delaying → Recording for a polled PID that has a selected present stream.</summary>
    internal void Observe(ulong now, uint pid, bool hasSelectedStream)
    {
        if (State == FrameCaptureState.Armed && hasSelectedStream && Matches(pid))
        {
            PrimaryPid = pid;
            _recordAfterQpc = now + (ulong)(Stopwatch.Frequency * Options.StartDelay.TotalSeconds);
            State = FrameCaptureState.Delaying;
            RuntimeDiagnostics.Write($"[FrameCapture] Target Pid={pid} Name={NameOf(pid)} RecordingInSeconds={Options.StartDelay.TotalSeconds:0}");
        }
        if (State == FrameCaptureState.Delaying && pid == PrimaryPid && now >= _recordAfterQpc)
        {
            _startQpc = now;
            _endQpc = now + (ulong)(Stopwatch.Frequency * Options.Duration.TotalSeconds);
            _startWallClock = DateTimeOffset.Now;
            State = FrameCaptureState.Recording;
            FrameCaptureActivity.Begin();
            RuntimeDiagnostics.Write($"[FrameCapture] Recording Pid={pid} StartQpc={now} Seconds={Options.Duration.TotalSeconds:0}");
        }
    }

    internal void Record(in FrameCaptureRecord record)
    {
        // Rows presented after the capture end (consumed in the poll that crosses it) are not part of the capture.
        if (State != FrameCaptureState.Recording || record.PresentQpc > _endQpc) return;
        if (_records.Count >= Options.MaxRecords)
        {
            _truncated = true;
            return;
        }
        if (!_names.ContainsKey(record.Pid)) NameOf(record.Pid);
        _records.Add(record);
    }

    /// <summary>The captured target stopped being tracked (exit, reset, switch): finish early or re-arm.</summary>
    internal void TargetStopped(uint pid, ulong now, double? productionLow)
    {
        if (pid != PrimaryPid || PrimaryPid == 0) return;
        if (State == FrameCaptureState.Recording) Complete(now, IsDue(now) ? "DurationElapsed" : "TargetStopped", productionLow);
        else if (State == FrameCaptureState.Delaying)
        {
            State = FrameCaptureState.Armed;
            PrimaryPid = 0;
            RuntimeDiagnostics.Write($"[FrameCapture] Target Pid={pid} stopped during start delay; re-armed");
        }
    }

    /// <summary>The owning PresentMon client is being disposed (disconnect/reconnect): finish or re-arm for the next client.</summary>
    internal void ClientDisposed(ulong now)
    {
        if (State == FrameCaptureState.Recording) Complete(now, IsDue(now) ? "DurationElapsed" : "ClientDisposed", null);
        else if (State == FrameCaptureState.Delaying) TargetStopped(PrimaryPid, now, null);
    }

    internal bool IsDue(ulong now) => State == FrameCaptureState.Recording && now >= _endQpc;

    /// <summary>Ends the capture once and starts the asynchronous analysis/write. Safe to call in any state.</summary>
    internal Task? Complete(ulong now, string reason, double? productionLowAtEnd)
    {
        if (State == FrameCaptureState.Completed) return Completion;
        var wasRecording = State == FrameCaptureState.Recording;
        State = FrameCaptureState.Completed;
        if (!wasRecording)
        {
            RuntimeDiagnostics.Write($"[FrameCapture] Ended before recording started: {reason}");
            return Completion = Task.CompletedTask;
        }
        var (marks, marksTruncated) = FrameCaptureActivity.End();
        var records = _records;
        _records = [];
        var result = new FrameCaptureResult(Options, Stopwatch.Frequency, _startQpc, Math.Min(now, Math.Max(_endQpc, _startQpc)),
            _startWallClock, PrimaryPid, reason, _truncated, marksTruncated, productionLowAtEnd, _queryTier, _columns,
            new Dictionary<uint, string>(_names), records, marks);
        RuntimeDiagnostics.Write($"[FrameCapture] Completed Reason={reason} Records={records.Count} Marks={marks.Length} Truncated={_truncated}");
        Completion = Task.Run(() => WriteSafely(result));
        return Completion;
    }

    private void WriteSafely(FrameCaptureResult result)
    {
        try
        {
            var summary = FrameCaptureAnalyzer.Analyze(result);
            var paths = _writer.Write(result, summary);
            RuntimeDiagnostics.Write($"[FrameCapture] Written {string.Join(" | ", paths)} N={summary.Production?.Count} Low={summary.Production?.LowFps:F2}");
        }
        catch (Exception exception)
        {
            // Diagnostics must never affect telemetry; the failure itself stays visible in the runtime log.
            RuntimeDiagnostics.Write($"[FrameCapture] Write failed: {exception}");
            Debug.WriteLine($"Frame capture write failed: {exception.Message}");
        }
    }

    private bool Matches(uint pid) =>
        Options.ProcessFilter is null || string.Equals(NameOf(pid), Options.ProcessFilter, StringComparison.OrdinalIgnoreCase);

    private string NameOf(uint pid)
    {
        if (_names.TryGetValue(pid, out var name)) return name;
        name = _processName(pid) ?? "?";
        _names[pid] = name;
        return name;
    }

    private static string? DefaultProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OverflowException)
        {
            return null;
        }
    }
}

/// <summary>One CSV (header comments + frame rows + activity rows) and one human-readable summary per capture.</summary>
internal sealed class CsvFrameCaptureWriter : IFrameCaptureWriter
{
    internal const string Header =
        "RecordType,PollIndex,PollQpc,CaptureMs,WallClock,Pid,ProcessName,TrackGeneration,SwapChain,SelectedChain," +
        "PresentQpc,MsBetweenPresents,Admission,AcceptedForOnePercentLow,InSlowest1Pct,MsBetweenDisplayChange," +
        "MsUntilDisplayed,MsDisplayedTime,MsBetweenAppStart,Dropped,FrameType,PresentMode,PresentRuntime," +
        "SyncInterval,PresentFlags,AllowsTearing,CpuStartQpc,MarkSource";

    public IReadOnlyList<string> Write(FrameCaptureResult result, FrameCaptureSummary summary)
    {
        Directory.CreateDirectory(result.Options.OutputDirectory);
        var stem = Path.Combine(result.Options.OutputDirectory,
            $"frame-capture-{result.StartWallClock:yyyyMMdd-HHmmss}-pid{result.PrimaryPid}");
        var csvPath = stem + ".csv";
        var summaryPath = stem + ".summary.txt";
        using (var writer = new StreamWriter(csvPath, false, new UTF8Encoding(false), 1 << 16))
            WriteCsv(writer, result, summary);
        File.WriteAllText(summaryPath, summary.Text, new UTF8Encoding(false));
        return [csvPath, summaryPath];
    }

    internal static void WriteCsv(TextWriter writer, FrameCaptureResult result, FrameCaptureSummary summary)
    {
        var c = CultureInfo.InvariantCulture;
        writer.WriteLine($"# DevOverlayFrameCapture SchemaVersion={FrameCaptureSchema.Version}");
        writer.WriteLine($"# Source=PresentMon v2.6.0 frame query (production raw-present session); ProductionMetric=PM_METRIC_BETWEEN_DISPLAY_CHANGE(80) AdmissionMetric=PM_METRIC_BETWEEN_PRESENTS(78)");
        writer.WriteLine(string.Create(c, $"# QpcFrequency={result.QpcFrequency} StartQpc={result.StartQpc} EndQpc={result.EndQpc} StartLocal={result.StartWallClock:O}"));
        writer.WriteLine(string.Create(c, $"# DurationSeconds={result.Options.Duration.TotalSeconds:0} DelaySeconds={result.Options.StartDelay.TotalSeconds:0} ProcessFilter={result.Options.ProcessFilter ?? "(none)"} PrimaryPid={result.PrimaryPid} CompletionReason={result.CompletionReason}"));
        writer.WriteLine(string.Create(c, $"# Truncated={result.Truncated} MarksTruncated={result.MarksTruncated} MaxRecords={result.Options.MaxRecords} Records={result.Records.Count} Marks={result.Marks.Count}"));
        writer.WriteLine(string.Create(c, $"# ProductionLowAtEnd={Format(result.ProductionLowAtEnd)} (PresentedFrameWindow.Calculate at capture end, {PresentedFrameWindow.OnePercentLowWindow.TotalSeconds:0.#} s window)"));
        writer.WriteLine($"# QueryTier={result.QueryTier} Columns={string.Join(';', result.Columns.Select(column => string.Create(c, $"{column.Name}:metric={column.Metric}:size={column.DataSize}")))}");
        foreach (var (pid, name) in result.ProcessNames.OrderBy(pair => pair.Key))
            writer.WriteLine(string.Create(c, $"# Process Pid={pid} Name={name}"));
        writer.WriteLine("# Unknown/unexposed values are empty cells. Dropped/AllowsTearing: 1/0. Admission: Accepted|ZeroQpc|DuplicateQpc|NonAdvancingQpc|FutureQpc|TooOld|InvalidInterval|BeforeTrackingCutoff");
        writer.WriteLine("# AcceptedForOnePercentLow: admitted Present of PrimaryPid's selected swap chain with a finite, positive MsBetweenDisplayChange (a production 1% sample). InSlowest1Pct: one of the ceil(N*0.01) largest MsBetweenDisplayChange of those rows over the whole capture.");
        writer.WriteLine("# FrameType: 0=NOT_SET 1=UNSPECIFIED 2=APPLICATION 3=REPEATED 50=INTEL_XEFG 100=AMD_AFMF. PresentMode: 0=UNKNOWN 1=HW_LEGACY_FLIP 2=HW_LEGACY_COPY 3=HW_INDEPENDENT_FLIP 4=COMPOSED_FLIP 5=COMPOSED_COPY_GPU_GDI 6=COMPOSED_COPY_CPU_GDI 8=HW_COMPOSED_INDEPENDENT_FLIP. PresentRuntime: 0=UNKNOWN 1=DXGI 2=D3D9");
        writer.WriteLine(Header);
        var slowest = summary.SlowestKeys;
        var frequency = (double)result.QpcFrequency;
        foreach (var record in result.Records)
        {
            var captureMs = (record.PresentQpc - (double)result.StartQpc) * 1000 / frequency;
            var accepted = record.InProductionPopulation(result.PrimaryPid);
            var inSlowest = accepted && slowest.Contains((record.Pid, record.SwapChain, record.PresentQpc));
            result.ProcessNames.TryGetValue(record.Pid, out var name);
            writer.Write("frame,");
            writer.Write(string.Create(c, $"{record.PollIndex},{record.PollQpc},{captureMs:F4},{result.StartWallClock.AddMilliseconds(captureMs):O},{record.Pid},{Csv(name)},{record.TrackGeneration},{record.SwapChain},{(record.SelectedChain ? 1 : 0)},"));
            writer.Write(string.Create(c, $"{record.PresentQpc},{Format(record.MsBetweenPresents)},{record.Admission},{(accepted ? 1 : 0)},{(inSlowest ? 1 : 0)},{Format(record.MsBetweenDisplayChange)},"));
            writer.Write(string.Create(c, $"{Format(record.MsUntilDisplayed)},{Format(record.MsDisplayedTime)},{Format(record.MsBetweenAppStart)},{Int(record.Dropped)},{Int(record.FrameType)},{Int(record.PresentMode)},{Int(record.PresentRuntime)},"));
            writer.WriteLine(string.Create(c, $"{Int(record.SyncInterval)},{(record.PresentFlags < 0 ? "" : record.PresentFlags.ToString(c))},{Int(record.AllowsTearing)},{(record.CpuStartQpc == 0 ? "" : record.CpuStartQpc.ToString(c))},"));
        }
        foreach (var mark in result.Marks)
        {
            var captureMs = (mark.Qpc - (double)result.StartQpc) * 1000 / frequency;
            writer.WriteLine(string.Create(c, $"mark,,,{captureMs:F4},{result.StartWallClock.AddMilliseconds(captureMs):O},,,,,,{mark.Qpc},,,,,,,,,,,,,,,,,{Csv(mark.Source)}"));
        }
    }

    private static string Format(double? value) =>
        value is { } number && double.IsFinite(number) ? number.ToString("0.######", CultureInfo.InvariantCulture) : "";

    private static string Int(long value) => value < 0 ? "" : value.ToString(CultureInfo.InvariantCulture);

    private static string Csv(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.IndexOfAny([',', '"', '\n', '\r']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
}
