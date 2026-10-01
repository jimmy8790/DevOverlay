using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DevOverlay.Platform.Windows;

namespace DevOverlay.Metrics.Windows;

internal readonly record struct PresentMonValues(
    double? FramesPerSecond,
    double? FrameTimeMs,
    double? OnePercentLow,
    double? RenderLatencyMs = null,
    ulong SourceQpc = 0);

internal interface IPresentMonClient : IDisposable
{
    bool Connect();
    bool Track(uint processId);
    void StopTracking(uint processId);
    PresentMonValues Poll(uint processId, bool pollSlow, bool pollLatency = true);
}

/// <summary>PresentMon 2.6 aggregates FPS and consumes independent raw-present and latency frame queries.</summary>
internal sealed class PresentMonClient : IPresentMonClient
{
    private const int Success = 0;
    // Values and ABI layout match the official v2.6.0 PresentMonAPI.h; other minor versions are rejected.
    private const int SwapChainAddress = 1;
    private const int PresentedFps = 12;
    private const int PresentedFrameTime = 87;
    private const int RenderPresentLatency = 82;
    private const int PresentStartQpc = 77;
    private const int Average = 1;
    private const int BetweenPresents = 78;
    private const int BetweenDisplayChange = 80;
    private const int DroppedFrames = 16;
    private const int FrameType = 63;
    private const int NewestPoint = 12;
    private const int MaxSwapChains = 16;
    private nint _library;
    private nint _session;
    // pmConsumeFrames consumes a per-process cursor shared by frame queries within a session.
    // Keep raw-present consumption in its own session so it cannot drain LAT's existing frame stream.
    private nint _presentSession;
    private nint _fastQuery;
    private QueryElement[]? _fastElements;
    private byte[]? _fastBuffer;
    private nint _presentQuery;
    private QueryElement[]? _presentElements;
    private byte[]? _presentBuffer;
    // Element indexes resolved from the registered query by metric id (-1 = not registered in this tier).
    private int _displayIndex = -1;
    private int _droppedIndex = -1;
    private int _frameTypeIndex = -1;
    private readonly Dictionary<(uint Pid, ulong SwapChain), PresentedFrameWindow> _presentWindows = [];
    private readonly Dictionary<(uint Pid, ulong SwapChain), PresentedFrameDiagnostics> _frameDiagnostics = [];
#if DEBUG
    private readonly bool _lowTrace = Environment.GetEnvironmentVariable("DEVOVERLAY_LOW_TRACE") == "1";
#else
    private readonly bool _lowTrace;
#endif
    private readonly Dictionary<uint, ulong> _trackingStartedQpc = [];
    // Opt-in per-frame capture (DEVOVERLAY_FRAME_CAPTURE, Debug only). Null in normal runs: no extra columns, no records.
    private readonly FrameCaptureSession? _capture;
    private int[] _captureIndex = [];
    private readonly Dictionary<uint, int> _trackGeneration = [];
    private int _generationCounter;
    private long _pollIndex;
    private nint _latencyQuery;
    private QueryElement[]? _latencyElements;
    private byte[]? _latencyBuffer;
    private uint _latencyPid;
    private bool _latencyRegistrationFailed;
    private bool _latencyFailureReported;
    private readonly RenderLatencyWindow _latencyWindow = new();
    private uint _selectedSwapChainPid;
    private ulong _selectedSwapChain;
    private OpenSession? _openSession;
    private CloseSession? _closeSession;
    private TrackProcess? _startTracking;
    private TrackProcess? _stopTracking;
    private RegisterQuery? _registerQuery;
    private FreeQuery? _freeQuery;
    private PollQuery? _pollQuery;
    private RegisterFrameQuery? _registerFrameQuery;
    private PollQuery? _consumeFrames;
    private FreeQuery? _freeFrameQuery;
    private SetEtwFlushPeriod? _setEtwFlushPeriod;
    internal const uint EtwFlushPeriodMs = 250;
    private bool _frameDeliveryConfigured;
    private readonly bool _trace = Environment.GetEnvironmentVariable("DEVOVERLAY_PRESENTMON_TRACE") == "1";
    private readonly Func<ulong> _qpcClock = () => (ulong)Stopwatch.GetTimestamp();

