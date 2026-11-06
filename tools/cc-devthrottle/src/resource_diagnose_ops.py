"""`cc-devthrottle machine diagnose`: why is this machine slow, now or at a time that has passed.

It reads the Director's 24-hour local recording (the Resource Monitor mission, issue #2811) - every
five-second sample, written by `ResourceRecorder` in `src/CcDirector.Core/Machine/` - straight off this
machine's disk. It needs no Gateway and no running Director, so "what made it slow at 08:26?" can be
answered after the slowdown has passed, and an agent asked "why is this machine slow?" runs one
command instead of improvising shell commands.

THIS COMMAND RULES NOTHING. The verdict (Normal, Tight, Critical) and the diagnosis ("Slow because of
the disk ...") were worked out by the Director, in Core, when the sample was taken, and stored in the
recording; this prints them verbatim (CLAUDE.md rule 7). When the Director stored none, it says so - it
never works one out from the figures, because a second copy of the rules in a second language is how
two screens come to disagree. What it DOES compute is aggregation of what was recorded: a minute's
average and peak, and a minute's worst recorded level (the same order as `ResourcePressureVerdict.Worst`:
Unknown never beats a real level).

A FIGURE THAT WAS NOT READ IS A DASH, NEVER A ZERO. The recording writes an unreadable figure as null
with its reason; an average is taken over the samples that were read, and a minute with none says `-`.
A session's processor or memory total that left out processes whose figures were refused is printed
"at least", because summing an unknown as zero reports a session cheaper than it is.

THE RECORDING'S OWN TIMELINE, NOT THE WALL CLOCK, DECIDES "THE LAST HOUR". Each line carries
`recordingSeconds`, which advances only with the monotonic clock inside a Director run. `--at` is the
exception, because a person's "08:26" means the wall clock: it selects on the stamp, and when the wall
clock stepped inside the recording it says that a time may appear twice.

The file format is version 1 of `ResourceRecordingFormat`. The committed recording in
`tests/fixtures/resource-recording/` is written byte for byte by the C# recorder's own golden test, so
the two ends cannot drift apart unseen.
"""

from __future__ import annotations

import json
import os
import re
import sys
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Optional

from . import axi_cli, setup_ops, usage_errors
from .axi_cli import axi_output

RECORDING_FOLDER = "resource-recording"
FORMAT_VERSION = 1
_FILE_NAME = re.compile(r"^samples-([0-9]{12})\.jsonl$")

#: A sample is "now" when it is younger than this on the wall clock: the recorder writes once a minute,
#: so a healthy recording's newest line is up to a minute old, and a minute more is allowed for a slow disk.
FRESH_SECONDS = 120

DEFAULT_MINUTES = 60
DEFAULT_MINUTES_AT = 10
MAX_MINUTES = 24 * 60

HISTORY_FIELDS = [
    "minute",
    "samples",
    "cpu-avg",
    "cpu-peak",
    "memory-available-min",
    "swap-used-max",
    "disk-stalled-peak",
    "worst",
    "slow",
    "unreadable",
]
DEFAULT_HISTORY_FIELDS = ["minute", "cpu-avg", "cpu-peak", "memory-available-min", "worst", "slow"]

_LEVEL_ORDER = {"Unknown": 0, "Normal": 1, "Tight": 2, "Critical": 3}
_KIND_WORDS = {"Cpu": "processor", "Memory": "memory", "Swap": "swap"}
_CAUSE_WORDS = {"Cpu": "processor", "Memory": "memory", "Disk": "disk"}

_COMMAND = "cc-devthrottle machine diagnose"


# ---------------------------------------------------------------------------------------------------
# Where the recording is
# ---------------------------------------------------------------------------------------------------


