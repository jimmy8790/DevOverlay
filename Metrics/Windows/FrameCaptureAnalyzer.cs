using System.Globalization;
using System.Text;

namespace DevOverlay.Metrics.Windows;

internal sealed record FrameStatistics(int Count, int SlowCount, double MinMs, double MedianMs, double MeanMs,
    double MaxMs, double SlowMeanMs, double LowFps, double AverageFps);

internal sealed record CategoryShare(string Field, string Value, int AllCount, double AllShare, int SlowCount,
    double SlowShare, double Lift);

internal sealed record StreamSummary(uint Pid, ulong SwapChain, int AcceptedRows, int SelectedRows, double MedianMs,
    int ProductionSlowContribution, int MixedSlowContribution);

internal sealed record PeriodicityResult(string Label, double PeriodMs, int SlowSamples, double SlowResultant,
    double SlowPValue, double BaselineResultant);

internal sealed record ActivityCorrelation(string Source, int Marks, double SlowHitShare, double AllHitShare, double Lift);

internal sealed record VariantResult(string Name, string Rule, FrameStatistics? Statistics, int Removed);

internal sealed record FrameCaptureSummary(
    uint PrimaryPid,
    FrameStatistics? Production,
    FrameStatistics? ProductionFinalWindow,
    double? ProductionLowAtEnd,
    IReadOnlyList<FrameCaptureRecord> Slowest,
    HashSet<(uint Pid, ulong SwapChain, ulong Qpc)> SlowestKeys,
    IReadOnlyDictionary<FrameAdmission, int> Admissions,
    IReadOnlyList<CategoryShare> Categories,
    IReadOnlyList<StreamSummary> Streams,
    int CandidatePids,
    int SelectedChainChanges,
    FrameStatistics? PresentIntervals,
    FrameStatistics? DisplayIntervals,
    int DisplayIntervalRows,
    IReadOnlyList<PeriodicityResult> Periodicity,
    IReadOnlyList<(int BinMs, int Count)> SlowGapHistogram,
    IReadOnlyList<ActivityCorrelation> Activity,
    IReadOnlyList<VariantResult> Variants,
    string Text);

/// <summary>
/// Pure, post-capture analysis using the production 1% semantics: display-change intervals of the admitted Presents of
/// the selected swap chain, slowest <c>ceil(N*0.01)</c>, arithmetic mean, <c>1000/meanMs</c>. Present-interval
/// statistics and all alternative populations are diagnostic only.
/// </summary>
internal static class FrameCaptureAnalyzer
{
    internal const int WorstListSize = 50;
    internal static readonly double[] CandidatePeriodsMs = [100, 250, 500, 1000];

    internal static FrameStatistics? Statistics(IEnumerable<double> milliseconds)
    {
        var times = milliseconds.Where(ms => double.IsFinite(ms) && ms > 0).ToArray();
        if (times.Length == 0) return null;
        Array.Sort(times);
        var slowCount = Math.Max(1, (int)Math.Ceiling(times.Length * 0.01));
        double slowSum = 0;
        for (var index = times.Length - slowCount; index < times.Length; index++) slowSum += times[index];
        var slowMean = slowSum / slowCount;
        var mean = times.Average();
        var median = times.Length % 2 == 1 ? times[times.Length / 2] : (times[times.Length / 2 - 1] + times[times.Length / 2]) / 2;
        return new FrameStatistics(times.Length, slowCount, times[0], median, mean, times[^1], slowMean, 1000 / slowMean, 1000 / mean);
    }

    /// <summary>
    /// Exactly <c>ceil(N*0.01)</c> production samples with the largest display-change interval; ties broken by
    /// Present QPC for determinism. Rows without a display interval are not samples and are ignored.
    /// </summary>
    internal static FrameCaptureRecord[] SelectSlowest(IReadOnlyList<FrameCaptureRecord> population)
    {
        var samples = population.Where(record => PresentedFrameWindow.IsDisplayInterval(record.MsBetweenDisplayChange)).ToArray();
        if (samples.Length == 0) return [];
        var slowCount = Math.Max(1, (int)Math.Ceiling(samples.Length * 0.01));
        return samples.OrderByDescending(record => record.MsBetweenDisplayChange).ThenBy(record => record.PresentQpc)
            .Take(slowCount).ToArray();
    }