    public PresentMonClient()
    {
        _capture = FrameCaptureSession.FromEnvironment();
        // The per-frame capture supersedes LowTrace; its per-second synchronous report would only add overhead.
        if (_capture is not null) _lowTrace = false;
    }

    // Native-boundary injection keeps ABI/layout tests on the production registration and decode path.
    internal PresentMonClient(RegisterQuery register, PollQuery poll, RegisterFrameQuery registerFrame,
        PollQuery consume, TrackProcess track, FreeQuery free, SetEtwFlushPeriod? setEtwFlushPeriod = null,
        Func<ulong>? qpcClock = null, bool? lowTrace = null, FrameCaptureSession? capture = null)
    {
        _session = 1;
        _presentSession = 2;
        _registerQuery = register;
        _pollQuery = poll;
        _registerFrameQuery = registerFrame;
        _consumeFrames = consume;
        _startTracking = _stopTracking = track;
        _freeQuery = _freeFrameQuery = free;
        _setEtwFlushPeriod = setEtwFlushPeriod;
        _qpcClock = qpcClock ?? _qpcClock;
        _lowTrace = lowTrace ?? _lowTrace;
        _capture = capture;
        if (capture is not null) _lowTrace = false;
    }

    public bool Connect()
    {
        try
        {
            if (_session != 0)
            {
                ConfigureFrameDelivery();
                return true;
            }
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Intel", "PresentMonSharedService", "PresentMonAPI2.dll");
            if (!File.Exists(path)) return false;
            var version = FileVersionInfo.GetVersionInfo(path);
            if (version.FileMajorPart != 2 || version.FileMinorPart != 6 || version.FileBuildPart != 0)
                return false;
            _library = NativeLibrary.Load(path);
            _openSession = Load<OpenSession>("pmOpenSession");
            _closeSession = Load<CloseSession>("pmCloseSession");
            _startTracking = Load<TrackProcess>("pmStartTrackingProcess");
            _stopTracking = Load<TrackProcess>("pmStopTrackingProcess");
            _registerQuery = Load<RegisterQuery>("pmRegisterDynamicQuery");
            _freeQuery = Load<FreeQuery>("pmFreeDynamicQuery");
            _pollQuery = Load<PollQuery>("pmPollDynamicQuery");
            _registerFrameQuery = Load<RegisterFrameQuery>("pmRegisterFrameQuery");
            _consumeFrames = Load<PollQuery>("pmConsumeFrames");
            _freeFrameQuery = Load<FreeQuery>("pmFreeFrameQuery");
            _setEtwFlushPeriod = Load<SetEtwFlushPeriod>("pmSetEtwFlushPeriod");
            var status = _openSession(out _session);
            RuntimeDiagnostics.Write($"[PresentMon] NativeOpenSession={status} Handle={_session}");
            if (status != Success || _session == 0) throw new InvalidOperationException("PresentMon session unavailable.");
            ConfigureFrameDelivery();
            status = _openSession(out _presentSession);
            if (status != Success || _presentSession == 0) throw new InvalidOperationException("PresentMon presented-frame session unavailable.");
#if DEBUG
            LogMetricMetadata();
#endif
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or InvalidOperationException or FileLoadException)
        {
            RuntimeDiagnostics.Write($"[PresentMon] Connect failure: {exception}");
            Dispose();
            return false;
        }
    }

    private void ConfigureFrameDelivery()
    {
        if (_frameDeliveryConfigured || _setEtwFlushPeriod is null) return;
        // Default ETW batching can deliver advancing live presents already older than our stale limit.
        // Request bounded delivery latency, not a longer freshness timeout or a polling-time marker.
        var status = _setEtwFlushPeriod(_session, EtwFlushPeriodMs);
        RuntimeDiagnostics.Write($"[PresentMon] EtwFlushPeriodMs={EtwFlushPeriodMs} Status={status}");
        if (status != Success) throw new InvalidOperationException($"PresentMon ETW flush configuration failed: {status}.");
        _frameDeliveryConfigured = true;
    }