def recording_directory(instance: Optional[str]) -> Path:
    """The recording of this machine's Director.

    Inside a session that is the Director the session runs under: CC_DIRECTOR_ROOT is that Director's
    own folder. `--instance` names another Director on this machine. Outside a session, with one
    Director on the machine, it is that one; with several, the caller must say which.
    """
    override = os.environ.get("CC_DIRECTOR_ROOT")
    if instance is None and override:
        return Path(override) / RECORDING_FOLDER

    machine_root = setup_ops._machine_root(Path(override)) if override else setup_ops._default_root()
    instances_dir = machine_root / "instances"
    names = sorted(p.name for p in instances_dir.iterdir() if p.is_dir()) if instances_dir.is_dir() else []

    if instance is not None:
        if instance not in names:
            listed = ", ".join(names) if names else "none"
            usage_errors.usage_error(
                f"no Director instance is named '{axi_output.escape_ascii(instance)}' on this machine; "
                f"the instances here are: {listed}")
        return instances_dir / instance / RECORDING_FOLDER

    if len(names) == 1:
        return instances_dir / names[0] / RECORDING_FOLDER
    if not names:
        # No Director has ever run here. Point at where the default one would record, and let the
        # empty result say so - an empty recording is an answer, not an error.
        return instances_dir / "default" / RECORDING_FOLDER
    usage_errors.usage_error(
        f"this machine runs several Directors; say which with --instance. The instances here are: {', '.join(names)}")


# ---------------------------------------------------------------------------------------------------
# Reading it
# ---------------------------------------------------------------------------------------------------


@dataclass
class Recording:
    """What was read: the lines a window needs, and what can be said about the rest without reading it."""

    directory: Path
    files: int = 0
    oldest_seq: Optional[int] = None
    newest_seq: Optional[int] = None
    unreadable_lines: int = 0
    newer_version_lines: int = 0
    lines: list[dict[str, Any]] = field(default_factory=list)  # oldest first by seq


def _recording_files(directory: Path) -> list[tuple[int, Path]]:
    if not directory.is_dir():
        return []
    files = []
    for path in directory.iterdir():
        match = _FILE_NAME.match(path.name)
        if match and path.is_file():
            files.append((int(match.group(1)), path))
    files.sort()
    return files


def _parse_line(text: str) -> Optional[dict[str, Any]]:
    """One whole line, or None for a line torn by a crash or a refused write."""
    try:
        line = json.loads(text)
    except ValueError:
        return None
    if not isinstance(line, dict) or not isinstance(line.get("seq"), int) or "recordingSeconds" not in line:
        return None
    return line


def read_recording(directory: Path, *, newest_seconds: Optional[float], read_everything: bool) -> Recording:
    """Read the recording, newest file first.

    `newest_seconds` stops the read once a file starts before that many seconds of recording time
    before the newest line - the window ending now never needs the whole 24 hours. `read_everything`
    reads every file, for a window placed by the wall clock, which can be anywhere in it. Torn lines
    are COUNTED, never skipped silently.
    """
    recording = Recording(directory=directory)
    files = _recording_files(directory)
    recording.files = len(files)
    if not files:
        return recording
    recording.oldest_seq = files[0][0]

    newest_first: list[dict[str, Any]] = []
    newest_recording_seconds: Optional[float] = None
    for _, path in reversed(files):
        file_lines = []
        for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
            if not raw.strip():
                continue
            line = _parse_line(raw)
            if line is None:
                recording.unreadable_lines += 1
                continue
            if line.get("v") != FORMAT_VERSION:
                recording.newer_version_lines += 1
                continue
            file_lines.append(line)
        newest_first.extend(reversed(file_lines))
        if newest_recording_seconds is None and newest_first:
            newest_recording_seconds = newest_first[0]["recordingSeconds"]
        if (
            not read_everything
            and newest_seconds is not None
            and newest_recording_seconds is not None
            and file_lines
            and file_lines[0]["recordingSeconds"] < newest_recording_seconds - newest_seconds
        ):
            break

    newest_first.sort(key=lambda line: line["seq"])
    recording.lines = newest_first
    if newest_first:
        recording.newest_seq = newest_first[-1]["seq"]
    return recording


