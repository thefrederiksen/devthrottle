#!/usr/bin/env python3
"""
Dictation capture-health analyzer (issue #863).

Reads the dictation session log that every dictation surface now writes, and prints a
per-surface audio-loss summary plus the worst recordings, so capture loss is readable
without dev tools and the local-stall versus upstream-under-delivery signal is visible.

Default log location (Windows):
    %LOCALAPPDATA%\\cc-director\\dictation\\sessions\\<date>.jsonl

How loss is measured, per surface (Source field):
    desktop-speak / endpoint : raw PCM16 at 24 kHz mono = 48000 bytes/sec.
                               deficit = 1 - AudioBytesReceived / ExpectedAudioBytes
    mobile                   : compressed audio, no fixed byte rate.
                               deficit = 1 - DecodedAudioSeconds*1000 / RecordedWallMs

Mechanism hint (desktop / endpoint only):
    large handler self-time  -> local capture-thread stall (a local fix helps)
    large callback gap       -> buffers arrived late / not at all (points upstream,
                                e.g. the Remote Desktop audio-redirection channel)

Usage:
    python scripts/dictation-capture-health.py
    python scripts/dictation-capture-health.py --days 1 --worst 15
    python scripts/dictation-capture-health.py --file C:/path/to/2026-06-30.jsonl
"""

import argparse
import json
import os
import sys
from glob import glob


# A 24 kHz, 16-bit, mono PCM stream is 48000 bytes per second. Both raw-PCM surfaces
# (desktop NAudio and the browser AudioWorklet overlay) capture at this rate.
PCM_BYTES_PER_SEC = 48000

# Thresholds for the mechanism hint. The default NAudio headroom is 3 buffers of 50 ms
# (150 ms); a gap beyond that means the driver could have run dry. A handler body that
# takes longer than one buffer interval (50 ms) for cheap work means the thread stalled.
GAP_STALL_MS = 150.0
HANDLER_STALL_MS = 50.0


def default_sessions_dir():
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        return ""
    return os.path.join(local, "cc-director", "dictation", "sessions")


def deficit_for(rec):
    """Return (deficit_fraction, detail_string) or (None, reason) when not computable."""
    source = rec.get("Source") or "unknown"

    if source == "mobile":
        recorded_ms = float(rec.get("RecordedWallMs") or 0)
        decoded_s = float(rec.get("DecodedAudioSeconds") or 0)
        if recorded_ms <= 0:
            return None, "no RecordedWallMs"
        deficit = max(0.0, 1.0 - (decoded_s * 1000.0) / recorded_ms)
        return deficit, "recorded=%.0fms decoded=%.2fs" % (recorded_ms, decoded_s)

    # desktop-speak, endpoint, and any other raw-PCM source.
    received = float(rec.get("AudioBytesReceived") or 0)
    expected = float(rec.get("ExpectedAudioBytes") or 0)
    if expected <= 0:
        # Legacy record without the expected-bytes field: derive it from the recording
        # duration at the known PCM rate (this is how the original loss was first found).
        dur_ms = float(rec.get("RecordingDurationMs") or 0)
        expected = PCM_BYTES_PER_SEC * dur_ms / 1000.0
    if expected <= 0 or received <= 0:
        return None, "no audio / no duration"
    deficit = max(0.0, 1.0 - received / expected)
    return deficit, "recv=%d exp=%d bytes" % (int(received), int(expected))


def mechanism_hint(rec):
    """A short A-vs-B hint for the raw-PCM surfaces; empty for mobile / when not informative."""
    if (rec.get("Source") or "") == "mobile":
        return ""
    handler = float(rec.get("MaxCaptureHandlerMs") or 0)
    gap = float(rec.get("MaxCaptureCallbackGapMs") or 0)
    if handler >= HANDLER_STALL_MS:
        return "local-stall(handler=%.0fms)" % handler
    if gap >= GAP_STALL_MS:
        return "upstream(gap=%.0fms)" % gap
    return ""


def percentile(sorted_vals, pct):
    if not sorted_vals:
        return 0.0
    if len(sorted_vals) == 1:
        return sorted_vals[0]
    rank = (pct / 100.0) * (len(sorted_vals) - 1)
    lo = int(rank)
    hi = min(lo + 1, len(sorted_vals) - 1)
    frac = rank - lo
    return sorted_vals[lo] * (1 - frac) + sorted_vals[hi] * frac