    public bool Track(uint processId)
    {
        var startedQpc = _qpcClock();
        if (_session == 0 || _startTracking!(_session, processId) != Success) return false;
        if (_startTracking(_presentSession, processId) != Success)
        {
            _stopTracking!(_session, processId);
            return false;
        }
        EnsureQueries();
        _trackingStartedQpc[processId] = startedQpc;
        _trackGeneration[processId] = ++_generationCounter;
        if (_trace) RuntimeDiagnostics.Write($"[PresentTrace] Tracking Pid={processId} Session={_session} RawSession={_presentSession} Cutoff={startedQpc}");
        return true;
    }

    private void EnsureQueries()
    {
        if (_fastQuery != 0 && _presentQuery != 0) return;
        // The API fills in offsets and sizes once. Both result arrays are reused for all polls.
        _fastElements = [new(SwapChainAddress, NewestPoint), new(PresentedFps, Average),
            new(PresentedFrameTime, Average)];
        var fastStatus = _registerQuery!(_session, out _fastQuery, _fastElements, (ulong)_fastElements.Length, 2000, 0);
        RuntimeDiagnostics.Write($"[PresentMon] FastQuery={fastStatus} {string.Join(';', _fastElements.Select(element => $"metric={element.Metric} stat={element.Stat} offset={element.DataOffset} size={element.DataSize}"))}");
        if (fastStatus != Success || _fastQuery == 0) throw new InvalidOperationException($"PresentMon fast query unavailable: {fastStatus}.");
        // Production: Present rows (admission/freshness) plus their display-change interval (1% Low samples).
        // Diagnostics only append columns; the rows and admission rule stay the same. Tiers fall back step by step.
        // PresentOnly is the last resort if the display column is ever rejected: FPS/FT/freshness keep working and
        // 1% Low becomes unavailable rather than silently switching back to Present intervals.
        var presentElements = new QueryElement[] { new(SwapChainAddress, 0), new(PresentStartQpc, 0), new(BetweenPresents, 0) };
        var baseElements = presentElements.Append(new QueryElement(BetweenDisplayChange, 0)).ToArray();
        QueryElement[] WithBase(IEnumerable<int> metrics) =>
            baseElements.Concat(metrics.Where(metric => baseElements.All(element => element.Metric != metric))
                .Select(metric => new QueryElement(metric, 0))).ToArray();
        var tiers = new List<(string Name, QueryElement[] Elements)>();
        if (_capture is not null)
        {
            tiers.Add(("Full", WithBase(FrameCaptureSchema.Columns.Select(column => column.Metric))));
            tiers.Add(("Core", WithBase(FrameCaptureSchema.CoreMetrics)));
        }
        else if (_lowTrace)
            tiers.Add(("LowTrace", WithBase([DroppedFrames, FrameType])));
        tiers.Add(("Base", baseElements));
        tiers.Add(("PresentOnly", presentElements));
        var frameStatus = -1;
        uint frameStride = 0;
        var tierName = "";
        foreach (var (name, template) in tiers)
        {
            var elements = template.ToArray(); // registration writes offsets into the array
            frameStatus = _registerFrameQuery!(_presentSession, out _presentQuery, elements, (ulong)elements.Length, out frameStride);
            if (frameStatus == Success && _presentQuery != 0 && frameStride != 0 && frameStride <= 4096)
            {
                _presentElements = elements;
                tierName = name;
                break;
            }
            if (_presentQuery != 0) _freeFrameQuery?.Invoke(_presentQuery);
            _presentQuery = 0;
            if (tiers.Count > 1) RuntimeDiagnostics.Write($"[PresentMon] PresentedFrameQuery tier {name} rejected: {frameStatus} Stride={frameStride}");
        }
        if (_presentQuery == 0 || _presentElements is null)
            throw new InvalidOperationException($"PresentMon presented-frame query unavailable: {frameStatus}.");
        _presentBuffer = new byte[checked((int)frameStride * 1024)];
        _displayIndex = IndexOf(_presentElements, BetweenDisplayChange);
        _droppedIndex = IndexOf(_presentElements, DroppedFrames);
        _frameTypeIndex = IndexOf(_presentElements, FrameType);
        RuntimeDiagnostics.Write($"[PresentMon] PresentedFrameQuery={frameStatus} Stride={frameStride} Tier={tierName}");
        if (_displayIndex < 0) RuntimeDiagnostics.Write("[PresentMon] PM_METRIC_BETWEEN_DISPLAY_CHANGE unavailable; 1% Low disabled");
        if (_capture is not null)
        {
            var registered = _presentElements;
            _captureIndex = FrameCaptureSchema.Columns.Select(column => Array.FindIndex(registered, element => element.Metric == column.Metric)).ToArray();
            _capture.SetQuery(tierName, registered.Select(element => new FrameCaptureColumnInfo(MetricSymbol(element.Metric), element.Metric, element.DataSize)).ToArray());
            RuntimeDiagnostics.Write($"[FrameCapture] Query Tier={tierName} {string.Join(';', registered.Select(element => $"metric={element.Metric} offset={element.DataOffset} size={element.DataSize}"))}");
        }
        _fastBuffer = new byte[GetStride(_fastElements) * MaxSwapChains];
    }