    internal static FrameCaptureSummary Analyze(FrameCaptureResult result)
    {
        var primary = result.PrimaryPid;
        var frequency = (double)result.QpcFrequency;
        var records = result.Records;
        var presentPopulation = records.Where(record => record.InPresentPopulation(primary)).ToArray();
        var production = presentPopulation.Where(record => record.InProductionPopulation(primary)).ToArray();
        var slowest = SelectSlowest(production);
        var slowestKeys = slowest.Select(record => (Pid: record.Pid, SwapChain: record.SwapChain, Qpc: record.PresentQpc)).ToHashSet();
        var productionStats = Statistics(production.Select(record => record.MsBetweenDisplayChange));
        var presentStats = Statistics(presentPopulation.Select(record => record.MsBetweenPresents));
        var windowTicks = (ulong)(frequency * PresentedFrameWindow.OnePercentLowWindow.TotalSeconds);
        var finalCutoff = result.EndQpc > windowTicks ? result.EndQpc - windowTicks : 0;
        var finalWindow = Statistics(production.Where(record => record.PresentQpc >= finalCutoff && record.PresentQpc <= result.EndQpc)
            .Select(record => record.MsBetweenDisplayChange));

        var admissions = records.Where(record => record.Pid == primary).GroupBy(record => record.Admission)
            .ToDictionary(group => group.Key, group => group.Count());

        var categories = Categories(production, slowest);
        var streams = Streams(records, primary, slowestKeys);
        var candidatePids = records.Select(record => record.Pid).Distinct().Count();
        var chainChanges = 0;
        ulong? previousChain = null;
        long previousPoll = -1;
        foreach (var record in records.Where(record => record.Pid == primary && record.SelectedChain))
        {
            if (record.PollIndex == previousPoll) continue;
            previousPoll = record.PollIndex;
            if (previousChain is { } chain && chain != record.SwapChain) chainChanges++;
            previousChain = record.SwapChain;
        }

        // Each display instance has its own display-change interval, so duplicate-QPC rows of the selected chain count here.
        var displayRows = records.Where(record => record.Pid == primary && record.SelectedChain &&
            (record.Admission is FrameAdmission.Accepted or FrameAdmission.DuplicateQpc) &&
            double.IsFinite(record.MsBetweenDisplayChange) && record.MsBetweenDisplayChange > 0).ToArray();
        var displayStats = Statistics(displayRows.Select(record => record.MsBetweenDisplayChange));

        var periodicity = Periodicity(production, slowest, frequency);
        var histogram = GapHistogram(slowest, frequency);
        var activity = Activity(result.Marks, production, slowestKeys, frequency);
        var variants = Variants(presentPopulation, presentStats);

        var summary = new FrameCaptureSummary(primary, productionStats, finalWindow, result.ProductionLowAtEnd, slowest.Take(WorstListSize).ToArray(),
            slowestKeys, admissions, categories, streams, candidatePids, chainChanges, presentStats, displayStats, displayRows.Length,
            periodicity, histogram, activity, variants, "");
        return summary with { Text = Format(result, summary, slowest) };
    }

    internal static IReadOnlyList<CategoryShare> Categories(IReadOnlyList<FrameCaptureRecord> all, IReadOnlyList<FrameCaptureRecord> slowest)
    {
        var fields = new (string Name, Func<FrameCaptureRecord, string> Value)[]
        {
            ("DisplayState", record => record.Dropped switch { 1 => "Undisplayed(Dropped=1)", 0 => "Displayed(Dropped=0)", _ => "Unknown" }),
            ("DisplayChangeInterval", record => double.IsFinite(record.MsBetweenDisplayChange) ? "Present" : "Missing/NaN"),
            ("FrameType", record => FrameCaptureSchema.FrameTypeName(record.FrameType)),
            ("PresentMode", record => FrameCaptureSchema.PresentModeName(record.PresentMode)),
            ("PresentRuntime", record => FrameCaptureSchema.RuntimeName(record.PresentRuntime)),
            ("SyncInterval", record => record.SyncInterval < 0 ? "Unknown" : record.SyncInterval.ToString(CultureInfo.InvariantCulture)),
            ("AllowsTearing", record => record.AllowsTearing switch { 1 => "1", 0 => "0", _ => "Unknown" }),
            ("PresentFlags", record => record.PresentFlags < 0 ? "Unknown" : "0x" + record.PresentFlags.ToString("X", CultureInfo.InvariantCulture)),
            ("SwapChain", record => record.SwapChain.ToString(CultureInfo.InvariantCulture))
        };
        var shares = new List<CategoryShare>();
        if (all.Count == 0) return shares;
        foreach (var (name, value) in fields)
        {
            var allCounts = all.GroupBy(value).ToDictionary(group => group.Key, group => group.Count());
            var slowCounts = slowest.GroupBy(value).ToDictionary(group => group.Key, group => group.Count());
            foreach (var (key, count) in allCounts.OrderByDescending(pair => pair.Value))
            {
                var slow = slowCounts.GetValueOrDefault(key);
                var allShare = (double)count / all.Count;
                var slowShare = slowest.Count == 0 ? 0 : (double)slow / slowest.Count;
                shares.Add(new CategoryShare(name, key, count, allShare, slow, slowShare, allShare > 0 ? slowShare / allShare : 0));
            }
        }
        return shares;
    }

