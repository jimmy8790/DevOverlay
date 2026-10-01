"""Stdlib tests for tools/frame_capture_analysis.py: python -m unittest tools/test_frame_capture_analysis.py"""
import io
import math
import os
import re
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(__file__))
import frame_capture_analysis as fca  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def csharp_header():
    source = open(os.path.join(ROOT, "Metrics", "Windows", "FrameCapture.cs"), encoding="utf-8").read()
    block = source[source.index("internal const string Header ="):]
    block = block[:block.index(";")]
    return "".join(re.findall(r'"([^"]*)"', block))


def capture_text(rows, marks=(), primary=1, schema=1):
    header = csharp_header()
    lines = [f"# DevOverlayFrameCapture SchemaVersion={schema}",
             f"# QpcFrequency=1000 StartQpc=0 EndQpc={max(r[2] for r in rows)} StartLocal=x",
             f"# DurationSeconds=60 DelaySeconds=0 ProcessFilter=(none) PrimaryPid={primary} CompletionReason=Test",
             f"# Process Pid={primary} Name=Game", header]
    columns = header.split(",")
    for pid, chain, qpc, ms, admission, selected, dropped, display in rows:
        cells = dict.fromkeys(columns, "")
        accepted = int(admission == "Accepted" and selected and pid == primary and (schema < 2 or display not in ("", "nan")))
        cells.update(RecordType="frame", Pid=str(pid), SwapChain=str(chain), SelectedChain=str(int(selected)),
                     PresentQpc=str(qpc), MsBetweenPresents=str(ms), Admission=admission,
                     AcceptedForOnePercentLow=str(accepted), Dropped=dropped, MsBetweenDisplayChange=display)
        lines.append(",".join(cells[c] for c in columns))
    for qpc, source in marks:
        cells = dict.fromkeys(columns, "")
        cells.update(RecordType="mark", PresentQpc=str(qpc), MarkSource=source)
        lines.append(",".join(cells[c] for c in columns))
    return "\n".join(lines) + "\n"


class FrameCaptureAnalysisTests(unittest.TestCase):
    def test_header_matches_csharp_writer_and_has_28_columns(self):
        header = csharp_header().split(",")
        self.assertEqual(28, len(header))
        for name in ("AcceptedForOnePercentLow", "InSlowest1Pct", "MsBetweenDisplayChange", "MarkSource", "Admission"):
            self.assertIn(name, header)

    def test_stats_use_ceil_one_percent_mean(self):
        values = [7.0] * 247 + [10.0, 20.0, 30.0]
        result = fca.stats(values)
        self.assertEqual(3, result.slow_count)
        self.assertAlmostEqual(20.0, result.slow_mean_ms)
        self.assertAlmostEqual(50.0, result.low_fps)
        self.assertIsNone(fca.stats([math.nan, 0, -1]))

    def test_analysis_separates_present_and_display_populations(self):
        rows = []
        for index in range(400):
            slow = index % 100 == 50
            rows.append((1, 11, index * 7 + 7, 12.0 if slow else 6.94, "Accepted", True,
                         "1" if slow else "0", "" if slow else "6.94"))
        rows.append((1, 22, 5, 40.0, "Accepted", False, "0", "40"))
        rows.append((2, 33, 9, 100.0, "Accepted", True, "0", ""))
        with tempfile.NamedTemporaryFile("w", suffix=".csv", delete=False, encoding="utf-8") as handle:
            handle.write(capture_text(rows, marks=[(3500, "PresentMonPoll")]))
            path = handle.name
        try:
            out = io.StringIO()
            result = fca.analyze(fca.read_capture(path), out)
        finally:
            os.unlink(path)
        self.assertEqual(400, result["production"].count)
        self.assertAlmostEqual(1000 / 12, result["production"].low_fps)
        self.assertAlmostEqual(1000 / 6.94, result["display"].low_fps)
        self.assertAlmostEqual(1000 / 6.94, result["variants"]["ExcludeUndisplayed"].low_fps)
        text = out.getvalue()
        self.assertIn("DisplayState=Undisplayed(Dropped=1): all 4 (1.00%) slow 4 (100.00%) lift=100.00", text)
        self.assertIn("candidatePids=2 streams=3", text)

    def test_schema_two_production_uses_display_intervals(self):
        rows = []
        for index in range(400):
            slow = index % 100 == 50
            rows.append((1, 11, index * 7 + 7, 12.0 if slow else 6.94, "Accepted", True,
                         "1" if slow else "0", "" if slow else "6.94"))
        with tempfile.NamedTemporaryFile("w", suffix=".csv", delete=False, encoding="utf-8") as handle:
            handle.write(capture_text(rows, schema=2))
            path = handle.name
        try:
            result = fca.analyze(fca.read_capture(path), io.StringIO())
        finally:
            os.unlink(path)
        self.assertEqual("MsBetweenDisplayChange", result["column"])
        self.assertEqual(396, result["production"].count)
        self.assertAlmostEqual(1000 / 6.94, result["production"].low_fps)
        self.assertEqual(400, result["present"].count)
        self.assertAlmostEqual(1000 / 12, result["present"].low_fps)


if __name__ == "__main__":
    unittest.main()