    public void StopTracking(uint processId)
    {
        if (_session != 0) _stopTracking!(_session, processId);
        if (_presentSession != 0) _stopTracking!(_presentSession, processId);
        if (_selectedSwapChainPid == processId)
        {
            _selectedSwapChainPid = 0;
            _selectedSwapChain = 0;
        }
        if (_latencyPid == processId) ResetLatency();
        if (_capture is not null)
        {
            var now = _qpcClock();
            var primaryWindow = _presentWindows.Where(pair => pair.Key.Pid == processId && pair.Key.SwapChain == _captureChain)
                .Select(pair => pair.Value).FirstOrDefault();
            _capture.TargetStopped(processId, now, primaryWindow?.Calculate(now));
        }
        foreach (var key in _presentWindows.Keys.Where(key => key.Pid == processId).ToArray()) _presentWindows.Remove(key);
        foreach (var key in _frameDiagnostics.Keys.Where(key => key.Pid == processId).ToArray()) _frameDiagnostics.Remove(key);
        _trackingStartedQpc.Remove(processId);
    }

    public PresentMonValues Poll(uint processId, bool pollSlow, bool pollLatency = true)
    {
        if (_session == 0 || _fastBuffer is null || _fastElements is null) return default;
        _pollIndex++;
        FrameCaptureActivity.Mark("PresentMonPoll");
        uint count = MaxSwapChains;
        var status = _pollQuery!(_fastQuery, processId, _fastBuffer, ref count);
        if (_trace && (status != Success || count == 0)) RuntimeDiagnostics.Write($"[PresentTrace] FastPoll Pid={processId} Status={status} Rows={count}");
        if (status != Success) throw new InvalidOperationException($"PresentMon fast poll failed: {status}.");
        if (count == 0 || count > MaxSwapChains) return default;
        var stride = GetStride(_fastElements);
        double bestFps = 0;
        double? frameTime = null;
        ulong selectedAddress = 0;
        for (var index = 0; index < count; index++)
        {
            var offset = index * stride;
            var fps = ReadDouble(_fastBuffer, offset, _fastElements[1]);
            if (!IsPositiveFinite(fps) || fps <= bestFps) continue;
            bestFps = fps;
            frameTime = ReadDouble(_fastBuffer, offset, _fastElements[2]);
            selectedAddress = ReadUInt64(_fastBuffer, offset, _fastElements[0]);
        }
        if (bestFps <= 0) return default;
        _selectedSwapChainPid = processId;
        _selectedSwapChain = selectedAddress;

        var window = ConsumePresentedFrames(processId, selectedAddress);
        var now = _qpcClock();
        if (_capture is not null) AdvanceCapture(processId, selectedAddress, window, now);
        var sourceQpc = window?.LastQpc ?? 0;
        var latestQpc = sourceQpc;
        // ETW delivery time is not Present time. An advancing, validated source QPC proves activity
        // even if a buffered batch arrives >1.5s later. The provider expires a nonadvancing marker;
        // returning this same cached QPC can never refresh its freshness timer.
        if (_trace) RuntimeDiagnostics.Write($"[PresentTrace] Pid={processId} Rows={count} Chain={selectedAddress} FPS={bestFps:F2} FT={frameTime:F3} LatestQpc={latestQpc} Source={sourceQpc} AgeMs={(latestQpc > 0 ? (now - latestQpc) * 1000d / Stopwatch.Frequency : double.NaN):F1}");
        var low = pollSlow && sourceQpc != 0 ? window?.Calculate(now) : null;
        if (_lowTrace && pollSlow && _frameDiagnostics.TryGetValue((processId, selectedAddress), out var diagnostics))
            RuntimeDiagnostics.Write($"[LowTrace] Pid={processId} Chain={selectedAddress} FPS={bestFps:F2} FT={frameTime:F3} ProductionLow={low:F2} {diagnostics.Report(now)}");
        return new PresentMonValues(bestFps, IsPositiveFinite(frameTime) ? frameTime : null,
            IsPositiveFinite(low) ? low : null,
            pollLatency ? PollRenderLatency(processId, selectedAddress) : null,
            sourceQpc);
    }

