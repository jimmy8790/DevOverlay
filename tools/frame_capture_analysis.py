#!/usr/bin/env python3
"""Independent re-analysis of a DevOverlay frame capture (DEVOVERLAY_FRAME_CAPTURE) and optional FrameView CSV.

Diagnostic tool only (standard library, no dependencies). It re-implements the production 1% Low semantics
(raw accepted MsBetweenPresents of the selected swap chain, slowest ceil(N*0.01), arithmetic mean, 1000/meanMs)
independently of the C# analyzer, checks the C# InSlowest1Pct flags, and answers the population questions:
display state, frame type, present mode, swap chains/PIDs, display-change intervals, periodicity, DevOverlay
activity coincidence and metadata-only variants. Usage:

    python tools/frame_capture_analysis.py frame-capture-....csv [--frameview FrameView_Log.csv]
        [--frameview-process DoorKickers2.exe] [--frameview-last-seconds 60] [--out report.txt]
"""
from __future__ import annotations

import argparse
import bisect
import csv
import io
import math
import statistics
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field

PERIODS_MS = (100.0, 250.0, 500.0, 1000.0)
FRAME_TYPES = {0: "NOT_SET", 1: "UNSPECIFIED", 2: "APPLICATION", 3: "REPEATED", 50: "INTEL_XEFG", 100: "AMD_AFMF"}
PRESENT_MODES = {0: "UNKNOWN", 1: "HARDWARE_LEGACY_FLIP", 2: "HARDWARE_LEGACY_COPY_TO_FRONT_BUFFER",
                 3: "HARDWARE_INDEPENDENT_FLIP", 4: "COMPOSED_FLIP", 5: "COMPOSED_COPY_WITH_GPU_GDI",
                 6: "COMPOSED_COPY_WITH_CPU_GDI", 8: "HARDWARE_COMPOSED_INDEPENDENT_FLIP"}
RUNTIMES = {0: "UNKNOWN", 1: "DXGI", 2: "D3D9"}


def _float(text: str | None) -> float:
    try:
        value = float(text) if text not in (None, "") else math.nan
    except ValueError:
        return math.nan
    return value


def _int(text: str | None) -> int | None:
    if text in (None, ""):
        return None
    try:
        return int(float(text))
    except ValueError:
        return None


@dataclass
class Stats:
    count: int
    slow_count: int
    min_ms: float
    median_ms: float
    mean_ms: float
    max_ms: float
    slow_mean_ms: float

    @property
    def low_fps(self) -> float:
        return 1000.0 / self.slow_mean_ms

    @property
    def avg_fps(self) -> float:
        return 1000.0 / self.mean_ms

    def describe(self) -> str:
        return (f"N={self.count} slowCount={self.slow_count} min={self.min_ms:.3f} median={self.median_ms:.3f} "
                f"mean={self.mean_ms:.3f} max={self.max_ms:.3f} slowMean={self.slow_mean_ms:.3f} "
                f"avgFps={self.avg_fps:.2f} low1%={self.low_fps:.2f}")


def stats(values) -> Stats | None:
    times = sorted(v for v in values if math.isfinite(v) and v > 0)
    if not times:
        return None
    slow_count = max(1, math.ceil(len(times) * 0.01))
    slow = times[-slow_count:]
    return Stats(len(times), slow_count, times[0], statistics.median(times), sum(times) / len(times), times[-1],
                 sum(slow) / slow_count)


def describe(value: Stats | None) -> str:
    return "N/A" if value is None else value.describe()


@dataclass
class Capture:
    meta: dict = field(default_factory=dict)
    processes: dict = field(default_factory=dict)
    frames: list = field(default_factory=list)
    marks: list = field(default_factory=list)


def read_capture(path: str) -> Capture:
    capture = Capture()
    with open(path, newline="", encoding="utf-8-sig") as handle:
        lines = handle.read().splitlines()
    data_lines = []
    for line in lines:
        if line.startswith("#"):
            body = line[1:].strip()
            if body.startswith("Process "):
                parts = dict(item.split("=", 1) for item in body[len("Process "):].split(" ") if "=" in item)
                capture.processes[int(parts.get("Pid", "0"))] = parts.get("Name", "?")
            else:
                for item in body.split(" "):
                    if "=" in item:
                        key, value = item.split("=", 1)
                        capture.meta.setdefault(key, value)
        else:
            data_lines.append(line)
    for row in csv.DictReader(io.StringIO("\n".join(data_lines))):
        if row.get("RecordType") == "frame":
            capture.frames.append(row)
        elif row.get("RecordType") == "mark":
            capture.marks.append(row)
    return capture


def ms(row, column="MsBetweenPresents") -> float:
    return _float(row.get(column))


def qpc(row) -> int:
    return int(row["PresentQpc"])


