"""Tests for `cc-devthrottle machine diagnose` (the Resource Monitor mission, issue #2811).

The recording these tests read, `fixtures/resource-recording/samples-000000000000.jsonl`, is written byte
for byte by the C# recorder's own golden test (`ResourceRecordingGoldenTests` in
`src/CcDirector.Core.UnitTests/Machine/`). So these tests read exactly what a Director writes, not a
second hand-made description of it, and a format change breaks one side or the other at once.

The scenario: devlinux on 2026-11-06 from 08:21 to 08:27 UTC. Healthy until 08:23, then a test run in one
session stalls programs on the spinning disk sdc. At 08:25 another session ends and leaves its compiler
server running. The first sample has no processor figure, as every first sample must not.
"""

import json
import shutil
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import axi_output  # noqa: E402
from src import resource_diagnose_ops as ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

FIXTURE = Path(__file__).parent / "fixtures" / "resource-recording" / "samples-000000000000.jsonl"
SESSION_140 = "2ed1adf4-a275-4478-9618-5443ac09427d"
SESSION_140_NAME = "Resource Monitor - Developer - 24-hour local recording and the diagnose command"
SESSION_98 = "9b80c3d1-5e2f-4a77-8c10-7f3a6e1d2b94"
NEWEST = datetime(2026, 11, 6, 8, 26, 55, tzinfo=timezone.utc)


@pytest.fixture
def director(tmp_path, monkeypatch):
    """A Director folder holding the slow-machine recording, as CC_DIRECTOR_ROOT points at it in a session."""
    folder = tmp_path / "instances" / "default"
    recording = folder / "resource-recording"
    recording.mkdir(parents=True)
    shutil.copy(FIXTURE, recording / FIXTURE.name)
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(folder))
    return folder


def invoke(*args):
    return runner.invoke(app, ["machine", "diagnose", *args])


def run_direct(capsys, *, minutes=None, at=None, json_output=False, fields=None, now=NEWEST + timedelta(seconds=30)):
    """The command with a fixed clock and UTC as the local zone, so the output is the same on every machine."""
    ops.diagnose(minutes, at, None, json_output, fields, now_utc=now, local_zone=timezone.utc)
    return capsys.readouterr().out


# ---------------------------------------------------------------------------------------------------
# It is discoverable
# ---------------------------------------------------------------------------------------------------


def test_actions_listsMachineDiagnose_asReadOnly():
    payload = json.loads(runner.invoke(app, ["actions", "--json"]).output)

    action = next(a for a in payload["actions"] if a["id"] == "machine-diagnose")
    assert action["command"].startswith("cc-devthrottle machine diagnose")
    assert action["mutatesState"] is False

    _, rows = axi_output.parse_list(runner.invoke(app, ["actions"]).output, "actions")
    row = next(r for r in rows if r["id"] == "machine-diagnose")
    assert row["command"] == action["command"]
    assert row["changes-state"] == "no"


def test_help_isShortAndNamesEveryFilter():
    result = invoke("--help")

    assert result.exit_code == 0
    for flag in ("--minutes", "--at", "--instance", "--fields", "--json"):
        assert flag in result.output


# ---------------------------------------------------------------------------------------------------
# What it says about a slow machine
# ---------------------------------------------------------------------------------------------------


def test_text_slowMachine_printsTheRecordedDiagnosisCulpritAndVerdictVerbatim(director, capsys):
    out = run_direct(capsys)

    assert "diagnosis: Slow because of the disk: the hard drive sdc is 75% busy at 26 milliseconds per write" in out
    assert "verdict: Normal (processor Normal:" in out
    assert "next step: Stop or move the test run in session 140 off sdc" in out
    _, culprits = axi_output.parse_list(out, "culprit")
    assert culprits == [{
        "process": "dotnet", "pid": "41872", "session": SESSION_140, "session-name": SESSION_140_NAME,
        "disk": "sdc", "disk-kind": "Spinning",
    }]
    assert "(the Director is recording)" in out


def test_text_everyIdAndNameIsReadBackWhole(director, capsys):
    # The recoverability test of docs/axi-standard.md: an agent can act on what it reads.
    out = run_direct(capsys)

    _, sessions = axi_output.parse_list(out, "sessions")
    assert sessions[0]["id"] == SESSION_140
    assert sessions[0]["name"] == SESSION_140_NAME
    _, processes = axi_output.parse_list(out, "processes")
    assert {p["pid"] for p in processes} >= {"41872", "39001", "38810"}


def test_text_childThatOutlivedItsSession_isListedAgainstTheEndedSession(director, capsys):
    out = run_direct(capsys)

    _, processes = axi_output.parse_list(out, "processes")
    compiler = next(p for p in processes if p["pid"] == "39001")
    assert compiler["name"] == "VBCSCompiler"
    assert compiler["session"] == f"ended:{SESSION_98}"
    assert "1 outlived or left their session" in out