    private PresentedFrameWindow? ConsumePresentedFrames(uint pid, ulong selectedAddress)
    {
        var now = _qpcClock();
        var stride = _presentBuffer!.Length / 1024;
        var recording = _capture?.IsRecording == true;
        var generation = recording ? _trackGeneration.GetValueOrDefault(pid) : 0;
        for (var batch = 0; batch < 4; batch++)
        {
            uint count = 1024;
            var status = _consumeFrames!(_presentQuery, pid, _presentBuffer, ref count);
            if (status != Success || count > 1024) throw new InvalidOperationException($"PresentMon frame consume failed: {status}.");
            now = _qpcClock();
            if (_trace && batch == 0) RuntimeDiagnostics.Write($"[PresentTrace] Raw Pid={pid} Count={count} Cutoff={_trackingStartedQpc.GetValueOrDefault(pid)} Now={now} FirstChain={(count > 0 ? ReadUInt64(_presentBuffer, 0, _presentElements![0]) : 0)} FirstQpc={(count > 0 ? ReadUInt64(_presentBuffer, 0, _presentElements![1]) : 0)} FirstMs={(count > 0 ? ReadDouble(_presentBuffer, 0, _presentElements![2]) : double.NaN)}");
            for (var index = 0; index < count; index++)
            {
                var offset = index * stride;
                var address = ReadUInt64(_presentBuffer, offset, _presentElements![0]);
                var qpc = ReadUInt64(_presentBuffer, offset, _presentElements[1]);
                var ms = ReadDouble(_presentBuffer, offset, _presentElements[2]);
                var displayMs = _displayIndex < 0 ? double.NaN : ReadDouble(_presentBuffer, offset, _presentElements[_displayIndex]);
                if (qpc < _trackingStartedQpc.GetValueOrDefault(pid))
                {
                    if (recording) CaptureRow(pid, generation, address, selectedAddress, qpc, ms, FrameAdmission.BeforeTrackingCutoff, offset, now);
                    continue;
                }
                var key = (pid, address);
                if (!_presentWindows.TryGetValue(key, out var window)) _presentWindows[key] = window = new PresentedFrameWindow();
                var admission = window.Admit(qpc, ms, displayMs, now);
                var accepted = admission == FrameAdmission.Accepted;
                if (recording) CaptureRow(pid, generation, address, selectedAddress, qpc, ms, admission, offset, now);
                if (_lowTrace && _droppedIndex >= 0 && _frameTypeIndex >= 0)
                {
                    if (!_frameDiagnostics.TryGetValue(key, out var diagnostics)) _frameDiagnostics[key] = diagnostics = new();
                    var droppedElement = _presentElements[_droppedIndex];
                    bool? dropped = droppedElement.DataSize == 1 ? _presentBuffer[offset + (int)droppedElement.DataOffset] != 0 : null;
                    var typeElement = _presentElements[_frameTypeIndex];
                    int? frameType = typeElement.DataSize == 4 ? BitConverter.ToInt32(_presentBuffer, offset + (int)typeElement.DataOffset) : null;
                    diagnostics.Observe(qpc, ms, accepted, dropped, frameType);
                }
            }
            if (count < 1024) break;
        }
        return _presentWindows.GetValueOrDefault((pid, selectedAddress));
    }