def display_state(row) -> str:
    value = _int(row.get("Dropped"))
    return {1: "Undisplayed(Dropped=1)", 0: "Displayed(Dropped=0)"}.get(value, "Unknown")


CATEGORY_FIELDS = (
    ("DisplayState", display_state),
    ("DisplayChangeInterval", lambda row: "Present" if math.isfinite(ms(row, "MsBetweenDisplayChange")) else "Missing/NaN"),
    ("FrameType", lambda row: "UNKNOWN" if _int(row.get("FrameType")) is None else
        FRAME_TYPES.get(_int(row.get("FrameType")), f"OTHER_{row.get('FrameType')}")),
    ("PresentMode", lambda row: "UNAVAILABLE" if _int(row.get("PresentMode")) is None else
        PRESENT_MODES.get(_int(row.get("PresentMode")), f"OTHER_{row.get('PresentMode')}")),
    ("PresentRuntime", lambda row: "UNAVAILABLE" if _int(row.get("PresentRuntime")) is None else
        RUNTIMES.get(_int(row.get("PresentRuntime")), f"OTHER_{row.get('PresentRuntime')}")),
    ("SyncInterval", lambda row: row.get("SyncInterval") or "Unknown"),
    ("AllowsTearing", lambda row: row.get("AllowsTearing") or "Unknown"),
    ("PresentFlags", lambda row: "Unknown" if _int(row.get("PresentFlags")) is None else hex(_int(row.get("PresentFlags")))),
    ("SwapChain", lambda row: row.get("SwapChain", "")),
)


def production_column(capture) -> str:
    """Schema 2+: production 1% samples are display-change intervals; schema 1 captures used Present intervals."""
    return "MsBetweenDisplayChange" if int(capture.meta.get("SchemaVersion", "1")) >= 2 else "MsBetweenPresents"


def select_slowest(rows, column="MsBetweenPresents"):
    rows = [row for row in rows if math.isfinite(ms(row, column)) and ms(row, column) > 0]
    if not rows:
        return []
    count = max(1, math.ceil(len(rows) * 0.01))
    return sorted(rows, key=lambda row: (-ms(row, column), qpc(row)))[:count]


def rayleigh(times_ms, period):
    if not times_ms:
        return 0.0
    c = sum(math.cos(2 * math.pi * (t % period) / period) for t in times_ms)
    s = sum(math.sin(2 * math.pi * (t % period) / period) for t in times_ms)
    return math.hypot(c, s) / len(times_ms)


