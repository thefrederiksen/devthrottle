"""Tests for `cc-devthrottle schedule list` in the AXI shape (issue #2922, docs/axi-standard.md).

What "done" means for a list command, and what each group below pins:

- Recoverability: every schedule's full id, full name and enabled state can be read back EXACTLY from
  the default output, with the same `parse_list` the helper ships. The same check is run against the
  old Rich table, and against a list that shortens names, to prove the check can fail.
- `--json` is byte-for-byte what it was when no filter is given, and a filter narrows the same bare
  array without changing its shape.
- An empty answer says `count: 0`, and `count: 0 of N total` when a filter matched nothing.
- An unknown field or an unknown flag exits 2 and lists the valid values.
- An answer with no list of jobs, a job with no id, or a job whose enabled flag is not true or false
  fails loudly instead of being listed or reported as "no schedules".
"""

import io
import json
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest
import typer
from rich import box
from rich.console import Console
from rich.table import Table
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import axi_output  # noqa: E402
from cc_shared.axi_output import ListParseError, parse_list  # noqa: E402
from src import schedule_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


def _job(job_id, name, enabled, *, machine="SOREN_NORTH", kind="recurring", cron="0 7 * * 1", run_at=None,
         next_run="2026-09-21T11:00:00Z", work_list=None, time_zone="Eastern Standard Time",
         repo="D:\\ReposFred\\devthrottle_internal", last_fired="2026-09-14T11:00:04Z", last_status="started",
         notify="failure", created="2026-08-01T10:00:00Z"):
    return {
        "id": job_id,
        "name": name,
        "enabled": enabled,
        "scheduleKind": kind,
        "cronExpression": cron,
        "runAt": run_at,
        "timeZoneId": time_zone,
        "target": {"machine": machine},
        "action": {
            "repoPath": repo,
            "seed": "A long seed prompt,\nwith a newline and \"quotes\".",
            "workListName": work_list,
            "autoDismiss": False,
        },
        "notifyOn": notify,
        "notifyWebhookUrl": None,
        "preventOverlap": True,
        "nextRunUtc": next_run,
        "lastFiredUtc": last_fired,
        "lastStatus": last_status,
        "createdUtc": created,
    }


# Both enabled states, with the names that break a naive list: a comma, quotes, non-ASCII, a name far
# longer than any table column, leading whitespace, and names that look like other values. No name and
# a blank name are not here: the Gateway never stores either, and the list refuses both.
JOBS = [
    _job("cj_1a10c4", "SmartScreen + winget follow-up, then report", False, kind="oneOff", cron=None,
         run_at="2026-09-09 09:47", next_run=None),
    _job("cj_33022a", 'Monday "business" finance run', True, time_zone="UTC", last_status="failed",
         last_fired="2026-09-15T07:00:01Z", notify="always", created="2026-08-20T09:30:00Z"),
    _job("cj_4056ec", "S\u00f8ren's caf\u00e9 \u2014 \U0001f680 check-in", True, machine="devthrottle-mac-mini",
         repo="/Users/soren/ReposFred/devthrottle", last_status="skipped-overlap", last_fired=None,
         notify="none"),
    _job("cj_91da7b", "Morning dictionary suggestions email (stopgap until issue 2074) for every account",
         False, machine="DEVTHROTTLE_2", work_list="nightly", cron="30 6 * * *", last_status=None,
         next_run="2026-09-17T10:30:00Z"),
    _job("cj_9dedee", "  padded  ", True, work_list="weekly review"),
    _job("cj_c536b9", "null", True),
    _job("cj_e22337", "0", False, kind="oneOff", cron=None, run_at="2026-10-01 18:00"),
]
ENABLED = ["no", "yes", "yes", "no", "yes", "yes", "no"]


@pytest.fixture
def serve(monkeypatch):
    """Serve a chosen list of jobs through the Gateway client, with no real HTTP."""

    def serve_(jobs):
        monkeypatch.setattr(schedule_ops.ScheduleClient, "__init__", lambda self, base_url=None: None)
        monkeypatch.setattr(schedule_ops.ScheduleClient, "list_jobs", lambda self: jobs)

    return serve_