def test_text_history_oneRowPerMinute_withTheDiskMinutesMarkedSlow(director, capsys):
    out = run_direct(capsys)

    _, rows = axi_output.parse_list(out, "history")
    assert [r["minute"] for r in rows] == ["08:21 (Director started)", "08:22", "08:23", "08:24", "08:25", "08:26"]
    assert [r["slow"] for r in rows] == ["no", "no", "disk", "disk", "disk", "disk"]
    assert all(r["worst"] == "Normal" for r in rows)
    # The first minute's average is over the samples that were READ, and says so.
    assert rows[0]["cpu-avg"].endswith("(11 of 12 read)")


def test_text_firstSample_processorIsADashWithItsReason_andSessionTotalsSayAtLeast(director, capsys):
    out = run_direct(capsys, at="2026-11-06T08:21:00Z", minutes=1)

    assert "showing: the sample nearest --at, 2026-11-06T08:21:00Z" in out
    assert "processor - (cannot read: first sample, no interval yet)" in out
    assert "memory 27.4 GB available of 39.0 GB" in out  # the other figures still show their own
    assert "verdict: Normal (processor Unknown: cannot read: first sample, no interval yet;" in out
    _, sessions = axi_output.parse_list(out, "sessions")
    # Session 140's agent is the only process in its tree, and its figure is unknown: a dash, not "at least 0%".
    assert sessions[0]["cpu"] == "- (none of 1 readable)"
    assert sessions[0]["memory"] == "488 MB"  # its memory WAS read, so that total is exact
    assert sessions[1]["cpu"] == "- (none of 2 readable)"
    _, processes = axi_output.parse_list(out, "processes")
    assert all(p["cpu"] == "-" for p in processes)
    assert "0.0%" not in out.split("history[")[0]


def test_text_sessionWithSomeProcessorFiguresUnknown_saysAtLeast(director, capsys):
    # 08:23:00: the test run has just started and has no processor figure yet; the agent's is known.
    out = run_direct(capsys, at="2026-11-06T08:23:00Z", minutes=1)

    _, sessions = axi_output.parse_list(out, "sessions")
    session = next(s for s in sessions if s["id"] == SESSION_140)
    assert session["cpu"] == "at least 0.9% (1 not readable)"
    assert session["memory"] == "1.7 GB"


def test_text_fields_choosesTheHistoryColumns(director, capsys):
    out = run_direct(capsys, fields="minute,disk-stalled-peak,unreadable")

    fields, rows = axi_output.parse_list(out, "history")
    assert fields == ["minute", "disk-stalled-peak", "unreadable"]
    assert rows[0]["unreadable"] == "1"


def test_text_staleRecording_saysTheDirectorIsNotRecording(director, capsys):
    out = run_direct(capsys, now=NEWEST + timedelta(hours=3))

    assert "(the Director is NOT recording now)" in out
    assert "this is history, not the machine now" in out


# ---------------------------------------------------------------------------------------------------
# --json: parsed, and every filter applies to it
# ---------------------------------------------------------------------------------------------------


def test_json_isParsedAndCarriesEveryField(director):
    result = invoke("--json")

    assert result.exit_code == 0
    payload = json.loads(result.output)
    assert set(payload) == {"recording", "window", "focus", "newest", "history", "notes", "samples"}
    assert payload["window"] == {"minutes": 60, "atUtc": None, "count": 72}
    assert payload["recording"]["newestSeq"] == 71
    assert payload["recording"]["oldestSeq"] == 0
    assert payload["focus"]["seq"] == 71
    assert payload["focus"]["diagnosis"]["causes"] == ["Disk"]
    assert payload["focus"]["diagnosis"]["culprit"]["diskName"] == "sdc"
    assert len(payload["samples"]) == 72
    assert len(payload["history"]) == 6

    # An unreadable figure stays null in the machine format, never zero.
    first = payload["samples"][0]
    assert first["cpu"]["couldRead"] is False
    assert first["cpu"]["totalBusyPercent"] is None
    assert first["cpu"]["unreadableReason"] == "first sample, no interval yet"


def test_json_minutesFilter_appliesToJson(director):
    whole = json.loads(invoke("--json").output)
    two = json.loads(invoke("--json", "--minutes", "2").output)

    assert two["window"]["minutes"] == 2
    assert two["window"]["count"] == 24
    assert [s["seq"] for s in two["samples"]] == list(range(48, 72))
    assert len(two["history"]) == 2
    assert set(two) == set(whole)  # the same shape, filtered


