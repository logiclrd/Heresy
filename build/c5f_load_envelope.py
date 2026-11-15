#!/usr/bin/env python3
"""C5(f) manual trial marks + objective analysis of Heresy SDL qualification CSV.

Standard library only. No audio-device access or modification of the Heresy
process. A "no new underruns" row is not, on its own, a listening pass or a
portable real-time guarantee.
"""

from __future__ import annotations

import argparse
import csv
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
import sys
import time

REQUIRED = (
    "utc", "session_id", "record", "sample_rate", "channels",
    "ring_capacity_frames", "producer_quantum_frames", "buffered_frames",
    "underruns", "producer_blocks", "producer_frames", "producer_over_budget",
    "producer_elapsed_ms", "worker_multi_completed", "worker_multi_cancelled",
    "worker_multi_prepared_children", "worker_queue_peak", "callback_calls",
    "callback_over_nominal_request_duration", "fault_type",
)
COUNTERS = (
    "underruns", "producer_blocks", "producer_frames", "producer_over_budget",
    "producer_elapsed_ms", "worker_multi_completed", "worker_multi_cancelled",
    "worker_multi_prepared_children", "callback_calls",
    "callback_over_nominal_request_duration",
)
MARK_FIELDS = ("utc", "trial", "phase")


class InvalidCapture(ValueError):
    """Invalid CSV or trial markers; do not silently infer measurements."""


def parse_utc(raw: str) -> datetime:
    try:
        dt = datetime.fromisoformat(raw.replace("Z", "+00:00"))
        if dt.tzinfo is None:
            raise ValueError("timezone required")
        return dt.astimezone(timezone.utc)
    except ValueError as exc:
        raise InvalidCapture(f"Invalid offset-aware timestamp: {raw!r}") from exc


@dataclass(frozen=True)
class Sample:
    at: datetime
    session: str
    counters: dict[str, float]
    buffered_frames: int
    queue_peak: int
    sample_rate: int
    channels: int
    ring_frames: int
    quantum_frames: int
    fault: str

    def delta(self, other: "Sample", field: str) -> float:
        if self.session != other.session:
            raise InvalidCapture("Cannot subtract counters across audio sessions")
        difference = self.counters[field] - other.counters[field]
        if difference < -0.0001:
            raise InvalidCapture(f"Counter {field} decreased within session")
        return max(0.0, difference)


@dataclass(frozen=True)
class Trial:
    name: str
    start: datetime
    end: datetime


