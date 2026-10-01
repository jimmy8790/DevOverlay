using System.Diagnostics;
using System.IO;
using DevOverlay.Metrics;
using DevOverlay.Metrics.Windows;
using Xunit;

namespace DevOverlay.Tests;

/// <summary>Opt-in per-frame capture for the 1% Low investigation; production 1% must stay unchanged.</summary>
public sealed class FrameCaptureTests
{
    private static readonly ulong Second = (ulong)Stopwatch.Frequency;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("5")]
    [InlineData("601")]
    public void CaptureIsDisabledUnlessExplicitlyRequested(string? value) =>
        Assert.Null(FrameCaptureOptions.Parse(value, null, null, "out"));

    [Fact]
    public void OptionsParseDurationDelayAndProcessFilter()
    {
        var defaults = FrameCaptureOptions.Parse("1", null, null, "out")!;
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.Duration);
        Assert.Equal(TimeSpan.FromSeconds(15), defaults.StartDelay);
        Assert.Null(defaults.ProcessFilter);
        var custom = FrameCaptureOptions.Parse("30", "0", "DoorKickers2.exe", "out")!;
        Assert.Equal(TimeSpan.FromSeconds(30), custom.Duration);
        Assert.Equal(TimeSpan.Zero, custom.StartDelay);
        Assert.Equal("DoorKickers2", custom.ProcessFilter);
    }

    [Fact]
    public void AdmissionReasonsKeepTheExactProductionAcceptedSet()
    {
        var now = Second * 100;
        var window = new PresentedFrameWindow();
        Assert.Equal(FrameAdmission.ZeroQpc, window.Admit(0, 7, 7, now));
        Assert.Equal(FrameAdmission.FutureQpc, window.Admit(now + 1, 7, 7, now));
        Assert.Equal(FrameAdmission.TooOld, window.Admit(now - Second * 16, 7, 7, now));
        Assert.Equal(FrameAdmission.InvalidInterval, window.Admit(now - 10, double.NaN, 7, now));
        Assert.Equal(FrameAdmission.InvalidInterval, window.Admit(now - 10, 0, 7, now));
        Assert.Equal(FrameAdmission.Accepted, window.Admit(now - 10, 7, 7, now));
        Assert.Equal(FrameAdmission.DuplicateQpc, window.Admit(now - 10, 9, 7, now));
        Assert.Equal(FrameAdmission.NonAdvancingQpc, window.Admit(now - 20, 9, 7, now));

        // Randomised equivalence with the former single-expression rejection rule.
        var random = new Random(7);
        var reference = new ReferenceWindow();
        var candidate = new PresentedFrameWindow();
        for (var index = 0; index < 5000; index++)
        {
            var qpc = random.Next(10) == 0 ? 0 : now - (ulong)random.Next(0, (int)Math.Min(int.MaxValue, Second * 20));
            var ms = random.Next(20) switch { 0 => double.NaN, 1 => -1, 2 => 0, _ => random.NextDouble() * 30 };
            Assert.Equal(reference.Add(qpc, ms, now), candidate.Add(qpc, ms, ms, now));
        }
    }

    [Fact]
    public async Task CaptureIsBoundedAndReportsTruncation()
    {
        var writer = new RecordingWriter();
        var session = new FrameCaptureSession(Options(maxRecords: 10), writer, _ => "Game");
        session.Observe(Second * 100, 7, true);
        Assert.True(session.IsRecording);
        for (var index = 0; index < 25; index++) session.Record(Row(7, 1, Second * 100 + (ulong)index, 7));
        Assert.Equal(10, session.RecordCount);
        await session.Complete(Second * 200, "Test", null)!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(writer.Result!.Truncated);
        Assert.Equal(10, writer.Result.Records.Count);
    }

    [Fact]
    public async Task CaptureStartsAfterDelayForMatchingProcessAndStopsOnce()
    {
        var writer = new RecordingWriter();
        var session = new FrameCaptureSession(Options(delaySeconds: 2, filter: "DoorKickers2"), writer,
            pid => pid == 9 ? "DoorKickers2" : "Other");
        session.Observe(Second * 100, 3, true);
        Assert.Equal(FrameCaptureState.Armed, session.State);
        session.Observe(Second * 100, 9, false);
        Assert.Equal(FrameCaptureState.Armed, session.State); // no selected stream yet
        session.Observe(Second * 100, 9, true);
        Assert.Equal(FrameCaptureState.Delaying, session.State);
        session.Record(Row(9, 1, Second * 100, 7));
        Assert.Equal(0, session.RecordCount); // nothing recorded during the delay
        session.Observe(Second * 102, 9, true);
        Assert.True(session.IsRecording);
        session.Record(Row(9, 1, Second * 102, 7));
        Assert.False(session.IsDue(Second * 111));
        Assert.True(session.IsDue(Second * 112));
        var first = session.Complete(Second * 112, "DurationElapsed", 140);
        Assert.Same(first, session.Complete(Second * 113, "Again", null));
        session.Record(Row(9, 1, Second * 113, 7));
        await first!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, writer.Calls);
        Assert.Single(writer.Result!.Records);
        Assert.Equal(140, writer.Result.ProductionLowAtEnd);
        Assert.Equal(9u, writer.Result.PrimaryPid);
    }

    [Fact]
    public async Task TargetStopDuringDelayReArmsAndDuringRecordingFinishesEarly()
    {
        var writer = new RecordingWriter();
        var session = new FrameCaptureSession(Options(delaySeconds: 5), writer, _ => "Game");
        session.Observe(Second * 100, 4, true);
        session.TargetStopped(4, Second * 101, null);
        Assert.Equal(FrameCaptureState.Armed, session.State);
        session.Observe(Second * 102, 5, true);
        session.Observe(Second * 107, 5, true);
        Assert.True(session.IsRecording);
        session.TargetStopped(5, Second * 108, 99);
        session.ClientDisposed(Second * 109); // already completed: no second write
        Assert.Equal(FrameCaptureState.Completed, session.State);
        await session.Completion!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("TargetStopped", writer.Result!.CompletionReason);
    }

    [Fact]
    public async Task WriterFailureNeverPropagates()
    {
        var session = new FrameCaptureSession(Options(), new RecordingWriter { Throw = true }, _ => "Game");
        session.Observe(Second * 100, 1, true);
        session.Record(Row(1, 1, Second * 100, 7));
        var completion = session.Complete(Second * 101, "Test", null)!;
        await completion.WaitAsync(TimeSpan.FromSeconds(10)); // throws if the writer failure escaped
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public void FileWriteHappensOffThePollingThreadAfterCompletion()
    {
        using var gate = new ManualResetEventSlim(false);
        var writer = new RecordingWriter { Gate = gate };
        var session = new FrameCaptureSession(Options(), writer, _ => "Game");
        session.Observe(Second * 100, 1, true);
        session.Record(Row(1, 1, Second * 100, 7));
        var pollingThread = Environment.CurrentManagedThreadId;
        var completion = session.Complete(Second * 101, "Test", null)!;
        // Complete returned while the writer is still blocked: the polling thread never waits for file I/O.
        Assert.False(completion.IsCompleted);
        gate.Set();
        // Not Task.Wait: an unstarted task could be inlined onto this thread.
        Assert.True(SpinWait.SpinUntil(() => completion.IsCompleted, TimeSpan.FromSeconds(10)));
        Assert.NotEqual(pollingThread, writer.ThreadId);
    }

    [Fact]
    public void SlowestSelectionUsesCeilOnePercentAndProductionFormula()
    {
        var records = Enumerable.Range(0, 250)
            .Select(index => Row(1, 1, Second * 100 + (ulong)index * 1000, 7, displayChange: index switch { 10 => 30, 20 => 20, 30 => 10, _ => 7 }))
            .ToArray();
        var slowest = FrameCaptureAnalyzer.SelectSlowest(records);
        Assert.Equal(3, slowest.Length); // ceil(250 * 0.01)
        Assert.Equal([30d, 20d, 10d], slowest.Select(record => record.MsBetweenDisplayChange));
        var stats = FrameCaptureAnalyzer.Statistics(records.Select(record => record.MsBetweenDisplayChange))!;
        Assert.Equal(3, stats.SlowCount);
        Assert.Equal(20, stats.SlowMeanMs, 9);
        Assert.Equal(50, stats.LowFps, 9);
        Assert.Equal(7, stats.MedianMs);

        // The analyzer agrees with the production window on the same accepted samples.
        var window = new PresentedFrameWindow();
        var now = records[^1].PresentQpc;
        foreach (var record in records) window.Add(record.PresentQpc, record.MsBetweenPresents, record.MsBetweenDisplayChange, now);
        Assert.Equal(window.Calculate(now)!.Value, stats.LowFps, 9);
    }

    [Fact]
    public void AnalysisClassifiesSlowFramesGroupsStreamsAndComparesDisplayIntervals()
    {
        var rows = new List<FrameCaptureRecord>();
        var qpc = Second * 100;
        for (var index = 0; index < 400; index++)
        {
            qpc += Second / 144;
            var slow = index % 100 == 50; // 4 slow, all undisplayed
            rows.Add(Row(1, 11, qpc, slow ? 12 : 6.94, dropped: (sbyte)(slow ? 1 : 0), displayChange: slow ? double.NaN : 6.94));
            if (index % 50 == 0) rows.Add(Row(1, 11, qpc, 6.94, FrameAdmission.DuplicateQpc, dropped: 0, displayChange: 6.94));
            if (index % 4 == 0) rows.Add(Row(1, 22, qpc + 5, 40, selected: false, dropped: 0, displayChange: 40)); // secondary chain
        }
        rows.Add(Row(2, 33, qpc, 100, selected: true, dropped: 0)); // another PID
        var result = Result(rows, primary: 1);
        var summary = FrameCaptureAnalyzer.Analyze(result);

        // Production samples are the display intervals of admitted Presents; the 4 undisplayed Presents add none.
        Assert.Equal(396, summary.Production!.Count);
        Assert.Equal(4, summary.Production.SlowCount);
        Assert.Equal(1000 / 6.94, summary.Production.LowFps, 6);
        Assert.DoesNotContain(summary.Categories, share => share.Value.StartsWith("Undisplayed"));
        // The former Present-interval source stays visible as a diagnostic.
        Assert.Equal(400, summary.PresentIntervals!.Count);
        Assert.Equal(1000d / 12, summary.PresentIntervals.LowFps, 6);

        Assert.Equal(2, summary.CandidatePids);
        Assert.Equal(3, summary.Streams.Count);
        var secondary = summary.Streams.Single(stream => stream.SwapChain == 22);
        Assert.Equal(100, secondary.AcceptedRows);
        Assert.Equal(0, secondary.SelectedRows);
        Assert.Equal(0, secondary.ProductionSlowContribution);
        Assert.Equal(4, summary.Streams.Single(stream => stream.SwapChain == 11).ProductionSlowContribution);

        // Display intervals: displayed rows of the selected chain, including display-instance (duplicate QPC) rows.
        Assert.Equal(396 + 8, summary.DisplayIntervalRows);
        Assert.Equal(1000 / 6.94, summary.DisplayIntervals!.LowFps, 6);

        Assert.Equal(1000d / 12, summary.Variants.Single(variant => variant.Name == "PresentIntervals").Statistics!.LowFps, 6);
        var excludeUndisplayed = summary.Variants.Single(variant => variant.Name == "ExcludeUndisplayed");
        Assert.Equal(4, excludeUndisplayed.Removed);
        Assert.Equal(1000 / 6.94, excludeUndisplayed.Statistics!.LowFps, 6);
        Assert.Contains("ExcludeUndisplayed", summary.Text);
        Assert.Contains("MsBetweenDisplayChange", summary.Text);
    }

    [Fact]
    public void PeriodicSlowFramesShowPhaseLockAndActivityCoincidence()
    {
        var rows = new List<FrameCaptureRecord>();
        var marks = new List<ActivityMark>();
        for (var index = 0; index < 6000; index++)
        {
            var qpc = Second * 100 + (ulong)(index * (Second / 100.0));
            var slow = index % 100 == 0; // exactly once per second
            rows.Add(Row(1, 1, qpc, slow ? 25 : 10));
            if (slow) marks.Add(new ActivityMark(qpc - Second / 1000, "SlowPoll"));
        }
        var summary = FrameCaptureAnalyzer.Analyze(Result(rows, 1, marks));
        Assert.Equal(60, summary.Production!.SlowCount);
        var oneSecond = summary.Periodicity.Single(period => period.PeriodMs == 1000);
        Assert.True(oneSecond.SlowResultant > 0.99);
        Assert.True(oneSecond.BaselineResultant < 0.1);
        Assert.Equal(1000, summary.SlowGapHistogram[0].BinMs);
        var activity = Assert.Single(summary.Activity);
        Assert.Equal(1.0, activity.SlowHitShare);
        Assert.True(activity.Lift > 50);
    }

    [Fact]
    public void CsvHasOneFixedWidthRowPerFrameAndMarkAndFlagsSlowest()
    {
        var rows = Enumerable.Range(0, 200).Select(index => Row(1, 1, Second * 100 + (ulong)index * 1000, index == 5 ? 30 : 7)).ToList();
        rows.Add(Row(1, 1, Second * 100 + 1000, 7, FrameAdmission.DuplicateQpc));
        var result = Result(rows, 1, [new ActivityMark(Second * 100, "Provider, with comma")]);
        var summary = FrameCaptureAnalyzer.Analyze(result);
        using var text = new StringWriter();
        CsvFrameCaptureWriter.WriteCsv(text, result, summary);
        var lines = text.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var header = lines.Single(line => line.StartsWith("RecordType,"));
        var data = lines.SkipWhile(line => !line.StartsWith("RecordType,")).Skip(1).ToArray();
        Assert.Equal(202, data.Length);
        var columns = header.Split(',').Length;
        Assert.All(data.Where(line => line.StartsWith("frame,")), line => Assert.Equal(columns, line.Split(',').Length));
        Assert.Contains("\"Provider, with comma\"", data[^1]);
        var accepted = Array.IndexOf(header.Split(','), "AcceptedForOnePercentLow");
        var slowest = Array.IndexOf(header.Split(','), "InSlowest1Pct");
        var frames = data.Where(line => line.StartsWith("frame,")).Select(line => line.Split(',')).ToArray();
        Assert.Equal(200, frames.Count(cells => cells[accepted] == "1"));
        Assert.Equal(2, frames.Count(cells => cells[slowest] == "1"));
        Assert.Contains("SchemaVersion=2", lines[0]);
    }

    [Fact]
    public async Task ClientCapturesProductionRowsWithoutChangingTelemetry()
    {
        var plain = new CaptureNative();
        using var plainClient = plain.CreateClient(null);
        var writer = new RecordingWriter();
        var capturedNative = new CaptureNative();
        var session = new FrameCaptureSession(Options(seconds: 10, maxRecords: 10_000), writer, _ => "DoorKickers2");
        using var client = capturedNative.CreateClient(session);
        Assert.True(plainClient.Track(100));
        Assert.True(client.Track(100));
        Assert.Equal("Full", session.QueryTier);
        for (var poll = 0; poll < 60; poll++)
        {
            plain.Advance();
            capturedNative.Advance();
            var expected = plainClient.Poll(100, true, false);
            var actual = client.Poll(100, true, false);
            Assert.Equal(expected, actual); // capture never changes FPS/FT/1%/source QPC
        }
        Assert.Equal(FrameCaptureState.Completed, session.State);
        await session.Completion!.WaitAsync(TimeSpan.FromSeconds(10));
        var result = writer.Result!;
        Assert.Equal("DurationElapsed", result.CompletionReason);
        Assert.False(result.Truncated);
        Assert.All(result.Records, record => Assert.True(record.PresentQpc <= result.EndQpc));
        Assert.Contains(result.Records, record => record.Admission == FrameAdmission.DuplicateQpc);
        Assert.Contains(result.Records, record => record.Admission == FrameAdmission.Accepted && record.SelectedChain);
        Assert.Contains(result.Records, record => !record.SelectedChain);
        var slow = result.Records.First(record => record.MsBetweenPresents == 20);
        Assert.Equal(1, slow.Dropped);
        Assert.True(double.IsNaN(slow.MsBetweenDisplayChange));
        Assert.Equal(2, slow.FrameType);
        Assert.Equal(3, slow.PresentMode);
        Assert.Equal(1, slow.PresentRuntime);
        Assert.Equal(1, slow.SyncInterval);
        var displayed = result.Records.First(record => record.Dropped == 0);
        Assert.Equal(6.94, displayed.MsBetweenDisplayChange, 6);
        Assert.NotNull(writer.Summary!.Production);
        Assert.NotNull(result.ProductionLowAtEnd);
    }

    [Fact]
    public void UnsupportedDiagnosticColumnsFallBackWithoutLosingTheProductionQuery()
    {
        var native = new CaptureNative { RejectFull = true };
        var session = new FrameCaptureSession(Options(), new RecordingWriter(), _ => "Game");
        using var client = native.CreateClient(session);
        Assert.True(client.Track(100));
        Assert.Equal("Core", session.QueryTier);
        native.Advance();
        Assert.Equal(144, client.Poll(100, false, false).FramesPerSecond);

        var none = new CaptureNative { RejectAllDiagnostics = true };
        var second = new FrameCaptureSession(Options(), new RecordingWriter(), _ => "Game");
        using var baseClient = none.CreateClient(second);
        Assert.True(baseClient.Track(100));
        Assert.Equal("PresentOnly", second.QueryTier);
        none.Advance();
        var presentOnly = baseClient.Poll(100, true, false);
        Assert.True(presentOnly.SourceQpc > 0);
        Assert.Equal(144, presentOnly.FramesPerSecond);
        Assert.Null(presentOnly.OnePercentLow); // never silently falls back to Present intervals
    }

    [Fact]
    public void ProductionQueryRegistersDisplayChangeAndOnePercentLowIgnoresPresentJitter()
    {
        var native = new CaptureNative();
        using var client = native.CreateClient(null);
        Assert.True(client.Track(100));
        Assert.Equal([1, 77, 78, 80], native.RegisteredMetrics);
        double? low = null;
        for (var poll = 0; poll < 8; poll++)
        {
            native.Advance();
            low = client.Poll(100, true, false).OnePercentLow;
        }
        // Present intervals include 20 ms (undisplayed) rows; the displayed cadence is a steady 6.94 ms.
        Assert.Equal(1000 / 6.94, low!.Value, 6);
    }

    private static FrameCaptureOptions Options(int seconds = 10, int delaySeconds = 0, string? filter = null, int maxRecords = 1000) =>
        new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(delaySeconds), filter, Path.GetTempPath(), maxRecords);

    private static FrameCaptureRecord Row(uint pid, ulong chain, ulong qpc, double ms,
        FrameAdmission admission = FrameAdmission.Accepted, bool selected = true, sbyte dropped = -1, double? displayChange = null) =>
        new(0, qpc, pid, 1, chain, selected, qpc, ms, admission, displayChange ?? ms, Dropped: dropped);

    private static FrameCaptureResult Result(IReadOnlyList<FrameCaptureRecord> records, uint primary, IReadOnlyList<ActivityMark>? marks = null)
    {
        var start = records.Min(record => record.PresentQpc);
        var end = records.Max(record => record.PresentQpc);
        return new FrameCaptureResult(Options(), Stopwatch.Frequency, start, end, DateTimeOffset.Now, primary, "Test", false, false,
            null, "Full", [], new Dictionary<uint, string> { [primary] = "Game" }, records, marks ?? []);
    }

    private sealed class ReferenceWindow
    {
        private ulong _last;
        internal bool Add(ulong qpc, double ms, ulong now)
        {
            if (qpc == 0 || qpc <= _last || qpc > now || now - qpc > (ulong)(Stopwatch.Frequency * 15) ||
                !double.IsFinite(ms) || ms <= 0) return false;
            _last = qpc;
            return true;
        }
    }

    private sealed class RecordingWriter : IFrameCaptureWriter
    {
        internal bool Throw;
        internal ManualResetEventSlim? Gate;
        internal int Calls;
        internal int ThreadId;
        internal FrameCaptureResult? Result;
        internal FrameCaptureSummary? Summary;

        public IReadOnlyList<string> Write(FrameCaptureResult result, FrameCaptureSummary summary)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            Gate?.Wait(TimeSpan.FromSeconds(10));
            Calls++;
            Result = result;
            Summary = summary;
            if (Throw) throw new IOException("disk full");
            return ["memory"];
        }
    }

    /// <summary>Native boundary fake that honours every diagnostic column the client registers.</summary>
    private sealed class CaptureNative
    {
        private PresentMonClient.QueryElement[] _fast = [];
        private PresentMonClient.QueryElement[] _present = [];
        private int _stride;
        private ulong _now = Second * 100;
        private ulong _lastQpc;
        private int _call;
        internal bool RejectFull;
        internal bool RejectAllDiagnostics;
        internal int[] RegisteredMetrics = [];

        internal void Advance() => _now += Second / 4;

        internal PresentMonClient CreateClient(FrameCaptureSession? session) => new(Register, Poll, RegisterFrame, Consume,
            (_, _) => 0, _ => 0, (_, _) => 0, () => _now, false, session);

        private int Register(nint session, out nint query, PresentMonClient.QueryElement[] elements, ulong count, double window, double offset)
        {
            for (var index = 0; index < elements.Length; index++)
            {
                elements[index].DataOffset = (ulong)index * 8;
                elements[index].DataSize = 8;
            }
            _fast = elements;
            query = 10;
            return 0;
        }

        private int Poll(nint query, uint pid, byte[] buffer, ref uint count)
        {
            count = 1;
            BitConverter.TryWriteBytes(buffer.AsSpan((int)_fast[0].DataOffset), 11UL);
            BitConverter.TryWriteBytes(buffer.AsSpan((int)_fast[1].DataOffset), 144d);
            BitConverter.TryWriteBytes(buffer.AsSpan((int)_fast[2].DataOffset), 6.94);
            return 0;
        }

        private int RegisterFrame(nint session, out nint query, PresentMonClient.QueryElement[] elements, ulong count, out uint blobSize)
        {
            query = 0;
            blobSize = 0;
            if ((RejectFull && elements.Length > 6) || (RejectAllDiagnostics && elements.Length > 3)) return 5;
            for (var index = 0; index < elements.Length; index++)
            {
                elements[index].DataOffset = (ulong)index * 8;
                elements[index].DataSize = elements[index].Metric switch { 16 or 22 => 1, 63 or 18 or 19 or 20 or 21 => 4, _ => 8 };
            }
            _present = elements;
            RegisteredMetrics = elements.Select(element => element.Metric).ToArray();
            _stride = elements.Length * 8;
            blobSize = (uint)_stride;
            query = 40;
            return 0;
        }

        // Each poll: 34 rows on the selected chain (one 20 ms undisplayed, one duplicate display-instance row) + 1 secondary row.
        private int Consume(nint query, uint pid, byte[] buffer, ref uint count)
        {
            var rows = new List<(ulong Chain, ulong Qpc, double Ms, bool Dropped)>();
            // 34 * 6.94 ms + one 20 ms interval < 250 ms: rows stay in the past and after the previous poll's rows.
            var qpc = Math.Max(_lastQpc, _now - Second / 4);
            for (var index = 0; index < 34; index++)
            {
                var slow = index == 17 && _call % 4 == 0;
                qpc += (ulong)(Second * (slow ? 0.020 : 0.00694));
                rows.Add((11, qpc, slow ? 20 : 6.94, slow));
                if (index == 5) rows.Add((11, qpc, 6.94, false));
            }
            rows.Add((22, qpc - 3, 33, false));
            _lastQpc = qpc;
            _call++;
            count = (uint)rows.Count;
            for (var row = 0; row < rows.Count; row++)
            {
                var blob = buffer.AsSpan(row * _stride, _stride);
                foreach (var element in _present)
                {
                    var target = blob[(int)element.DataOffset..];
                    var (chain, rowQpc, ms, dropped) = rows[row];
                    switch (element.Metric)
                    {
                        case 1: BitConverter.TryWriteBytes(target, chain); break;
                        case 77: BitConverter.TryWriteBytes(target, rowQpc); break;
                        case 78: BitConverter.TryWriteBytes(target, ms); break;
                        case 16: target[0] = (byte)(dropped ? 1 : 0); break;
                        case 63: BitConverter.TryWriteBytes(target, 2); break;
                        case 80: BitConverter.TryWriteBytes(target, dropped ? double.NaN : 6.94); break;
                        case 18: BitConverter.TryWriteBytes(target, 1); break;
                        case 19: BitConverter.TryWriteBytes(target, 0u); break;
                        case 20: BitConverter.TryWriteBytes(target, 3); break;
                        case 21: BitConverter.TryWriteBytes(target, 1); break;
                        case 22: target[0] = 0; break;
                        default: BitConverter.TryWriteBytes(target, 1.5); break;
                    }
                }
            }
            return 0;
        }
    }
}