    internal static IReadOnlyList<StreamSummary> Streams(IReadOnlyList<FrameCaptureRecord> records, uint primary,
        HashSet<(uint Pid, ulong SwapChain, ulong Qpc)> productionSlowest)
    {
        var accepted = records.Where(record => record.Admission == FrameAdmission.Accepted).ToArray();
        // Hypothetical: what if every accepted stream were mixed into one window (production never does this).
        var mixedSlowest = SelectSlowest(accepted).Select(record => (record.Pid, record.SwapChain)).ToArray();
        return accepted.GroupBy(record => (record.Pid, record.SwapChain)).Select(group =>
        {
            var times = group.Select(record => record.MsBetweenPresents).Order().ToArray();
            var median = times.Length % 2 == 1 ? times[times.Length / 2] : (times[times.Length / 2 - 1] + times[times.Length / 2]) / 2;
            return new StreamSummary(group.Key.Pid, group.Key.SwapChain, times.Length,
                group.Count(record => record.SelectedChain && record.Pid == primary), median,
                productionSlowest.Count(key => key.Pid == group.Key.Pid && key.SwapChain == group.Key.SwapChain),
                mixedSlowest.Count(key => key == group.Key));
        }).OrderByDescending(stream => stream.AcceptedRows).ToArray();
    }

    /// <summary>
    /// Rayleigh phase concentration of slow-frame Present times for fixed periods: R near 0 = no phase lock,
    /// near 1 = locked to the cadence. p ≈ exp(-n R²). Baseline uses all production frames for comparison.
    /// </summary>
    internal static IReadOnlyList<PeriodicityResult> Periodicity(IReadOnlyList<FrameCaptureRecord> production,
        IReadOnlyList<FrameCaptureRecord> slowest, double frequency)
    {
        var results = new List<PeriodicityResult>();
        if (slowest.Count < 2 || production.Count == 0) return results;
        foreach (var period in CandidatePeriodsMs)
        {
            var slowR = Resultant(slowest, period, frequency);
            results.Add(new PeriodicityResult($"{period:0}ms", period, slowest.Count, slowR,
                Math.Exp(-slowest.Count * slowR * slowR), Resultant(production, period, frequency)));
        }
        return results;
    }

    internal static double Resultant(IEnumerable<FrameCaptureRecord> records, double periodMs, double frequency)
    {
        double sumCos = 0, sumSin = 0;
        var count = 0;
        foreach (var record in records)
        {
            var ms = record.PresentQpc * 1000.0 / frequency;
            var phase = 2 * Math.PI * (ms % periodMs) / periodMs;
            sumCos += Math.Cos(phase);
            sumSin += Math.Sin(phase);
            count++;
        }
        return count == 0 ? 0 : Math.Sqrt(sumCos * sumCos + sumSin * sumSin) / count;
    }

    internal static IReadOnlyList<(int BinMs, int Count)> GapHistogram(IReadOnlyList<FrameCaptureRecord> slowest, double frequency)
    {
        var ordered = slowest.Select(record => record.PresentQpc).Order().ToArray();
        const int binMs = 50;
        return ordered.Zip(ordered.Skip(1), (first, second) => (second - first) * 1000.0 / frequency)
            .GroupBy(gap => (int)(Math.Round(gap / binMs) * binMs)).Select(group => (BinMs: group.Key, Count: group.Count()))
            .OrderByDescending(bin => bin.Count).ThenBy(bin => bin.BinMs).Take(8).ToArray();
    }

