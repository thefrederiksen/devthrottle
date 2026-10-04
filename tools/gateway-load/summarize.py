"""Summarize a gateway-load results folder into per-account memory and data-out figures.

Each step folder is named <profile>-<accounts> (or <profile>-today-<accounts>) and holds:
  memory.csv     - the Gateway process's memory every few seconds, no collection forced (an UPPER bound)
  collected.csv  - one sample after a full, compacting collection at the end of the hold, accounts still connected
                   (LIVE memory)
  drive.json     - what the driver did: when it began and ended, phones open, the live sample, errors
  traffic.json   - each account's own traffic meter rows

Memory: the slope of live memory against accounts, per profile, by least squares, is what one more connected
account costs; the median of the regular samples over the second half of the hold is shown beside it as the upper
bound. Data out: each account's metered bytes over the whole drive (connecting included, because the meter counts
it), scaled to a day; phone bytes are charged per OPEN phone times the profile's share of the day the phone is
open, because a whole number of phones cannot match the share at every step.

A step whose driver saw any failed read or push, or whose live sample is missing, is refused: its load was not
the profile's.

Usage: python summarize.py <results-folder>
"""
import csv
import json
import re
import statistics
import sys
from datetime import datetime
from pathlib import Path


def parse_utc(text):
    """.NET writes seven fractional digits ("o" format); Python reads at most six, so the seventh is dropped."""
    text = re.sub(r"(\.\d{6})\d+", lambda m: m.group(1), text.strip()).replace("Z", "+00:00")
    return datetime.fromisoformat(text)


def read_csv(path):
    rows = list(csv.DictReader(open(path, encoding="utf-8")))
    if not rows:
        raise SystemExit(f"ERROR: {path} has no samples")
    return rows


def upper_memory(step_dir, connected):
    rows = [r for r in read_csv(step_dir / "memory.csv") if connected is None or parse_utc(r["utc"]) >= connected]
    rows = rows[len(rows) // 2:]
    if not rows:
        raise SystemExit(f"ERROR: {step_dir} has no samples after the accounts connected")
    return statistics.median(float(r["working_set_mb"]) for r in rows), statistics.median(float(r["gc_heap_mb"]) for r in rows)


def live_memory(step_dir):
    path = step_dir / "collected.csv"
    if not path.exists():
        raise SystemExit(f"ERROR: {step_dir} has no live-memory sample (collected.csv); the step did not finish its hold")
    row = read_csv(path)[0]
    return float(row["working_set_mb"]), float(row["gc_heap_mb"])


def traffic_per_account_day(step_dir, run):
    """Mean bytes per account per day, and the largest routes, from each account's own meter rows."""
    data = json.load(open(step_dir / "traffic.json", encoding="utf-8"))
    hours = (parse_utc(run["EndedUtc"]) - parse_utc(run["BegunUtc"])).total_seconds() / 3600
    accounts = run["Accounts"]
    phones, share = run["PhonesOpen"], run["PhoneOpenShare"]
    other_total, phone_total, routes = 0, 0, {}
    for snapshot in data.values():
        for hour in snapshot["hours"]:
            for row in hour["rows"]:
                if row["caller"] == "unauthenticated":
                    continue  # every reader sees these rows; never charge them to one account
                key = f'{row["route"]} [{row["caller"]}]'
                if row["caller"] == "device:phone":
                    phone_total += row["bytes"]
                    per_account = row["bytes"] / phones * share if phones else 0
                else:
                    other_total += row["bytes"]
                    per_account = row["bytes"] / accounts
                routes[key] = routes.get(key, 0) + per_account * 24 / hours
    phone_part = phone_total / phones * share if phones else 0
    per_day = (other_total / accounts + phone_part) * 24 / hours
    return per_day, sorted(routes.items(), key=lambda kv: -kv[1])[:8]


def slope(points):
    n = len(points)
    if n < 2:
        return float("nan")
    mx = sum(x for x, _ in points) / n
    my = sum(y for _, y in points) / n
    den = sum((x - mx) ** 2 for x, _ in points)
    return sum((x - mx) * (y - my) for x, y in points) / den if den else float("nan")


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    root = Path(sys.argv[1])
    groups = {}
    for step_dir in sorted(p for p in root.iterdir() if p.is_dir()):
        match = re.fullmatch(r"(.+)-(\d+)", step_dir.name)
        if not match:
            continue
        profile, n = match.group(1), int(match.group(2))
        run = None
        if n > 0:
            run_file = step_dir / "drive.json"
            if not run_file.exists():
                raise SystemExit(f"ERROR: step {step_dir.name} has no drive.json; the driver did not finish")
            run = json.load(open(run_file, encoding="utf-8"))
            if run["Errors"]:
                raise SystemExit(f"ERROR: step {step_dir.name} had {run['Errors']} failed read(s) or push(es); "
                                 "its load was lighter than the profile - rerun it")
        connected = parse_utc(run["ConnectedUtc"]) if run else None
        up_ws, up_heap = upper_memory(step_dir, connected)
        live_ws, live_heap = live_memory(step_dir)
        traffic = traffic_per_account_day(step_dir, run) if run else None
        groups.setdefault(profile, []).append((n, live_ws, live_heap, up_ws, up_heap, traffic))

    for profile, steps in groups.items():
        steps.sort(key=lambda s: s[0])
        print(f"== {profile}")
        print(f"{'accounts':>9}{'live WS MB':>12}{'live heap MB':>14}{'upper WS MB':>13}{'upper heap MB':>15}{'MB out/account/day':>20}")
        for n, lws, lheap, uws, uheap, traffic in steps:
            out = f"{traffic[0] / 1048576:.1f}" if traffic else "-"
            print(f"{n:>9}{lws:>12.1f}{lheap:>14.1f}{uws:>13.1f}{uheap:>15.1f}{out:>20}")
        connected_steps = [s for s in steps if s[0] > 0]
        print(f"live memory per account, all steps:          working set {slope([(s[0], s[1]) for s in steps]):.2f} MB, "
              f"heap {slope([(s[0], s[2]) for s in steps]):.2f} MB")
        print(f"live memory per account, connected steps only: working set {slope([(s[0], s[1]) for s in connected_steps]):.2f} MB, "
              f"heap {slope([(s[0], s[2]) for s in connected_steps]):.2f} MB")
        biggest = max(connected_steps, key=lambda s: s[0], default=None)
        if biggest:
            print(f"top routes per account per day at {biggest[0]} accounts:")
            for route, b in biggest[5][1]:
                print(f"  {b / 1048576:9.2f} MB  {route}")
        print()


if __name__ == "__main__":
    main()