    private ulong _captureChain;

    private void AdvanceCapture(uint pid, ulong selectedAddress, PresentedFrameWindow? window, ulong now)
    {
        var capture = _capture!;
        capture.Observe(now, pid, window is not null);
        if (pid != capture.PrimaryPid) return;
        _captureChain = selectedAddress;
        if (capture.IsDue(now)) capture.Complete(now, "DurationElapsed", window?.Calculate(now));
    }

    /// <summary>Copies one already-decoded production row plus the diagnostic columns into the bounded capture buffer.</summary>
    private void CaptureRow(uint pid, int generation, ulong address, ulong selectedAddress, ulong qpc, double ms,
        FrameAdmission admission, int offset, ulong pollQpc)
    {
        var buffer = _presentBuffer!;
        var elements = _presentElements!;
        double Real(CaptureColumn column)
        {
            var index = (int)column < _captureIndex.Length ? _captureIndex[(int)column] : -1;
            return index < 0 ? double.NaN : ReadDouble(buffer, offset, elements[index]);
        }
        long Integer(CaptureColumn column, bool unsigned)
        {
            var index = (int)column < _captureIndex.Length ? _captureIndex[(int)column] : -1;
            if (index < 0) return -1;
            var element = elements[index];
            var at = offset + (int)element.DataOffset;
            if (at < 0 || at + (int)element.DataSize > buffer.Length) return -1;
            return element.DataSize switch
            {
                1 => buffer[at],
                4 => unsigned ? (long)BitConverter.ToUInt32(buffer, at) : BitConverter.ToInt32(buffer, at),
                _ => -1
            };
        }
        sbyte Flag(CaptureColumn column) => (sbyte)(Integer(column, false) switch { < 0 => -1, 0 => 0, _ => 1 });
        int Signed(CaptureColumn column) => (int)Math.Clamp(Integer(column, false), int.MinValue, int.MaxValue);
        var cpuIndex = _captureIndex.Length > (int)CaptureColumn.CpuStartQpc ? _captureIndex[(int)CaptureColumn.CpuStartQpc] : -1;
        _capture!.Record(new FrameCaptureRecord(_pollIndex, pollQpc, pid, generation, address, address == selectedAddress, qpc, ms, admission,
            Real(CaptureColumn.BetweenDisplayChange), Real(CaptureColumn.UntilDisplayed), Real(CaptureColumn.DisplayedTime),
            Real(CaptureColumn.BetweenAppStart), Flag(CaptureColumn.Dropped), Signed(CaptureColumn.FrameType),
            Signed(CaptureColumn.PresentMode), Signed(CaptureColumn.PresentRuntime), Signed(CaptureColumn.SyncInterval),
            Integer(CaptureColumn.PresentFlags, true), Flag(CaptureColumn.AllowsTearing),
            cpuIndex < 0 ? 0 : ReadUInt64(buffer, offset, elements[cpuIndex])));
    }

    private static int IndexOf(QueryElement[] elements, int metric) => Array.FindIndex(elements, element => element.Metric == metric);