    /// <summary>Share of slow vs all production intervals [Present-ms, Present] containing a DevOverlay activity mark.</summary>
    internal static IReadOnlyList<ActivityCorrelation> Activity(IReadOnlyList<ActivityMark> marks,
        IReadOnlyList<FrameCaptureRecord> production, HashSet<(uint Pid, ulong SwapChain, ulong Qpc)> slowest, double frequency)
    {
        var results = new List<ActivityCorrelation>();
        if (production.Count == 0) return results;
        foreach (var source in marks.GroupBy(mark => mark.Source))
        {
            var times = source.Select(mark => mark.Qpc).Order().ToArray();
            int allHits = 0, slowHits = 0;
            foreach (var record in production)
            {
                var start = record.PresentQpc - Math.Min(record.PresentQpc, (ulong)(record.MsBetweenPresents * frequency / 1000));
                if (!ContainsMark(times, start, record.PresentQpc)) continue;
                allHits++;
                if (slowest.Contains((record.Pid, record.SwapChain, record.PresentQpc))) slowHits++;
            }
            var allShare = (double)allHits / production.Count;
            var slowShare = slowest.Count == 0 ? 0 : (double)slowHits / slowest.Count;
            results.Add(new ActivityCorrelation(source.Key, times.Length, slowShare, allShare, allShare > 0 ? slowShare / allShare : 0));
        }
        return results.OrderByDescending(result => result.Lift).ToArray();
    }

    private static bool ContainsMark(ulong[] sorted, ulong start, ulong end)
    {
        var index = Array.BinarySearch(sorted, start);
        if (index < 0) index = ~index;
        return index < sorted.Length && sorted[index] <= end;
    }

    /// <summary>
    /// Present-interval populations (the pre-display-change 1% source, kept for comparison). Only objectively distinct
    /// metadata categories are removed; never a threshold on the interval itself.
    /// </summary>
    internal static IReadOnlyList<VariantResult> Variants(IReadOnlyList<FrameCaptureRecord> presentPopulation, FrameStatistics? presentStats)
    {
        VariantResult Without(string name, string rule, Func<FrameCaptureRecord, bool> remove)
        {
            var kept = presentPopulation.Where(record => !remove(record)).ToArray();
            return new VariantResult(name, rule, Statistics(kept.Select(record => record.MsBetweenPresents)), presentPopulation.Count - kept.Length);
        }
        return
        [
            new VariantResult("PresentIntervals", "selected chain, accepted, raw MsBetweenPresents", presentStats, 0),
            Without("ExcludeUndisplayed", "remove rows with Dropped=1", record => record.Dropped == 1),
            Without("ExcludeNoDisplayChange", "remove rows whose MsBetweenDisplayChange is NaN/missing", record => !double.IsFinite(record.MsBetweenDisplayChange)),
            Without("ExcludeNonApplicationFrameType", "remove FrameType REPEATED/XEFG/AFMF/other (NOT_SET/UNSPECIFIED/APPLICATION kept)",
                record => record.FrameType is not (-1 or 0 or 1 or 2))
        ];
    }

    internal static string Format(FrameCaptureResult result, FrameCaptureSummary summary, IReadOnlyList<FrameCaptureRecord> slowest)
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        void Line(FormattableString value) => text.AppendLine(value.ToString(c));
        string Stats(FrameStatistics? s) => s is null ? "N/A" : string.Create(c,
            $"N={s.Count} slowCount={s.SlowCount} min={s.MinMs:F3} median={s.MedianMs:F3} mean={s.MeanMs:F3} max={s.MaxMs:F3} slowMean={s.SlowMeanMs:F3} avgFps={s.AverageFps:F2} low1%={s.LowFps:F2}");