def _check_recoverable(output, jobs):
    """Read id, name and enabled back from `output` and require an exact match with the jobs."""
    _, records = parse_list(output, "schedules")
    got = [(r["id"], r["name"], r["enabled"]) for r in records]
    want = [(j["id"], j["name"], "yes" if j["enabled"] else "no") for j in jobs]
    assert got == want


def _old_table(jobs):
    """The `schedule list` table as it was on main before #2922, frozen here as the negative control.

    Rendered the way an agent read it: stdout a pipe, so Rich lays it out at 80 columns.
    """
    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("Id", "Name", "Machine", "Runs", "Schedule", "Next run (UTC)", "Enabled"):
        table.add_column(column)
    for job in jobs:
        table.add_row(
            job["id"],
            job["name"] or "-",
            job["target"]["machine"],
            schedule_ops._runs_label(job),
            schedule_ops._schedule_label(job),
            job["nextRunUtc"] or "-",
            "yes" if job["enabled"] else "no",
        )
    console = Console(width=80, record=True, file=io.StringIO())
    console.print(table)
    return console.export_text()


# ---------------------------------------------------------------------------------------------------
# Recoverability
# ---------------------------------------------------------------------------------------------------


def test_list_jobs_DefaultOutput_EveryIdNameAndEnabledReadBackExactly(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(json_output=False)

    out = capsys.readouterr().out
    assert out.isascii()
    _check_recoverable(out, JOBS)
    fields, records = parse_list(out, "schedules")
    assert fields == ["id", "name", "enabled", "next-run"]
    # Pinned independently of the check's own mapping.
    assert [r["enabled"] for r in records] == ENABLED
    assert [r["name"] for r in records][5:] == ["null", "0"]
    assert records[0]["next-run"] is None
    assert records[1]["next-run"] == "2026-09-21T11:00:00Z"


def test_recoverability_check_OldRichTable_Fails():
    old = _old_table(JOBS)

    with pytest.raises((ListParseError, AssertionError)):
        _check_recoverable(old, JOBS)
    # And not merely for want of a header: the long name is wrapped across lines to fit 80 columns.
    assert JOBS[3]["name"] not in old


def test_recoverability_check_ListThatShortensNames_Fails():
    records = [
        {"id": j["id"], "name": (j["name"] or "")[:6] + "...", "enabled": "yes" if j["enabled"] else "no"}
        for j in JOBS
    ]
    shortened = axi_output.render_list("schedules", ["id", "name", "enabled"], records)

    with pytest.raises(AssertionError):
        _check_recoverable(shortened, JOBS)


def test_list_jobs_Fields_ShowsTheGatewaysOwnValuesInOrder(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(
        json_output=False,
        fields="machine,id,kind,cron,run-at,time-zone,work-list,path,last-fired,last-status,notify,created",
    )

    fields, records = parse_list(capsys.readouterr().out, "schedules")
    assert fields[:2] == ["machine", "id"]
    assert records[0] == {
        "machine": "SOREN_NORTH",
        "id": "cj_1a10c4",
        "kind": "oneOff",
        "cron": None,
        "run-at": "2026-09-09 09:47",
        "time-zone": "Eastern Standard Time",
        "work-list": None,
        "path": "D:\\ReposFred\\devthrottle_internal",
        "last-fired": "2026-09-14T11:00:04Z",
        "last-status": "started",
        "notify": "failure",
        "created": "2026-08-01T10:00:00Z",
    }
    assert records[3]["work-list"] == "nightly"
    assert records[1]["cron"] == "0 7 * * 1"


LIST_KEYS = {
    "id": lambda j: j["id"],
    "name": lambda j: j["name"],
    "enabled": lambda j: "yes" if j["enabled"] else "no",
    "next-run": lambda j: j["nextRunUtc"],
    "machine": lambda j: j["target"]["machine"],
    "kind": lambda j: j["scheduleKind"],
    "cron": lambda j: j["cronExpression"],
    "run-at": lambda j: j["runAt"],
    "time-zone": lambda j: j["timeZoneId"],
    "work-list": lambda j: j["action"]["workListName"],
    "path": lambda j: j["action"]["repoPath"],
    "last-fired": lambda j: j["lastFiredUtc"],
    "last-status": lambda j: j["lastStatus"],
    "notify": lambda j: j["notifyOn"],
    "created": lambda j: j["createdUtc"],
}


def test_fields_fixture_EveryMappedFieldHasTwoDistinctValues():
    # A mapping replaced by a constant can only be caught if the fixtures disagree with that constant.
    assert set(LIST_KEYS) == set(schedule_ops.SCHEDULE_LIST_FIELDS)
    for field, read in LIST_KEYS.items():
        present = {read(j) for j in JOBS if read(j) is not None}
        assert len(present) >= 2, field


def test_list_jobs_EveryField_EveryScheduleReadsBackExactly(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(json_output=False, fields=",".join(schedule_ops.SCHEDULE_LIST_FIELDS))

    fields, records = parse_list(capsys.readouterr().out, "schedules")
    assert fields == list(schedule_ops.SCHEDULE_LIST_FIELDS)
    assert records == [{f: LIST_KEYS[f](j) for f in fields} for j in JOBS]


# ---------------------------------------------------------------------------------------------------
# Counts, empty states, help
# ---------------------------------------------------------------------------------------------------


def test_list_jobs_Unfiltered_CountsByEnabledAndHelp(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(json_output=False)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 7 (enabled 4, disabled 3)"
    help_index = next(i for i, line in enumerate(lines) if line.startswith("help["))
    assert "  cc-devthrottle schedule list --enabled" in lines[help_index:]
    assert "  cc-devthrottle schedule get <id>" in lines[help_index:]


def test_list_jobs_Filtered_CountSaysOfTotal(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(json_output=False, enabled=False)

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 3 of 7 total (disabled 3)"
    _check_recoverable(out, [JOBS[0], JOBS[3], JOBS[6]])


def test_list_jobs_NoSchedules_PrintsCountZero(serve, capsys):
    serve([])

    schedule_ops.list_jobs(json_output=False)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 0"
    assert "schedules[0]{id,name,enabled,next-run}:" in lines
    assert "No schedules on the Gateway." in lines
    assert "  cc-devthrottle schedule create --help" in lines


def test_list_jobs_FilterMatchesNothing_PrintsCountZeroOfTotal(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(json_output=False, machine="NO_SUCH_MACHINE")

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 0 of 7 total"
    assert "No schedule matches the filter." in lines
    assert "  cc-devthrottle schedule list" in lines


# ---------------------------------------------------------------------------------------------------
# Filters
# ---------------------------------------------------------------------------------------------------


def test_list_jobs_MachineFilter_IgnoresCase(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(json_output=False, machine="DEVTHROTTLE-MAC-MINI")

    _check_recoverable(capsys.readouterr().out, [JOBS[2]])


def test_list_jobs_EnabledAndMachine_BothApply(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(json_output=False, enabled=True, machine="soren_north")

    _check_recoverable(capsys.readouterr().out, [JOBS[1], JOBS[4], JOBS[5]])


# ---------------------------------------------------------------------------------------------------
# --json keeps its shape
# ---------------------------------------------------------------------------------------------------


def test_list_jobs_JsonUnfiltered_ByteForByteTheJobs(serve, capsys):
    # Includes rows this tool would refuse to list: unfiltered --json prints what the Gateway sent.
    jobs = JOBS + [{"name": "no id"}, _job("cj_ffffff", "odd", "yes")]
    serve(jobs)

    schedule_ops.list_jobs(json_output=True)

    captured = capsys.readouterr()
    assert captured.out == json.dumps(jobs, indent=2) + "\n"
    assert captured.err == ""


def test_list_jobs_JsonEnabledFilter_SameBareArrayNarrowed(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(json_output=True, enabled=True)

    assert capsys.readouterr().out == json.dumps([JOBS[i] for i in (1, 2, 4, 5)], indent=2) + "\n"


def test_list_jobs_JsonMachineFilter_MatchesThatMachineOnly(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(json_output=True, machine="devthrottle_2")

    assert capsys.readouterr().out == json.dumps([JOBS[3]], indent=2) + "\n"


def test_list_jobs_JsonDisabledAndMachine_OnlyThatJob(serve, capsys):
    # JOBS[0] and JOBS[6] are also disabled; only the machine tells them apart from JOBS[3].
    serve(JOBS)

    schedule_ops.list_jobs(json_output=True, enabled=False, machine="DEVTHROTTLE_2")

    assert json.loads(capsys.readouterr().out) == [JOBS[3]]


def test_list_jobs_JsonFilterMatchesNothing_EmptyArray(serve, capsys):
    serve(JOBS)

    schedule_ops.list_jobs(json_output=True, machine="NO_SUCH_MACHINE")

    assert capsys.readouterr().out == "[]\n"


def test_schedule_list_Cli_DisabledJson_ThroughAPipe(serve):
    serve(JOBS)

    result = runner.invoke(app, ["schedule", "list", "--disabled", "--json"])

    assert result.exit_code == 0
    assert [j["id"] for j in json.loads(result.stdout)] == ["cj_1a10c4", "cj_91da7b", "cj_e22337"]


# ---------------------------------------------------------------------------------------------------
# Broken answers fail loudly
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("body", [{}, {"jobs": None}, {"jobs": "none"}, [{"id": "cj_1"}]])
def test_list_jobs_AnswerWithNoListOfJobs_ExitsOne(capsys, body):
    # Absent is not empty: an answer with no list of jobs never reads as "no schedules".
    response = MagicMock(status_code=200)
    client = schedule_ops.ScheduleClient.__new__(schedule_ops.ScheduleClient)
    client.base_url = "http://gateway.example"
    client._token = "key"
    with patch.object(schedule_ops, "_client", return_value=client), \
            patch("src.schedule_ops.requests.request", return_value=response), \
            patch.object(schedule_ops.gateway, "parse_json_body", return_value=body):
        for json_output in (True, False):
            with pytest.raises(typer.Exit) as exc:
                schedule_ops.list_jobs(json_output=json_output)
            assert exc.value.exit_code == 1
            captured = capsys.readouterr()
            assert captured.out == ""
            assert "no list of jobs" in captured.err


@pytest.mark.parametrize("orphan", [{"name": "no id", "enabled": True}, _job("", "blank", True),
                                    _job("  ", "spaces", True), _job(None, "none", True), "not a job"])
def test_schedule_list_Cli_JobWithNoId_ExitsOne(serve, orphan):
    serve(JOBS + [orphan])

    result = runner.invoke(app, ["schedule", "list"])

    assert result.exit_code == 1
    assert "no id (row 8)" in result.stderr
    assert result.stdout == ""


@pytest.mark.parametrize("args", [["schedule", "list"], ["schedule", "list", "--enabled", "--json"],
                                  ["schedule", "list", "--machine", "x", "--json"]])
@pytest.mark.parametrize("value", [None, "yes", 1])
def test_schedule_list_Cli_EnabledNotABoolean_ExitsOne(serve, args, value):
    odd = _job("cj_ffffff", "odd", True)
    if value is None:
        del odd["enabled"]
    else:
        odd["enabled"] = value
    serve(JOBS + [odd])

    result = runner.invoke(app, args)

    assert result.exit_code == 1
    assert "cj_ffffff" in result.stderr
    assert "must be true or false" in result.stderr
    assert result.stdout == ""


@pytest.mark.parametrize("args", [["schedule", "list"], ["schedule", "list", "--machine", "SOREN_NORTH"],
                                  ["schedule", "list", "--machine", "SOREN_NORTH", "--json"],
                                  ["schedule", "list", "--enabled", "--json"],
                                  ["schedule", "list", "--disabled"]])
@pytest.mark.parametrize("target", ["drop", None, {}, {"machine": None}, {"machine": ""},
                                    {"machine": "  "}, {"machine": 7}, "SOREN_NORTH"])
def test_schedule_list_Cli_JobWithNoMachine_ExitsOne(serve, args, target):
    # The Gateway refuses to store a schedule with no target machine. One that arrives anyway fails
    # loudly; it never quietly drops out of a --machine filter as a nonmatch.
    orphan = _job("cj_0rphan", "orphan", True)
    if target == "drop":
        del orphan["target"]
    else:
        orphan["target"] = target
    serve(JOBS + [orphan])

    result = runner.invoke(app, args)

    assert result.exit_code == 1
    assert "cj_0rphan" in result.stderr
    assert "no target machine" in result.stderr
    assert result.stdout == ""


def test_schedule_list_Cli_UnfilteredJson_JobWithNoMachine_PrintsTheRawRows(serve):
    # The unfiltered --json reads no row; it prints the Gateway's answer as it always has, and is
    # where the error message sends the caller to look.
    orphan = _job("cj_0rphan", "orphan", True)
    del orphan["target"]
    serve(JOBS + [orphan])

    result = runner.invoke(app, ["schedule", "list", "--json"])

    assert result.exit_code == 0
    assert json.loads(result.stdout) == JOBS + [orphan]


def test_list_jobs_GatewayError_ExitsOneWithTheSentence(monkeypatch, capsys):
    def fail(self):
        raise schedule_ops.GatewayError("Gateway not reachable at http://gateway.example")

    monkeypatch.setattr(schedule_ops.ScheduleClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(schedule_ops.ScheduleClient, "list_jobs", fail)

    for json_output in (True, False):
        with pytest.raises(typer.Exit) as exc:
            schedule_ops.list_jobs(json_output=json_output)
        assert exc.value.exit_code == 1
        captured = capsys.readouterr()
        assert captured.out == ""
        assert "Gateway not reachable" in captured.err


# ---------------------------------------------------------------------------------------------------
# Usage errors exit 2 and list the valid values
# ---------------------------------------------------------------------------------------------------


def test_schedule_list_Cli_UnknownField_ExitsTwoListingFields(serve):
    serve(JOBS)

    result = runner.invoke(app, ["schedule", "list", "--fields", "id,seed"])

    assert result.exit_code == 2
    assert "seed" in result.stderr
    assert ("id, name, enabled, next-run, machine, kind, cron, run-at, time-zone, work-list, path, "
            "last-fired, last-status, notify, created") in result.stderr


def test_schedule_list_Cli_FieldsWithJson_ExitsTwo(serve):
    serve(JOBS)

    result = runner.invoke(app, ["schedule", "list", "--json", "--fields", "id"])

    assert result.exit_code == 2
    assert "--fields does not apply to --json" in result.stderr


@pytest.mark.parametrize("args", [["--state", "enabled"], ["--enabled=no"], ["--status", "on"]])
def test_schedule_list_Cli_UnknownFlag_ExitsTwo(serve, args):
    serve(JOBS)

    result = runner.invoke(app, ["schedule", "list", *args])

    assert result.exit_code == 2


def test_schedule_list_Cli_BlankMachine_ExitsTwo(serve):
    serve(JOBS)

    result = runner.invoke(app, ["schedule", "list", "--machine", " "])

    assert result.exit_code == 2
    assert "--machine needs a value" in result.stderr


# ---------------------------------------------------------------------------------------------------
# Every field is checked where it is read (re-check 2). The Gateway serializes every CronJobDto field and
# its write check refuses a blank name, time zone or repo path, so a missing key, a wrong kind, or a
# value it would never store is a broken answer - never an empty value. Every path that reads the rows
# refuses it; the unfiltered --json prints it as sent.
# ---------------------------------------------------------------------------------------------------

_MISSING = object()

READ_PATHS = [["schedule", "list"],
              ["schedule", "list", "--fields", ",".join(schedule_ops.SCHEDULE_LIST_FIELDS)],
              ["schedule", "list", "--machine", "SOREN_NORTH"],
              ["schedule", "list", "--disabled"],
              ["schedule", "list", "--machine", "SOREN_NORTH", "--json"],
              ["schedule", "list", "--enabled", "--json"]]


def _broken(changes, **job_args):
    orphan = _job("cj_0rphan", "orphan", True, **job_args)
    for path, value in changes.items():
        record, key = (orphan["action"], path[len("action."):]) if path.startswith("action.") else (orphan, path)
        if value is _MISSING:
            del record[key]
        else:
            record[key] = value
    return orphan


ONE_OFF = {"kind": "oneOff", "cron": None, "run_at": "2026-10-01 18:00", "next_run": None}

# (field, value, job arguments, what the error names)
BROKEN_JOB_FIELDS = [
    ("name", _MISSING, {}, "no name"),
    ("name", None, {}, "name null"),
    ("name", "", {}, "a blank name"),
    ("name", "   ", {}, "a blank name"),
    ("name", 5, {}, "name a int"),
    ("nextRunUtc", _MISSING, {}, "no nextRunUtc"),
    ("nextRunUtc", 5, {}, "nextRunUtc a int"),
    ("nextRunUtc", "", {}, "a blank nextRunUtc"),
    ("scheduleKind", _MISSING, {}, "no scheduleKind"),
    ("scheduleKind", None, {}, "scheduleKind null"),
    ("scheduleKind", "", {}, "a blank scheduleKind"),
    ("scheduleKind", "weekly", {}, "scheduleKind weekly"),
    ("scheduleKind", ["recurring"], {}, "scheduleKind a list"),
    ("cronExpression", _MISSING, {}, "no cronExpression"),
    ("cronExpression", None, {}, "cronExpression null"),
    ("cronExpression", "", {}, "a blank cronExpression"),
    ("cronExpression", 7, {}, "cronExpression a int"),
    ("cronExpression", _MISSING, ONE_OFF, "no cronExpression"),
    ("cronExpression", 7, ONE_OFF, "cronExpression a int"),
    ("runAt", _MISSING, {}, "no runAt"),
    ("runAt", 7, {}, "runAt a int"),
    ("runAt", None, ONE_OFF, "runAt null"),
    ("runAt", " ", ONE_OFF, "a blank runAt"),
    ("timeZoneId", _MISSING, {}, "no timeZoneId"),
    ("timeZoneId", None, {}, "timeZoneId null"),
    ("timeZoneId", "", {}, "a blank timeZoneId"),
    ("timeZoneId", 0, {}, "timeZoneId a int"),
    ("action", _MISSING, {}, "action missing; it must be an object"),
    ("action", None, {}, "action null; it must be an object"),
    ("action", "D:\\repo", {}, "action a str; it must be an object"),
    ("action.workListName", _MISSING, {}, "no action.workListName"),
    ("action.workListName", 3, {}, "action.workListName a int"),
    ("action.repoPath", _MISSING, {}, "no action.repoPath"),
    ("action.repoPath", None, {}, "action.repoPath null"),
    ("action.repoPath", "", {}, "a blank action.repoPath"),
    ("action.repoPath", 3, {}, "action.repoPath a int"),
    ("lastFiredUtc", _MISSING, {}, "no lastFiredUtc"),
    ("lastFiredUtc", "", {}, "a blank lastFiredUtc"),
    ("lastFiredUtc", False, {}, "lastFiredUtc a bool"),
    ("lastStatus", _MISSING, {}, "no lastStatus"),
    ("lastStatus", 200, {}, "lastStatus a int"),
    ("notifyOn", _MISSING, {}, "no notifyOn"),
    ("notifyOn", None, {}, "notifyOn null"),
    ("notifyOn", "", {}, "a blank notifyOn"),
    ("notifyOn", "never", {}, "notifyOn never"),
    ("notifyOn", "Always", {}, "notifyOn Always"),
    ("createdUtc", _MISSING, {}, "no createdUtc"),
    ("createdUtc", None, {}, "createdUtc null"),
    ("createdUtc", "", {}, "a blank createdUtc"),
    ("createdUtc", 1, {}, "createdUtc a int"),
]


@pytest.mark.parametrize("args", READ_PATHS)
@pytest.mark.parametrize("field, value, job_args, said", BROKEN_JOB_FIELDS)
def test_schedule_list_Cli_BrokenField_ExitsOneOnEveryPath(serve, args, field, value, job_args, said):
    serve(JOBS + [_broken({field: value}, **job_args)])

    result = runner.invoke(app, args)

    assert result.exit_code == 1
    assert "schedule cj_0rphan with " + said in result.stderr
    assert "Traceback" not in result.stderr
    assert result.stdout == ""


def test_schedule_list_Cli_InspectionReproductions_ExitOne(serve):
    # The inspection's own cases: no name on the default list, no next run on the default list, and no
    # action behind --fields id,path,work-list.
    for field, args in (("name", ["schedule", "list"]), ("nextRunUtc", ["schedule", "list"]),
                        ("action", ["schedule", "list", "--fields", "id,path,work-list"])):
        serve([_broken({field: _MISSING})])
        result = runner.invoke(app, args)
        assert result.exit_code == 1, field
        assert result.stdout == "", field


# What the Gateway can store and that must still list: nulls where CronJobDto is nullable, the timing
# field the kind does not use left null or blank, an empty work list beside a seed, an empty last
# status, and a kind in any case with spaces (CronSchedule compares it ignoring both).
SOUND_JOB_VARIANTS = [
    ({"nextRunUtc": None, "lastFiredUtc": None, "lastStatus": None, "action.workListName": None}, {}),
    ({"runAt": ""}, {}),
    ({"cronExpression": ""}, ONE_OFF),
    ({"action.workListName": "", "lastStatus": ""}, {}),
    ({"scheduleKind": " Recurring "}, {}),
    ({"scheduleKind": "ONEOFF"}, ONE_OFF),
]


@pytest.mark.parametrize("args", READ_PATHS)
@pytest.mark.parametrize("changes, job_args", SOUND_JOB_VARIANTS)
def test_schedule_list_Cli_ValuesTheGatewayStores_AreListedAsSent(serve, args, changes, job_args):
    job = _broken(changes, **job_args)
    serve([job])

    result = runner.invoke(app, args)

    assert result.exit_code == 0, result.stderr
    if "--json" in args:
        assert json.loads(result.stdout) == [job]
    elif "--fields" in args:
        _, records = parse_list(result.stdout, "schedules")
        assert records == [{f: LIST_KEYS[f](job) for f in schedule_ops.SCHEDULE_LIST_FIELDS}]


@pytest.mark.parametrize("args", READ_PATHS)
def test_schedule_list_Cli_TwoRowsWithOneId_ExitsOne(serve, args):
    serve(JOBS + [_job("cj_33022a", "twin", True)])

    result = runner.invoke(app, args)

    assert result.exit_code == 1
    assert "schedule cj_33022a with an id that an earlier row already has (row 8)" in result.stderr
    assert result.stdout == ""


@pytest.mark.parametrize("field, value, job_args, said", BROKEN_JOB_FIELDS)
def test_schedule_list_Cli_JsonUnfiltered_BrokenFieldStaysTheRawAnswer(serve, field, value, job_args, said):
    jobs = JOBS + [_broken({field: value}, **job_args)]
    serve(jobs)

    result = runner.invoke(app, ["schedule", "list", "--json"])

    assert result.exit_code == 0
    assert json.loads(result.stdout) == jobs