    private static string MetricSymbol(int metric) => metric switch
    {
        SwapChainAddress => "PM_METRIC_SWAP_CHAIN_ADDRESS",
        PresentStartQpc => "PM_METRIC_PRESENT_START_QPC",
        BetweenPresents => "PM_METRIC_BETWEEN_PRESENTS",
        _ => FrameCaptureSchema.Columns.FirstOrDefault(column => column.Metric == metric).Symbol ?? metric.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private double? PollRenderLatency(uint processId, ulong swapChain)
    {
        // v2.6 metadata marks metric 82 as FRAME_EVENT, polledType=VOID. A dynamic AVG
        // rejects the entire FPS query (QUERY_MALFORMED). Keep this optional query isolated.
        if (_latencyRegistrationFailed) return null;
        if (_latencyQuery == 0)
        {
            _latencyElements = [new(SwapChainAddress, 0), new(PresentStartQpc, 0), new(RenderPresentLatency, 0)];
            var status = _registerFrameQuery!(_session, out _latencyQuery, _latencyElements, 3, out var stride);
            RuntimeDiagnostics.Write($"[PresentMon] LatencyFrameQuery={status} Stride={stride} {string.Join(';', _latencyElements.Select(element => $"metric={element.Metric} offset={element.DataOffset} size={element.DataSize}"))}");
            if (status != Success || _latencyQuery == 0 || stride == 0 || stride > 4096)
            {
                _latencyRegistrationFailed = true;
                return null;
            }
            _latencyBuffer = new byte[checked((int)stride * 1024)];
        }
        if (_latencyPid != processId)
        {
            ResetLatency();
            _latencyPid = processId;
        }
        var now = _qpcClock();
        var frameStride = _latencyBuffer!.Length / 1024;
        // At most four reused batches per existing 250ms tick; no per-frame object/UI work.
        for (var batch = 0; batch < 4; batch++)
        {
            uint count = 1024;
            var status = _consumeFrames!(_latencyQuery, processId, _latencyBuffer, ref count);
            if (status != Success || count > 1024)
            {
                _latencyWindow.Clear();
                if (!_latencyFailureReported) RuntimeDiagnostics.Write($"[PresentMon] LAT consume unavailable: {status}");
                _latencyFailureReported = true;
                return null;
            }
            _latencyFailureReported = false;
            for (var index = 0; index < count; index++)
            {
                var offset = index * frameStride;
                _latencyWindow.Add(ReadUInt64(_latencyBuffer, offset, _latencyElements![1]),
                    ReadDouble(_latencyBuffer, offset, _latencyElements[2]), now,
                    ReadUInt64(_latencyBuffer, offset, _latencyElements[0]));
            }
            if (count < 1024) break;
        }
        return _latencyWindow.Mean(now, swapChain);
    }

    private void ResetLatency()
    {
        _latencyWindow.Clear();
        _latencyPid = 0;
    }

    public void Dispose()
    {
        _capture?.ClientDisposed(_qpcClock());
        if (_fastQuery != 0) _freeQuery?.Invoke(_fastQuery);
        if (_latencyQuery != 0) _freeFrameQuery?.Invoke(_latencyQuery);
        if (_presentQuery != 0) _freeFrameQuery?.Invoke(_presentQuery);
        _presentQuery = 0;
        _presentBuffer = null;
        _presentElements = null;
        _displayIndex = _droppedIndex = _frameTypeIndex = -1;
        _presentWindows.Clear();
        _frameDiagnostics.Clear();
        _trackingStartedQpc.Clear();
        if (_session != 0) _closeSession?.Invoke(_session);
        if (_presentSession != 0) _closeSession?.Invoke(_presentSession);
        _presentSession = 0;
        _fastQuery = _session = 0;
        _latencyQuery = 0;
        _latencyBuffer = null;
        _latencyElements = null;
        _latencyRegistrationFailed = false;
        ResetLatency();
        if (_library != 0) NativeLibrary.Free(_library);
        _library = 0;
        _fastBuffer = null;
        _fastElements = null;
        _openSession = null;
        _closeSession = null;
        _startTracking = _stopTracking = null;
        _registerQuery = null;
        _freeQuery = null;
        _pollQuery = null;
        _registerFrameQuery = null;
        _consumeFrames = null;
        _freeFrameQuery = null;
        _setEtwFlushPeriod = null;
        _frameDeliveryConfigured = false;
        _selectedSwapChainPid = 0;
        _selectedSwapChain = 0;
    }

    private T Load<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

#if DEBUG
    private void LogMetricMetadata()
    {
        if (Load<GetIntrospection>("pmGetIntrospectionRoot")(_session, out var root) != 0 || root == 0) return;
        try
        {
            static IEnumerable<nint> Objects(nint array)
            {
                var data = Marshal.ReadIntPtr(array);
                var count = (int)Marshal.ReadIntPtr(array, IntPtr.Size);
                for (var index = 0; index < count; index++) yield return Marshal.ReadIntPtr(data, index * IntPtr.Size);
            }
            foreach (var enumeration in Objects(Marshal.ReadIntPtr(root, IntPtr.Size)))
            {
                if (Marshal.ReadInt32(enumeration) != 1) continue;
                foreach (var key in Objects(Marshal.ReadIntPtr(enumeration, 24)))
                {
                    var symbol = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(Marshal.ReadIntPtr(key, 8)));
                    if (symbol is not ("PM_METRIC_SWAP_CHAIN_ADDRESS" or "PM_METRIC_PRESENTED_FPS" or
                        "PM_METRIC_PRESENTED_FRAME_TIME" or "PM_METRIC_RENDER_PRESENT_LATENCY")) continue;
                    var id = Marshal.ReadInt32(key, 4);
                    var metric = Objects(Marshal.ReadIntPtr(root)).First(value => Marshal.ReadInt32(value) == id);
                    var type = Marshal.ReadIntPtr(metric, 16);
                    var stats = Objects(Marshal.ReadIntPtr(metric, 24)).Select(value => Marshal.ReadInt32(value));
                    RuntimeDiagnostics.Write($"[Metadata] {symbol} Id={id} Type={Marshal.ReadInt32(metric, 4)} Unit={Marshal.ReadInt32(metric, 8)} PolledType={Marshal.ReadInt32(type)} Stats={string.Join(',', stats)}");
                }
            }
        }
        finally { Load<FreeQuery>("pmFreeIntrospectionRoot")(root); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetIntrospection(nint session, out nint root);
#endif

    private static int GetStride(QueryElement[] elements) =>
        checked((int)elements.Max(element => element.DataOffset + element.DataSize));

    private static double ReadDouble(byte[] buffer, int baseOffset, QueryElement element) =>
        element.DataSize == sizeof(double) && baseOffset + (int)element.DataOffset + sizeof(double) <= buffer.Length
            ? BitConverter.ToDouble(buffer, baseOffset + (int)element.DataOffset) : double.NaN;

    private static ulong ReadUInt64(byte[] buffer, int baseOffset, QueryElement element) =>
        element.DataSize == sizeof(ulong) && baseOffset + (int)element.DataOffset + sizeof(ulong) <= buffer.Length
            ? BitConverter.ToUInt64(buffer, baseOffset + (int)element.DataOffset) : 0;

    private static bool IsPositiveFinite(double? value) => value is > 0 && double.IsFinite(value.Value);

    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryElement(int metric, int stat)
    {
        public int Metric = metric;
        public int Stat = stat;
        public uint DeviceId;
        public uint ArrayIndex;
        public ulong DataOffset;
        public ulong DataSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenSession(out nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseSession(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SetEtwFlushPeriod(nint session, uint periodMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int TrackProcess(nint handle, uint processId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int RegisterQuery(nint session, out nint query,
        [In, Out] QueryElement[] elements, ulong count, double windowMs, double offsetMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FreeQuery(nint query);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int PollQuery(nint query, uint processId,
        [Out] byte[] buffer, ref uint swapChains);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int RegisterFrameQuery(nint session, out nint query,
        [In, Out] QueryElement[] elements, ulong count, out uint blobSize);
}