        Line($"DevOverlay frame capture summary (schema {FrameCaptureSchema.Version}) — diagnostic only, production 1% unchanged");
        Line($"Start={result.StartWallClock:O} Duration={result.Options.Duration.TotalSeconds:0}s Reason={result.CompletionReason} PrimaryPid={result.PrimaryPid} Name={result.ProcessNames.GetValueOrDefault(result.PrimaryPid, "?")}");
        Line($"QueryTier={result.QueryTier} Records={result.Records.Count} Truncated={result.Truncated} Marks={result.Marks.Count} MarksTruncated={result.MarksTruncated}");
        text.AppendLine();
        Line($"[Production population (MsBetweenDisplayChange), whole capture] {Stats(summary.Production)}");
        Line($"[Production population, final {PresentedFrameWindow.OnePercentLowWindow.TotalSeconds:0.#} s]   {Stats(summary.ProductionFinalWindow)}");
        Line($"[Production PresentedFrameWindow.Calculate at end] {(summary.ProductionLowAtEnd is { } low ? low.ToString("F2", c) : "N/A")}");
        Line($"Admissions (primary PID): {string.Join(' ', summary.Admissions.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))}");
        text.AppendLine();
        Line($"[A] MsBetweenPresents (admitted Presents, former 1% source; diagnostic): {Stats(summary.PresentIntervals)}");
        Line($"[B] MsBetweenDisplayChange (selected chain, finite>0, incl. display-instance rows; diagnostic): rows={summary.DisplayIntervalRows} {Stats(summary.DisplayIntervals)}");
        text.AppendLine();
        Line($"Streams: candidatePids={summary.CandidatePids} selectedChainChanges={summary.SelectedChainChanges}");
        foreach (var stream in summary.Streams)
            Line($"  pid={stream.Pid} chain={stream.SwapChain} accepted={stream.AcceptedRows} selectedRows={stream.SelectedRows} median={stream.MedianMs:F3} productionSlow={stream.ProductionSlowContribution} mixedSlow(hypothetical)={stream.MixedSlowContribution}");
        text.AppendLine();
        Line($"Classification (all production rows vs slowest 1%):");
        foreach (var share in summary.Categories)
            Line($"  {share.Field}={share.Value}: all {share.AllCount} ({share.AllShare:P2}) slow {share.SlowCount} ({share.SlowShare:P2}) lift={share.Lift:F2}");
        text.AppendLine();
        Line($"Diagnostic MsBetweenPresents variants (metadata-category removal only; NOT production):");
        foreach (var variant in summary.Variants)
            Line($"  {variant.Name} removed={variant.Removed} [{variant.Rule}] {Stats(variant.Statistics)}");
        text.AppendLine();
        Line($"Periodicity of slowest-1% Present times (Rayleigh R; baseline = all production frames):");
        foreach (var period in summary.Periodicity)
            Line($"  {period.Label}: n={period.SlowSamples} R={period.SlowResultant:F3} p≈{period.SlowPValue:G3} baselineR={period.BaselineResultant:F3}");
        Line($"  slow-to-slow gap histogram (50 ms bins, top): {string.Join(' ', summary.SlowGapHistogram.Select(bin => $"{bin.BinMs}ms×{bin.Count}"))}");
        Line($"DevOverlay activity inside the frame interval (slow share vs all share):");
        foreach (var activity in summary.Activity)
            Line($"  {activity.Source}: marks={activity.Marks} slow={activity.SlowHitShare:P1} all={activity.AllHitShare:P1} lift={activity.Lift:F2}");
        text.AppendLine();
        Line($"Worst {Math.Min(WorstListSize, slowest.Count)} production frames:");
        Line($"  captureMs,pid,chain,presentQpc,msBetweenPresents,msBetweenDisplayChange,msUntilDisplayed,dropped,frameType,presentMode,runtime,syncInterval,allowsTearing,flags");
        foreach (var record in slowest.Take(WorstListSize))
            Line($"  {(record.PresentQpc - (double)result.StartQpc) * 1000 / result.QpcFrequency:F1},{record.Pid},{record.SwapChain},{record.PresentQpc},{record.MsBetweenPresents:F3},{record.MsBetweenDisplayChange:F3},{record.MsUntilDisplayed:F3},{record.Dropped},{FrameCaptureSchema.FrameTypeName(record.FrameType)},{FrameCaptureSchema.PresentModeName(record.PresentMode)},{FrameCaptureSchema.RuntimeName(record.PresentRuntime)},{record.SyncInterval},{record.AllowsTearing},{record.PresentFlags}");
        return text.ToString();
    }
}