def _stamp(line: dict[str, Any]) -> datetime:
    return datetime.strptime(line["takenAtUtc"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)


# ---------------------------------------------------------------------------------------------------
# The window
# ---------------------------------------------------------------------------------------------------


def parse_at(text: str, now_local: datetime) -> datetime:
    """`--at`: a clock time on this machine (`08:26`, the most recent one that has passed) or an ISO
    time with its offset (`2026-11-06T08:26:00Z`). Returns UTC."""
    hhmm = re.fullmatch(r"([01]?[0-9]|2[0-3]):([0-5][0-9])", text.strip())
    if hhmm:
        candidate = now_local.replace(hour=int(hhmm.group(1)), minute=int(hhmm.group(2)), second=0, microsecond=0)
        if candidate > now_local:
            candidate -= timedelta(days=1)
        return candidate.astimezone(timezone.utc)
    try:
        parsed = datetime.fromisoformat(text.strip().replace("Z", "+00:00"))
    except ValueError:
        usage_errors.usage_error(
            f"--at takes a clock time such as 08:26 or an ISO time such as 2026-11-06T08:26:00Z, "
            f"not '{axi_output.escape_ascii(text)}'")
    if parsed.tzinfo is None:
        usage_errors.usage_error("--at needs a time zone when it is a full date and time, for example 2026-11-06T08:26:00Z")
    return parsed.astimezone(timezone.utc)


def select_window(lines: list[dict[str, Any]], minutes: int, at_utc: Optional[datetime]) -> list[dict[str, Any]]:
    """The lines in the window, oldest first. Without `--at`: the last `minutes` of recording time.
    With it: `minutes` centred on that wall-clock time."""
    if not lines:
        return []
    if at_utc is None:
        start = lines[-1]["recordingSeconds"] - minutes * 60
        return [line for line in lines if line["recordingSeconds"] > start]
    half = timedelta(minutes=minutes / 2)
    return [line for line in lines if at_utc - half <= _stamp(line) <= at_utc + half]


def focus_line(lines: list[dict[str, Any]], window: list[dict[str, Any]], at_utc: Optional[datetime]) -> Optional[dict[str, Any]]:
    """The one sample the verdict is read from: the newest, or the one nearest `--at`."""
    if at_utc is None:
        return lines[-1] if lines else None
    if not window:
        return None
    return min(window, key=lambda line: (abs((_stamp(line) - at_utc).total_seconds()), -line["seq"]))


# ---------------------------------------------------------------------------------------------------
# Minutes: aggregation of what was recorded, and nothing more
# ---------------------------------------------------------------------------------------------------


def _readable(part: Optional[dict[str, Any]], key: str) -> Optional[float]:
    if not part or not part.get("couldRead"):
        return None
    value = part.get(key)
    return value if isinstance(value, (int, float)) else None


def _worst_recorded(lines: list[dict[str, Any]]) -> Optional[str]:
    levels = [line["verdict"]["worst"] for line in lines if line.get("verdict")]
    if not levels:
        return None
    return max(levels, key=lambda level: _LEVEL_ORDER.get(level, 0))


def _slow_recorded(lines: list[dict[str, Any]]) -> Optional[str]:
    diagnoses = [line["diagnosis"] for line in lines if line.get("diagnosis")]
    if not diagnoses:
        return None
    causes: list[str] = []
    for diagnosis in diagnoses:
        if diagnosis.get("isSlow"):
            for cause in diagnosis.get("causes", []):
                if cause not in causes:
                    causes.append(cause)
    if causes:
        return "+".join(_CAUSE_WORDS.get(cause, cause.lower()) for cause in causes)
    if any(not diagnosis.get("couldDiagnose") for diagnosis in diagnoses):
        return "not known"
    return "no"


def minutes_of(window: list[dict[str, Any]], local_zone: Any) -> list[dict[str, Any]]:
    """One row per wall-clock minute, in recording order. A minute label can repeat when the wall clock
    stepped back; the rows are kept apart rather than merged, so two different moments are never averaged
    into one."""
    groups: list[list[dict[str, Any]]] = []
    labels: list[str] = []
    for line in window:
        label = _stamp(line).astimezone(local_zone).strftime("%Y-%m-%dT%H:%M")
        starts_new = (
            not groups
            or labels[-1] != label
            or line.get("runStart")
            or line.get("clockStepSeconds") is not None
        )
        if starts_new:
            groups.append([line])
            labels.append(label)
        else:
            groups[-1].append(line)

    rows = []
    for label, lines in zip(labels, groups):
        cpu = [v for v in (_readable(line.get("cpu"), "totalBusyPercent") for line in lines) if v is not None]
        available = [v for v in (_readable(line.get("memory"), "availableBytes") for line in lines) if v is not None]
        swap = [v for v in (_readable(line.get("swap"), "usedBytes") for line in lines) if v is not None]
        stalled = [v for v in (_readable(line.get("stall"), "ioStalledPercent") for line in lines) if v is not None]
        unreadable = sum(
            1 for line in lines
            if _readable(line.get("cpu"), "totalBusyPercent") is None
            or _readable(line.get("memory"), "availableBytes") is None)
        rows.append({
            "minute": label,
            "firstSeq": lines[0]["seq"],
            "samples": len(lines),
            "cpuAvgPercent": round(sum(cpu) / len(cpu), 1) if cpu else None,
            "cpuPeakPercent": max(cpu) if cpu else None,
            "cpuSamplesRead": len(cpu),
            "memoryAvailableMinBytes": int(min(available)) if available else None,
            "swapUsedMaxBytes": int(max(swap)) if swap else None,
            "diskStalledPeakPercent": max(stalled) if stalled else None,
            "worst": _worst_recorded(lines),
            "slow": _slow_recorded(lines),
            "unreadable": unreadable,
            "runStart": bool(lines[0].get("runStart")),
            "clockStepSeconds": lines[0].get("clockStepSeconds"),
        })
    return rows


# ---------------------------------------------------------------------------------------------------
# Words
# ---------------------------------------------------------------------------------------------------


def _gb(value: Optional[float]) -> Optional[str]:
    """Bytes in words. Below a gigabyte in megabytes, so 14 MB is never printed as "0.0 GB" - a made-up zero."""
    if value is None:
        return None
    if value < 1024 ** 3:
        return f"{value / 1024 ** 2:.0f} MB"
    return f"{value / 1024 ** 3:.1f} GB"


def _percent(value: Optional[float]) -> Optional[str]:
    return None if value is None else f"{value:.0f}%"


def _dash(value: Optional[str]) -> str:
    return "-" if value is None else value


def _age_words(seconds: float) -> str:
    seconds = int(abs(seconds))
    if seconds < 120:
        return f"{seconds} seconds"
    if seconds < 7200:
        return f"{seconds // 60} minutes"
    return f"{seconds // 3600} hours {seconds % 3600 // 60} minutes"


def _session_cell(process: dict[str, Any]) -> Optional[str]:
    if process.get("sessionId"):
        return process["sessionId"]
    if process.get("formerSessionId"):
        state = "ended" if process.get("formerSessionEnded") else "left"
        return f"{state}:{process['formerSessionId']}"
    return None


def _session_total(value: str, unknown: int, process_count: int) -> str:
    """A session's total, honest about what it left out. When NO process could be read the sum is zero
    over nothing, and "at least 0%" would be the made-up zero in other words - so that is a dash."""
    if unknown >= process_count:
        return f"- (none of {process_count} readable)"
    return f"at least {value} ({unknown} not readable)" if unknown else value


# ---------------------------------------------------------------------------------------------------
# The command
# ---------------------------------------------------------------------------------------------------


def diagnose(
    minutes: Optional[int],
    at: Optional[str],
    instance: Optional[str],
    json_output: bool,
    fields: Optional[str],
    *,
    now_utc: Optional[datetime] = None,
    local_zone: Any = None,
) -> None:
    """The command. `now_utc` and `local_zone` are for tests; the command uses the machine's own."""
    if json_output and fields is not None:
        usage_errors.usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen_fields = usage_errors.parse_fields(fields, HISTORY_FIELDS, DEFAULT_HISTORY_FIELDS)
    if minutes is not None and not 1 <= minutes <= MAX_MINUTES:
        usage_errors.usage_error(f"--minutes must be from 1 to {MAX_MINUTES} (24 hours), not {minutes}")

    now_utc = now_utc or datetime.now(timezone.utc)
    local_zone = local_zone or datetime.now().astimezone().tzinfo
    at_utc = parse_at(at, now_utc.astimezone(local_zone)) if at is not None else None
    window_minutes = minutes or (DEFAULT_MINUTES_AT if at_utc is not None else DEFAULT_MINUTES)

    directory = recording_directory(instance)
    try:
        recording = read_recording(directory, newest_seconds=window_minutes * 60, read_everything=at_utc is not None)
    except OSError as exc:
        axi_cli.fail(f"could not read the recording at {directory}: {exc}", [f"{_COMMAND} --help"])

    window = select_window(recording.lines, window_minutes, at_utc)
    focus = focus_line(recording.lines, window, at_utc)
    rows = minutes_of(window, local_zone)
    newest = recording.lines[-1] if recording.lines else None
    notes = _notes(recording, window, rows, newest, now_utc, at_utc)

    if json_output:
        print(json.dumps(_json_payload(recording, window_minutes, at_utc, window, focus, newest, rows, notes, now_utc), indent=2))
        return
    _write_text(recording, window_minutes, at_utc, window, focus, newest, rows, notes, chosen_fields, now_utc, local_zone)


def _newest_age(newest: Optional[dict[str, Any]], now_utc: datetime) -> Optional[float]:
    return None if newest is None else (now_utc - _stamp(newest)).total_seconds()


def _notes(recording: Recording, window, rows, newest, now_utc: datetime, at_utc) -> list[str]:
    notes = []
    age = _newest_age(newest, now_utc)
    if age is not None and age < 0:
        notes.append(
            f"the newest sample is stamped {_age_words(age)} in the future: this machine's wall clock has moved "
            "backwards since it was taken")
    elif age is not None and age > FRESH_SECONDS:
        notes.append(
            f"the newest sample is {_age_words(age)} old: the Director is not recording now, so this is history, "
            "not the machine now")
    if recording.unreadable_lines:
        notes.append(f"{recording.unreadable_lines} line(s) of the recording were not whole (a crash or a refused write) and were left out")
    if recording.newer_version_lines:
        notes.append(f"{recording.newer_version_lines} line(s) are in a newer recording format than this command reads; update cc-devthrottle")
    lost = sum(line.get("samplesLostBefore", 0) for line in window)
    if lost:
        notes.append(f"{lost} sample(s) in this window were lost unwritten because the disk refused the writes")
    if any(row["runStart"] for row in rows):
        notes.append("the Director started inside this window; there is no sample from while it was stopped")
    if any(line.get("clockWentBackwardsAtRestart") for line in window):
        notes.append("the wall clock went backwards across a Director restart in this window")
    steps = [line for line in window if line.get("clockStepSeconds") is not None]
    if steps:
        notes.append(
            f"the wall clock stepped {len(steps)} time(s) in this window; a clock time can appear twice, and the rows are "
            "kept in the order they were recorded")
    if at_utc is not None and not window and recording.lines:
        notes.append(
            f"nothing was recorded near that time; the recording covers {recording.lines[0]['takenAtUtc']} to "
            f"{recording.lines[-1]['takenAtUtc']}")
    return notes


def _json_payload(recording, window_minutes, at_utc, window, focus, newest, rows, notes, now_utc) -> dict[str, Any]:
    age = _newest_age(newest, now_utc)
    return {
        "recording": {
            "directory": str(recording.directory),
            "files": recording.files,
            "oldestSeq": recording.oldest_seq,
            "newestSeq": recording.newest_seq,
            "unreadableLines": recording.unreadable_lines,
            "newerVersionLines": recording.newer_version_lines,
            "newestTakenAtUtc": newest["takenAtUtc"] if newest else None,
            "newestAgeSeconds": age,
            "recordingNow": age is not None and 0 <= age <= FRESH_SECONDS,
        },
        "window": {
            "minutes": window_minutes,
            "atUtc": at_utc.strftime("%Y-%m-%dT%H:%M:%SZ") if at_utc else None,
            "count": len(window),
        },
        "focus": focus,
        "newest": newest,
        "history": rows,
        "notes": notes,
        "samples": window,
    }


def _write_text(recording, window_minutes, at_utc, window, focus, newest, rows, notes, chosen_fields, now_utc, local_zone) -> None:
    blocks: list[str] = [f"recording: {axi_cli.ascii_text(str(recording.directory))}"]

    if newest is None:
        blocks.append(axi_output.format_count(0))
        blocks.append(
            "no samples are recorded here: this Director has not recorded yet. The recording starts when a Director "
            "with the Resource Monitor runs, and is written once a minute.")
        blocks.append(axi_output.format_help([f"{_COMMAND} --instance <name>", "cc-devthrottle director list"]))
        axi_output.write_blocks(sys.stdout, *blocks)
        return

    age = _newest_age(newest, now_utc)
    state = "the Director is recording" if age is not None and 0 <= age <= FRESH_SECONDS else "the Director is NOT recording now"
    blocks.append(f"newest: {newest['takenAtUtc']}, {_age_words(age)} ago, seq {newest['seq']} ({state})")
    if recording.oldest_seq is not None:
        kept = recording.newest_seq - recording.oldest_seq + 1
        blocks.append(f"kept: {kept} samples in {recording.files} file(s), seq {recording.oldest_seq} to {recording.newest_seq}")

    if focus is not None:
        local = _stamp(focus).astimezone(local_zone).strftime("%Y-%m-%d %H:%M:%S %z")
        which = "the sample nearest --at" if at_utc is not None else "the newest sample"
        blocks.append(f"showing: {which}, {focus['takenAtUtc']} (local {local}), seq {focus['seq']}")
        blocks.extend(_focus_blocks(focus))

    blocks.append(axi_output.format_count(len(window), total=None))
    blocks.append(_window_words(window_minutes, at_utc, len(rows)))
    records = [_history_record(row) for row in rows]
    blocks.append(axi_output.render_list("history", chosen_fields, records))
    if notes:
        blocks.append(axi_output.render_list("notes", ["note"], [{"note": note} for note in notes]))

    blocks.append(axi_output.format_help([
        f"{_COMMAND} --at <HH:MM>",
        f"{_COMMAND} --minutes 1440 --fields minute,cpu-peak,memory-available-min,worst,slow",
        f"{_COMMAND} --json",
    ]))
    axi_output.write_blocks(sys.stdout, *blocks)


def _window_words(window_minutes: int, at_utc: Optional[datetime], row_count: int) -> str:
    where = (
        f"the {window_minutes} minutes around {at_utc.strftime('%Y-%m-%dT%H:%M:%SZ')}" if at_utc is not None
        else f"the last {window_minutes} minutes of recording"
    )
    return f"window: {where}, {row_count} minute row(s), times local"


def _focus_blocks(line: dict[str, Any]) -> list[str]:
    blocks = []
    verdict = line.get("verdict")
    if verdict:
        parts = "; ".join(
            f"{_KIND_WORDS.get(r['kind'], r['kind'])} {r['level']}: {r['evidence']}" for r in verdict.get("resources", []))
        blocks.append(axi_cli.ascii_text(f"verdict: {verdict['worst']} ({parts})"))
    else:
        blocks.append("verdict: none recorded - the Director stored no verdict with this sample")

    diagnosis = line.get("diagnosis")
    if diagnosis:
        blocks.append(axi_cli.ascii_text(f"diagnosis: {diagnosis['summary']}"))
        evidence = diagnosis.get("evidence") or []
        if evidence:
            blocks.append(axi_output.render_list("evidence", ["line"], [{"line": e} for e in evidence]))
        culprit = diagnosis.get("culprit")
        if culprit:
            blocks.append(axi_output.render_list(
                "culprit", ["process", "pid", "session", "session-name", "disk", "disk-kind"],
                [{
                    "process": culprit.get("processName"),
                    "pid": culprit.get("pid"),
                    "session": culprit.get("sessionId"),
                    "session-name": culprit.get("sessionName"),
                    "disk": culprit.get("diskName"),
                    "disk-kind": culprit.get("diskKind"),
                }]))
        blocks.append(axi_cli.ascii_text(f"next step: {diagnosis['nextStep']}"))
    else:
        blocks.append("diagnosis: none recorded - the Director stored no diagnosis with this sample")

    blocks.append(_machine_line(line))

    sessions = line.get("sessions") or []
    blocks.append(axi_output.render_list(
        "sessions", ["id", "name", "cpu", "memory", "processes"],
        [{
            "id": s["sessionId"],
            "name": s.get("name"),
            "cpu": _session_total(f"{s['cpuPercent']:.1f}%", s["processesWithUnknownProcessor"], s["processCount"]),
            "memory": _session_total(_gb(s["memoryBytes"]), s["processesWithUnknownMemory"], s["processCount"]),
            "processes": s["processCount"],
        } for s in sessions]))

    processes = line.get("processes") or {}
    if processes.get("couldRead"):
        listed = processes.get("listed") or []
        blocks.append(
            f"processes: {processes['count']} running, {len(listed)} listed below; processor unknown for "
            f"{processes['withUnknownProcessor']}, memory unknown for {processes['withUnknownMemory']}; "
            f"{processes['outlivedOrLeftSession']} outlived or left their session")
        blocks.append(axi_output.render_list(
            "processes", ["pid", "name", "cpu", "memory", "session"],
            [{
                "pid": p["pid"],
                "name": p["name"],
                "cpu": "-" if p.get("cpuPercent") is None else f"{p['cpuPercent']:.1f}%",
                "memory": _dash(_gb(p.get("memoryBytes"))),
                "session": _session_cell(p),
            } for p in listed]))
    else:
        blocks.append(axi_cli.ascii_text(f"processes: cannot read - {processes.get('unreadableReason')}"))
    return blocks


def _machine_line(line: dict[str, Any]) -> str:
    """The machine's own figures, each with its own dash and reason: they fail independently."""

    def part(name: str, reading: Optional[dict[str, Any]], words) -> str:
        if not reading or not reading.get("couldRead"):
            reason = (reading or {}).get("unreadableReason") or "not recorded"
            return f"{name} - (cannot read: {reason})"
        return f"{name} {words(reading)}"

    return axi_cli.ascii_text("machine: " + ", ".join([
        part("processor", line.get("cpu"), lambda r: f"{_dash(_percent(r.get('totalBusyPercent')))} busy"),
        part("memory", line.get("memory"),
             lambda r: f"{_dash(_gb(r.get('availableBytes')))} available of {_dash(_gb(r.get('totalBytes')))}"),
        part("swap", line.get("swap"), lambda r: f"{_dash(_gb(r.get('usedBytes')))} used of {_dash(_gb(r.get('totalBytes')))}"),
    ]))


def _history_record(row: dict[str, Any]) -> dict[str, Any]:
    minute = row["minute"][11:]  # HH:MM; the date is in the window line and the JSON
    if row["clockStepSeconds"] is not None:
        minute += " (clock stepped)"
    elif row["runStart"]:
        minute += " (Director started)"
    cpu_avg = row["cpuAvgPercent"]
    if cpu_avg is not None and row["cpuSamplesRead"] < row["samples"]:
        cpu_avg_text = f"{cpu_avg:.0f}% ({row['cpuSamplesRead']} of {row['samples']} read)"
    else:
        cpu_avg_text = _dash(_percent(cpu_avg))
    return {
        "minute": minute,
        "samples": row["samples"],
        "cpu-avg": cpu_avg_text,
        "cpu-peak": _dash(_percent(row["cpuPeakPercent"])),
        "memory-available-min": _dash(_gb(row["memoryAvailableMinBytes"])),
        "swap-used-max": _dash(_gb(row["swapUsedMaxBytes"])),
        "disk-stalled-peak": _dash(_percent(row["diskStalledPeakPercent"])),
        "worst": _dash(row["worst"]),
        "slow": _dash(row["slow"]),
        "unreadable": row["unreadable"],
    }