def load_records(files):
    records = []
    for path in files:
        try:
            with open(path, "r", encoding="utf-8") as fh:
                for line in fh:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        records.append(json.loads(line))
                    except json.JSONDecodeError:
                        continue
        except OSError as ex:
            print("WARNING: could not read %s: %s" % (path, ex))
    return records


def resolve_files(args):
    if args.file:
        return [args.file]
    directory = args.dir or default_sessions_dir()
    if not directory or not os.path.isdir(directory):
        print("ERROR: sessions directory not found: %s" % directory)
        print("Pass --dir <path> or --file <path/to/file.jsonl>.")
        sys.exit(2)
    files = sorted(glob(os.path.join(directory, "*.jsonl")))
    if args.days and args.days > 0:
        files = files[-args.days:]
    return files


def main():
    parser = argparse.ArgumentParser(description="Per-surface dictation capture-health summary (issue #863).")
    parser.add_argument("--dir", help="sessions directory (default: %%LOCALAPPDATA%%/cc-director/dictation/sessions)")
    parser.add_argument("--file", help="analyze a single .jsonl file instead of the directory")
    parser.add_argument("--days", type=int, default=0, help="only the most recent N day-files (0 = all)")
    parser.add_argument("--worst", type=int, default=10, help="how many worst recordings to list (default 10)")
    parser.add_argument("--threshold", type=float, default=0.10, help="deficit fraction counted as a loss (default 0.10)")
    args = parser.parse_args()

    files = resolve_files(args)
    records = load_records(files)

    print("=" * 78)
    print("Dictation capture-health   files=%d   records=%d   loss-threshold=%.0f%%"
          % (len(files), len(records), args.threshold * 100))
    print("=" * 78)

    by_source = {}
    worst = []
    for rec in records:
        source = rec.get("Source") or "unknown"
        deficit, detail = deficit_for(rec)
        bucket = by_source.setdefault(source, {"total": 0, "measured": [], "over": 0})
        bucket["total"] += 1
        if deficit is None:
            continue
        bucket["measured"].append(deficit)
        if deficit >= args.threshold:
            bucket["over"] += 1
        worst.append((deficit, rec, detail))

    if not records:
        print("No records found.")
        return

    print()
    print("%-14s %7s %9s %9s %9s %9s %9s" % ("surface", "records", "measured", "mean", "median", "p90", ">thresh"))
    print("-" * 78)
    for source in sorted(by_source):
        b = by_source[source]
        vals = sorted(b["measured"])
        if vals:
            mean = sum(vals) / len(vals)
            print("%-14s %7d %9d %8.1f%% %8.1f%% %8.1f%% %9d"
                  % (source, b["total"], len(vals), mean * 100,
                     percentile(vals, 50) * 100, percentile(vals, 90) * 100, b["over"]))
        else:
            print("%-14s %7d %9d %9s %9s %9s %9d"
                  % (source, b["total"], 0, "-", "-", "-", 0))

    worst.sort(key=lambda t: t[0], reverse=True)
    print()
    print("Worst %d recordings by deficit:" % min(args.worst, len(worst)))
    print("-" * 78)
    if not worst:
        print("(nothing measurable)")
    for deficit, rec, detail in worst[: args.worst]:
        ts = (rec.get("TimestampUtc") or "")[:19]
        source = rec.get("Source") or "unknown"
        hint = mechanism_hint(rec)
        raw = rec.get("RawTranscript") or rec.get("CleanedTranscript") or ""
        tail = raw[-44:].replace("\n", " ")
        print("%-19s %-13s deficit=%5.1f%%  %-24s %s"
              % (ts, source, deficit * 100, detail, hint))
        if tail:
            print("    ...ends: %r" % tail)

    print()
    print("Hint: a deficit with a local-stall(handler=...) tag is fixable locally (decouple the")
    print("capture-thread work / add buffers); an upstream(gap=...) tag with a low handler time")
    print("means the audio was under-delivered before capture (e.g. Remote Desktop redirection).")


if __name__ == "__main__":
    main()