def test_json_atFilter_appliesToJson_andFocusIsTheNearestSample(director):
    payload = json.loads(invoke("--json", "--at", "2026-11-06T08:23:02Z", "--minutes", "2").output)

    assert payload["window"]["atUtc"] == "2026-11-06T08:23:02Z"
    assert payload["focus"]["seq"] == 24  # 08:23:00, the nearest
    assert payload["focus"]["takenAtUtc"] == "2026-11-06T08:23:00Z"
    assert all("2026-11-06T08:22:02Z" <= s["takenAtUtc"] <= "2026-11-06T08:24:02Z" for s in payload["samples"])
    assert payload["newest"]["seq"] == 71  # the newest is still the newest


def test_json_atNothingRecorded_isAnEmptyWindowThatSaysWhy(director):
    payload = json.loads(invoke("--json", "--at", "2026-11-05T08:00:00Z").output)

    assert payload["window"]["count"] == 0
    assert payload["samples"] == []
    assert payload["focus"] is None
    assert any("nothing was recorded near that time" in n for n in payload["notes"])


# ---------------------------------------------------------------------------------------------------
# Errors fail loudly, with exit 2 for a usage error
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("args, says", [
    (["--frobnicate"], "--frobnicate"),
    (["devlinux"], "devlinux"),
    (["--fields", "minute,nope"], "Valid fields: minute, samples"),
    (["--json", "--fields", "minute"], "--fields does not apply to --json"),
    (["--minutes", "0"], "--minutes must be from 1 to 1440"),
    (["--minutes", "1441"], "--minutes must be from 1 to 1440"),
    (["--at", "half past eight"], "--at takes a clock time"),
    (["--at", "2026-11-06T08:26:00"], "--at needs a time zone"),
    (["--instance", "nope"], "no Director instance is named 'nope'"),
])
def test_usageErrors_exit2_andSayWhatIsWrong(director, args, says):
    result = invoke(*args)

    assert result.exit_code == 2
    assert says in result.output


# ---------------------------------------------------------------------------------------------------
# Empty, torn and stepped recordings
# ---------------------------------------------------------------------------------------------------


def test_noRecording_saysCountZero_inTextAndJson(tmp_path, monkeypatch):
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path / "instances" / "default"))

    text = invoke()
    payload = json.loads(invoke("--json").output)

    assert text.exit_code == 0
    assert "count: 0" in text.output
    assert "has not recorded yet" in text.output
    assert payload["window"]["count"] == 0
    assert payload["newest"] is None
    assert payload["samples"] == []
    assert payload["recording"]["files"] == 0


def test_tornLine_isCountedAndSaid(director):
    with open(director / "resource-recording" / FIXTURE.name, "a", encoding="utf-8") as f:
        f.write('{"v":1,"seq":72,"run":"6f1c')

    payload = json.loads(invoke("--json").output)

    assert payload["recording"]["unreadableLines"] == 1
    assert payload["window"]["count"] == 72
    assert any("were not whole" in n for n in payload["notes"])


def test_wallClockStep_rowsKeptApartAndSaid(director, capsys):
    # Step the wall clock an hour back at seq 60, as the recorder would write it.
    path = director / "resource-recording" / FIXTURE.name
    lines = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
    for line in lines[60:]:
        stamp = datetime.strptime(line["takenAtUtc"], "%Y-%m-%dT%H:%M:%SZ") - timedelta(hours=1)
        line["takenAtUtc"] = stamp.strftime("%Y-%m-%dT%H:%M:%SZ")
    lines[60]["clockStepSeconds"] = -3600.0
    path.write_text("".join(json.dumps(line) + "\n" for line in lines), encoding="utf-8")

    out = run_direct(capsys, now=datetime(2026, 11, 6, 7, 27, 30, tzinfo=timezone.utc))

    _, rows = axi_output.parse_list(out, "history")
    # The recording's own timeline still says the last hour holds every sample, in the order taken.
    assert rows[-1]["minute"] == "07:26 (clock stepped)"
    assert sum(1 for r in rows if r["minute"].startswith("08:2")) == 5
    assert "the wall clock stepped 1 time(s) in this window" in out


# ---------------------------------------------------------------------------------------------------
# Which Director's recording
# ---------------------------------------------------------------------------------------------------


def test_instance_readsAnotherDirectorOnThisMachine(director, tmp_path):
    other = tmp_path / "instances" / "slot5" / "resource-recording"
    other.mkdir(parents=True)

    payload = json.loads(invoke("--json", "--instance", "slot5").output)

    assert Path(payload["recording"]["directory"]) == other
    assert payload["window"]["count"] == 0


def test_outsideASession_oneDirector_isThatOne(tmp_path, monkeypatch):
    monkeypatch.delenv("CC_DIRECTOR_ROOT", raising=False)
    monkeypatch.setattr(ops.setup_ops, "_default_root", lambda: tmp_path)
    (tmp_path / "instances" / "only").mkdir(parents=True)

    assert ops.recording_directory(None) == tmp_path / "instances" / "only" / "resource-recording"