def analyze(capture: Capture, out) -> dict:
    freq = float(capture.meta.get("QpcFrequency", "10000000"))
    primary = int(capture.meta.get("PrimaryPid", "0"))
    end_qpc = int(capture.meta.get("EndQpc", "0"))
    frames = capture.frames
    column = production_column(capture)
    production = [row for row in frames if row.get("AcceptedForOnePercentLow") == "1"]
    present_population = [row for row in frames if _int(row.get("Pid")) == primary and row.get("SelectedChain") == "1"
                          and row.get("Admission") == "Accepted"]
    slowest = select_slowest(production, column)
    slow_keys = {(row["Pid"], row["SwapChain"], row["PresentQpc"]) for row in slowest}
    flagged = {(row["Pid"], row["SwapChain"], row["PresentQpc"]) for row in production if row.get("InSlowest1Pct") == "1"}
    prod_stats = stats(ms(row, column) for row in production)
    present_stats = stats(ms(row) for row in present_population)
    cutoff = end_qpc - int(freq * 15)
    final15 = stats(ms(row, column) for row in production if cutoff <= qpc(row) <= end_qpc)
    result = {"production": prod_stats, "present": present_stats, "slowest": slowest, "column": column}

    def p(text=""):
        print(text, file=out)

    p(f"DevOverlay frame capture re-analysis (independent Python implementation)")
    p(f"PrimaryPid={primary} Name={capture.processes.get(primary, '?')} QueryTier={capture.meta.get('QueryTier')} "
      f"Records={len(frames)} Marks={len(capture.marks)} Truncated={capture.meta.get('Truncated')} "
      f"Reason={capture.meta.get('CompletionReason')}")
    p()
    p(f"[Production population ({column}), whole capture] {describe(prod_stats)}")
    p(f"[Production population, final 15 s]   {describe(final15)}")
    p(f"[C# PresentedFrameWindow.Calculate at end] {capture.meta.get('ProductionLowAtEnd') or 'N/A'}")
    p(f"InSlowest1Pct cross-check: python={len(slow_keys)} csharp={len(flagged)} "
      f"{'MATCH' if slow_keys == flagged else 'MISMATCH (ties may differ only if equal intervals)'}")
    admissions = Counter(row.get("Admission") for row in frames if _int(row.get("Pid")) == primary)
    p("Admissions (primary PID): " + " ".join(f"{key}={value}" for key, value in sorted(admissions.items())))
    p()

    # Task 5: present vs display intervals on the same capture.
    display_rows = [row for row in frames if _int(row.get("Pid")) == primary and row.get("SelectedChain") == "1"
                    and row.get("Admission") in ("Accepted", "DuplicateQpc")
                    and math.isfinite(ms(row, "MsBetweenDisplayChange")) and ms(row, "MsBetweenDisplayChange") > 0]
    display_stats = stats(ms(row, "MsBetweenDisplayChange") for row in display_rows)
    result["display"] = display_stats
    p(f"[A] MsBetweenPresents (admitted Presents of the selected chain): {describe(present_stats)}")
    p(f"[B] MsBetweenDisplayChange (selected chain, finite>0, incl. display-instance rows): rows={len(display_rows)} "
      f"{describe(display_stats)}")
    p()

    # Task 4: streams.
    accepted = [row for row in frames if row.get("Admission") == "Accepted"]
    mixed = Counter((row["Pid"], row["SwapChain"]) for row in select_slowest(accepted, column))
    groups = defaultdict(list)
    for row in accepted:
        groups[(row["Pid"], row["SwapChain"])].append(row)
    p(f"Streams: candidatePids={len({row['Pid'] for row in frames})} streams={len(groups)}")
    for (pid, chain), rows in sorted(groups.items(), key=lambda item: -len(item[1])):
        selected = sum(1 for row in rows if row.get("SelectedChain") == "1" and _int(pid) == primary)
        prod_slow = sum(1 for key in slow_keys if key[0] == pid and key[1] == chain)
        p(f"  pid={pid}({capture.processes.get(_int(pid), '?')}) chain={chain} accepted={len(rows)} selectedRows={selected} "
          f"median={statistics.median(ms(row) for row in rows):.3f} productionSlow={prod_slow} mixedSlow(hypothetical)={mixed[(pid, chain)]}")
    p()

    # Task 3: classification.
    p("Classification (all production rows vs slowest 1%):")
    for name, getter in CATEGORY_FIELDS:
        all_counts = Counter(getter(row) for row in production)
        slow_counts = Counter(getter(row) for row in slowest)
        for key, count in all_counts.most_common():
            all_share = count / len(production)
            slow_share = slow_counts[key] / len(slowest) if slowest else 0.0
            p(f"  {name}={key}: all {count} ({all_share:.2%}) slow {slow_counts[key]} ({slow_share:.2%}) "
              f"lift={slow_share / all_share if all_share else 0:.2f}")
    p()

    # Task 7: metadata-only variants.
    p("Diagnostic MsBetweenPresents variants (metadata-category removal only; NOT production):")
    variants = {
        "ExcludeUndisplayed": lambda row: _int(row.get("Dropped")) == 1,
        "ExcludeNoDisplayChange": lambda row: not math.isfinite(ms(row, "MsBetweenDisplayChange")),
        "ExcludeNonApplicationFrameType": lambda row: _int(row.get("FrameType")) not in (None, 0, 1, 2),
    }
    result["variants"] = {}
    for name, remove in variants.items():
        kept = [row for row in present_population if not remove(row)]
        value = stats(ms(row) for row in kept)
        result["variants"][name] = value
        p(f"  {name} removed={len(present_population) - len(kept)} {describe(value)}")
    p()

    # Task 6: periodicity and activity.
    slow_times = sorted(qpc(row) * 1000.0 / freq for row in slowest)
    all_times = [qpc(row) * 1000.0 / freq for row in production]
    p("Periodicity of slowest-1% Present times (Rayleigh R; p≈exp(-nR²); baseline = all production frames):")
    for period in PERIODS_MS:
        r = rayleigh(slow_times, period)
        p(f"  {period:.0f}ms: n={len(slow_times)} R={r:.3f} p≈{math.exp(-len(slow_times) * r * r):.3g} "
          f"baselineR={rayleigh(all_times, period):.3f}")
    gaps = [b - a for a, b in zip(slow_times, slow_times[1:])]
    bins = Counter(int(round(gap / 50.0) * 50) for gap in gaps)
    p("  slow-to-slow gap histogram (50 ms bins, top): " + " ".join(f"{k}ms×{v}" for k, v in
                                                                    sorted(bins.items(), key=lambda kv: (-kv[1], kv[0]))[:8]))
    if gaps:
        p(f"  gap median={statistics.median(gaps):.1f}ms min={min(gaps):.1f}ms max={max(gaps):.1f}ms")
    marks = defaultdict(list)
    for mark in capture.marks:
        marks[mark.get("MarkSource", "")].append(int(mark["PresentQpc"]))
    p("DevOverlay activity inside the frame interval (slow share vs all share):")
    for source, times in sorted(marks.items()):
        times.sort()

        def hit(row):
            end = qpc(row)
            start = end - min(end, int(ms(row) * freq / 1000))
            index = bisect.bisect_left(times, start)
            return index < len(times) and times[index] <= end
        all_hits = sum(1 for row in production if hit(row))
        slow_hits = sum(1 for row in slowest if hit(row))
        all_share = all_hits / len(production) if production else 0
        slow_share = slow_hits / len(slowest) if slowest else 0
        p(f"  {source}: marks={len(times)} slow={slow_share:.1%} all={all_share:.1%} "
          f"lift={slow_share / all_share if all_share else 0:.2f}")
    p()
    start_qpc = int(capture.meta.get("StartQpc", "0"))
    p(f"Worst {min(50, len(slowest))} production frames:")
    p("  captureMs,pid,chain,msBetweenPresents,msBetweenDisplayChange,msUntilDisplayed,dropped,frameType,presentMode,runtime,syncInterval,allowsTearing,flags")
    for row in slowest[:50]:
        p(f"  {(qpc(row) - start_qpc) * 1000 / freq:.1f},{row['Pid']},{row['SwapChain']},{ms(row):.3f},"
          f"{row.get('MsBetweenDisplayChange')},{row.get('MsUntilDisplayed')},{row.get('Dropped')},{row.get('FrameType')},"
          f"{row.get('PresentMode')},{row.get('PresentRuntime')},{row.get('SyncInterval')},{row.get('AllowsTearing')},{row.get('PresentFlags')}")
    return result