def load_capture(path: Path) -> dict[str, list[Sample]]:
    result: dict[str, list[Sample]] = {}
    with path.open(newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        missing = set(REQUIRED).difference(reader.fieldnames or ())
        if missing:
            raise InvalidCapture(f"CSV is missing fields: {', '.join(sorted(missing))}")
        for line, row in enumerate(reader, 2):
            try:
                values = {key: float(row[key]) if key == "producer_elapsed_ms"
                          else float(int(row[key])) for key in COUNTERS}
                sample = Sample(
                    at=parse_utc(row["utc"]), session=row["session_id"],
                    counters=values, buffered_frames=int(row["buffered_frames"]),
                    queue_peak=int(row["worker_queue_peak"]),
                    sample_rate=int(row["sample_rate"]),
                    channels=int(row["channels"]),
                    ring_frames=int(row["ring_capacity_frames"]),
                    quantum_frames=int(row["producer_quantum_frames"]),
                    fault=row["fault_type"].strip(),
                )
            except (ValueError, TypeError, KeyError) as exc:
                raise InvalidCapture(f"Bad CSV row {line}: {exc}") from exc
            if not sample.session:
                raise InvalidCapture(f"Missing session ID on row {line}")
            rows = result.setdefault(sample.session, [])
            if rows and sample.at < rows[-1].at:
                raise InvalidCapture(f"Unsorted timestamp in session {sample.session}")
            if rows:
                for field in COUNTERS:
                    sample.delta(rows[-1], field)
            rows.append(sample)
    if not result:
        raise InvalidCapture("No audio-session samples in CSV")
    return result


def load_trials(path: Path) -> list[Trial]:
    completed: list[Trial] = []
    pending: tuple[str, datetime] | None = None
    with path.open(newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        if set(reader.fieldnames or ()) != set(MARK_FIELDS):
            raise InvalidCapture("Marker CSV requires utc,trial,phase columns")
        for row in reader:
            name = row["trial"].strip()
            phase = row["phase"].strip()
            dt = parse_utc(row["utc"])
            if not name:
                raise InvalidCapture("Trial name cannot be empty")
            if phase == "start":
                if pending is not None:
                    raise InvalidCapture(f"Trial {pending[0]} has no end marker")
                pending = (name, dt)
            elif phase in ("end", "abort"):
                if pending is None or pending[0] != name or dt <= pending[1]:
                    raise InvalidCapture(f"Unpaired {phase} marker for {name}")
                if phase == "end":
                    completed.append(Trial(name, pending[1], dt))
                pending = None
            else:
                raise InvalidCapture(f"Unknown trial marker phase {phase!r}")
    if pending is not None:
        raise InvalidCapture(f"Trial {pending[0]} is missing an end marker")
    if not completed:
        raise InvalidCapture("No completed trial intervals in marker CSV")
    return completed


def append_mark(path: Path, trial: str, phase: str, now: datetime | None = None) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    write_header = not path.is_file() or path.stat().st_size == 0
    with path.open("a", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        if write_header:
            writer.writerow(MARK_FIELDS)
        writer.writerow(((now or datetime.now(timezone.utc)).isoformat(), trial, phase))
        handle.flush()


def summarize_window(name: str, samples: list[Sample]) -> dict[str, object]:
    if len(samples) < 3:
        raise InvalidCapture(f"{name}: fewer than 3 audio samples within interval")
    first, last = samples[0], samples[-1]
    dt_seconds = (last.at - first.at).total_seconds()
    if dt_seconds <= 0:
        raise InvalidCapture(f"{name}: no elapsed sample time")
    delta = {field: last.delta(first, field) for field in COUNTERS}
    blocks = int(delta["producer_blocks"])
    over = int(delta["producer_over_budget"])
    if over > blocks:
        raise InvalidCapture("More over-budget blocks than produced blocks")
    frames = int(delta["producer_frames"])
    ms_per_block = delta["producer_elapsed_ms"] / blocks if blocks else None
    return {
        "trial": name, "session": first.session[:8],
        "sample_seconds": dt_seconds,
        "underruns": int(delta["underruns"]),
        "blocks": blocks, "over": over,
        "over_percent": 100.0 * over / blocks if blocks else 0.0,
        "frames": frames, "ms_per_block": ms_per_block,
        "min_buffered": min(x.buffered_frames for x in samples),
        "max_queue": max(x.queue_peak for x in samples),
        "worker_prepared": int(delta["worker_multi_prepared_children"]),
        "worker_completed": int(delta["worker_multi_completed"]),
        "callback_nominal_over": int(delta["callback_over_nominal_request_duration"]),
        "fault": any(bool(x.fault) for x in samples),
        "format": f"{first.sample_rate}Hz/{first.channels}ch; ring={first.ring_frames}; quantum={first.quantum_frames}",
    }


def rows_for_trial(sessions: dict[str, list[Sample]], trial: Trial) -> list[Sample]:
    matching = [samples for samples in sessions.values() if samples and
                samples[0].at <= trial.start and samples[-1].at >= trial.end]
    if len(matching) != 1:
        raise InvalidCapture(f"{trial.name}: interval does not fit exactly one complete SDL session; "
                             "ensure Heresy was playing throughout this trial")
    return [x for x in matching[0] if trial.start <= x.at <= trial.end]


def format_table(records: list[dict[str, object]]) -> str:
    columns = ("trial", "duration", "new_underruns", "over_budget", "mean_pcm_ms",
               "min_ring", "prepared", "fault")
    lines = ["| " + " | ".join(columns) + " |", "|" + "|".join(["---"] * len(columns)) + "|"]
    for r in records:
        mean = r["ms_per_block"]
        values = [
            str(r["trial"]), f"{r['sample_seconds']:.1f}s", str(r["underruns"]),
            f"{r['over']}/{r['blocks']} ({r['over_percent']:.1f}%)",
            "n/a" if mean is None else f"{mean:.3f}", str(r["min_buffered"]),
            str(r["worker_prepared"]), "YES" if r["fault"] else "no",
        ]
        lines.append("| " + " | ".join(values) + " |")
    return "\n".join(lines)


def cmd_report(args: argparse.Namespace) -> int:
    sessions = load_capture(args.csv)
    output = ["# C5(f) SDL qualification CSV measurements",
              "", "Cumulative counters are differenced between first/last samples **inside** each window.",
              "The window excludes up to one sample period at either edge; edit times are manually marked.",
              "No audible or hardware-deadline pass is inferred automatically.", ""]
    for sid, samples in sessions.items():
        one = summarize_window(f"session-{sid[:8]}", samples)
        output.append(f"Session {sid[:8]}: {one['format']}, observed {one['sample_seconds']:.1f}s, "
                      f"underruns +{one['underruns']}, overbudget +{one['over']}/{one['blocks']} blocks, "
                      f"worker children +{one['worker_prepared']}, fault={'yes' if one['fault'] else 'no'}.")
    if args.markers:
        trials = load_trials(args.markers)
        rows = [summarize_window(t.name, rows_for_trial(sessions, t)) for t in trials]
        output.extend(["", "## Marked trials", "", format_table(rows), "",
                       "**Interpretation:** A trial with new underruns is NOT real-time-clean. "
                       "A trial with zero new underruns still needs listening confirmation and "
                       "repeat runs before claiming a supported envelope. "
                       "Over-budget PCM blocks are informative even without audible glitches."])
    else:
        output.extend(["", "Pass '--markers <file>' for per-voice-count trials; "
                       "unmarked CSV data cannot prove which specific UI edit caused a workload interval."])
    report = "\n".join(output) + "\n"
    print(report, end="")
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(report, encoding="utf-8")
        print(f"Saved report: {args.output}")
    return 0


def cmd_record(args: argparse.Namespace) -> int:
    print("C5(f) markers only; Heresy must already be running in another window.")
    print("Use Latch mode, commit each change, then come back here. "
          "Press Enter to begin a trial; its measurement starts after the warmup.")
    print(f"Markers: {args.markers} | settle={args.settle}s | measured={args.seconds}s")
    while True:
        try:
            name = input("\nTrial label (e.g. 1x3, 1x5, 3x3, recovery-1x3; blank to finish): ").strip()
            if not name:
                break
            if "\n" in name or "\r" in name:
                raise InvalidCapture("Trial name contains newline")
            input(f"Set {name} in Heresy; once audible/stable, press Enter here: ")
            print(f"Start in {args.settle:g}s; return focus to Heresy if desired ...", flush=True)
            time.sleep(args.settle)
            append_mark(args.markers, name, "start")
            print(f"MEASURING {name} for {args.seconds:g}s; keep the configuration unchanged ...", flush=True)
            try:
                time.sleep(args.seconds)
            except KeyboardInterrupt:
                append_mark(args.markers, name, "abort")
                raise
            append_mark(args.markers, name, "end")
            print(f"Finished {name}. Set the next configuration in Heresy.", flush=True)
        except EOFError:
            break
    print(f"Saved markers: {args.markers}")
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="command", required=True)
    rec = sub.add_parser("record", help="Interactive UTC trial markers while Heresy plays")
    rec.add_argument("--markers", type=Path, required=True)
    rec.add_argument("--settle", type=float, default=3.0, help="seconds after confirmation before sampling")
    rec.add_argument("--seconds", type=float, default=12.0, help="measurement duration")
    report = sub.add_parser("report", help="Summarize per-session and manually marked trial windows")
    report.add_argument("--csv", type=Path, required=True)
    report.add_argument("--markers", type=Path)
    report.add_argument("--output", type=Path)
    args = p.parse_args(argv)
    try:
        if args.command == "record":
            if args.settle < 0 or args.seconds < 2:
                p.error("Settle must be nonnegative and measurements at least 2 seconds")
            return cmd_record(args)
        return cmd_report(args)
    except (InvalidCapture, OSError) as exc:
        print(f"C5(f) report error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
