"""Pure standard-library regression checks for C5(f) qualification analyzer."""

import csv
from datetime import datetime, timedelta, timezone
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "c5f_load_envelope", Path(__file__).with_name("c5f_load_envelope.py"))
module = importlib.util.module_from_spec(spec)
import sys
sys.modules[spec.name] = module
spec.loader.exec_module(module)


def stamp(n):
    return (datetime(2026, 11, 15, 18, tzinfo=timezone.utc)
            + timedelta(seconds=n)).isoformat()


class AnalyzerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        base = Path(self.temp.name)
        self.csv = base / "audio.csv"
        self.markers = base / "markers.csv"
        fields = list(module.REQUIRED)
        with self.csv.open("w", newline="") as fp:
            w = csv.DictWriter(fp, fieldnames=fields)
            w.writeheader()
            for i in range(28):
                # First trial: no new underruns or PCM budget misses;
                # second: sample-limited overload and worker expansion.
                n = max(0, i - 12)
                w.writerow({
                    "utc": stamp(i), "session_id": "s1", "record": "sample",
                    "sample_rate": 48000, "channels": 2,
                    "ring_capacity_frames": 2048, "producer_quantum_frames": 256,
                    "buffered_frames": 1024 if i < 14 else 0,
                    "underruns": 3 * n, "producer_blocks": 40 * i,
                    "producer_frames": 10240 * i,
                    "producer_over_budget": 9 * n,
                    "producer_elapsed_ms": f"{(40 * i * 2.1 + n * 200):.3f}",
                    "worker_multi_completed": min(1, n),
                    "worker_multi_cancelled": 0,
                    "worker_multi_prepared_children": min(4, 4 * n),
                    "worker_queue_peak": min(n, 1), "callback_calls": 20 * i,
                    "callback_over_nominal_request_duration": 0,
                    "fault_type": "",
                })
        for label, start, end in [("one-3", 1.3, 10.3), ("one-9", 14.3, 24.3)]:
            module.append_mark(self.markers, label, "start", module.parse_utc(stamp(start)))
            module.append_mark(self.markers, label, "end", module.parse_utc(stamp(end)))

    def test_first_and_second_trials_are_disjoint_and_truthful(self):
        sessions = module.load_capture(self.csv)
        marks = module.load_trials(self.markers)
        first = module.summarize_window(marks[0].name, module.rows_for_trial(sessions, marks[0]))
        second = module.summarize_window(marks[1].name, module.rows_for_trial(sessions, marks[1]))
        self.assertEqual(first["underruns"], 0)
        self.assertEqual(first["over"], 0)
        self.assertEqual(first["blocks"], 320)
        self.assertGreater(second["underruns"], 0)
        self.assertGreater(second["over_percent"], 0)
        self.assertEqual(second["min_buffered"], 0)
        self.assertGreaterEqual(second["worker_prepared"], 0)
        table = module.format_table([first, second])
        self.assertIn("one-3", table)
        self.assertIn("one-9", table)

    def test_real_capture_schema_and_counter_deltas(self):
        sessions = module.load_capture(self.csv)
        self.assertEqual(len(sessions), 1)
        row = module.summarize_window("session", sessions["s1"])
        self.assertEqual(row["underruns"], 45)
        self.assertEqual(row["over"], 135)
        self.assertEqual(row["blocks"], 1080)

    def test_reject_cross_session_markers(self):
        data = module.load_capture(self.csv)
        crossing = module.Trial("outside", module.parse_utc(stamp(-1)), module.parse_utc(stamp(3)))
        with self.assertRaisesRegex(module.InvalidCapture, "exactly one"):
            module.rows_for_trial(data, crossing)

    def test_reject_broken_markers(self):
        module.append_mark(self.markers, "incomplete", "start", module.parse_utc(stamp(30)))
        with self.assertRaisesRegex(module.InvalidCapture, "missing an end"):
            module.load_trials(self.markers)

    def test_reject_malformed_capture_row(self):
        with self.csv.open("a") as fp:
            fp.write("bad,insufficient\n")
        with self.assertRaises(module.InvalidCapture):
            module.load_capture(self.csv)


if __name__ == "__main__":
    unittest.main()