FRAMEVIEW_ALIASES = {
    "process": ("Application", "ProcessName"),
    "pid": ("ProcessID", "ProcessId"),
    "chain": ("SwapChainAddress",),
    "time": ("TimeInSeconds", "TimeInSeconds(s)"),
    "presents": ("MsBetweenPresents", "msBetweenPresents"),
    "display": ("MsBetweenDisplayChange", "msBetweenDisplayChange"),
    "dropped": ("Dropped",),
    "mode": ("PresentMode",),
    "runtime": ("Runtime",),
}


def _pick(row, key):
    for name in FRAMEVIEW_ALIASES[key]:
        if name in row:
            return row[name]
    return None


def analyze_frameview(path, process, last_seconds, devoverlay, out):
    def p(text=""):
        print(text, file=out)
    with open(path, newline="", encoding="utf-8-sig", errors="replace") as handle:
        rows = list(csv.DictReader(handle))
    if process:
        rows = [row for row in rows if (_pick(row, "process") or "").lower() == process.lower()]
    if last_seconds and rows and _pick(rows[-1], "time") is not None:
        end = max(_float(_pick(row, "time")) for row in rows)
        rows = [row for row in rows if _float(_pick(row, "time")) >= end - last_seconds]
    p()
    p(f"FrameView CSV: {path} rows={len(rows)} process={process or '(all)'} window={'last %ss' % last_seconds if last_seconds else 'all'}")
    if not rows:
        return
    p("  processes: " + ", ".join(f"{k}×{v}" for k, v in Counter((_pick(r, 'process'), _pick(r, 'pid')) for r in rows).most_common()))
    p("  swap chains: " + ", ".join(f"{k}×{v}" for k, v in Counter(_pick(r, 'chain') for r in rows).most_common()))
    dropped = Counter(_pick(r, "dropped") for r in rows)
    p("  Dropped: " + ", ".join(f"{k}={v}" for k, v in dropped.items()))
    present_stats = stats(_float(_pick(r, "presents")) for r in rows)
    display_stats = stats(_float(_pick(r, "display")) for r in rows if _pick(r, "dropped") in (None, "0", "False", "false"))
    p(f"  [FrameView A] MsBetweenPresents: {describe(present_stats)}")
    p(f"  [FrameView B] MsBetweenDisplayChange (non-dropped): {describe(display_stats)}")
    p("Side by side (low1% FPS):")
    p(f"  DevOverlay present={describe(devoverlay.get('production'))}")
    p(f"  DevOverlay display={describe(devoverlay.get('display'))}")
    p(f"  FrameView  present={describe(present_stats)}")
    p(f"  FrameView  display={describe(display_stats)}")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("capture")
    parser.add_argument("--frameview")
    parser.add_argument("--frameview-process")
    parser.add_argument("--frameview-last-seconds", type=float)
    parser.add_argument("--out")
    args = parser.parse_args(argv)
    buffer = io.StringIO()
    result = analyze(read_capture(args.capture), buffer)
    if args.frameview:
        analyze_frameview(args.frameview, args.frameview_process, args.frameview_last_seconds, result, buffer)
    text = buffer.getvalue()
    if args.out:
        with open(args.out, "w", encoding="utf-8") as handle:
            handle.write(text)
    sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
