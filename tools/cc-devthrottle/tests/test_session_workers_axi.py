"""Tests for `cc-devthrottle session workers` in the AXI shape (issue #2922, docs/axi-standard.md).

The same bar as `session list` (tests/test_session_list_axi.py):

- Recoverability: every worker's full id, full name, state and need can be read back EXACTLY from the
  default output with the `parse_list` the helper ships. The same check is run against the old Rich
  table, frozen below, to prove the check can fail.
- `--json` is the Gateway's rows for these workers, unchanged, as a bare array.
- No workers says `count: 0`.
- The state is the same plain_state fold `session list` uses.
"""

import io
import json
import sys
from pathlib import Path

import pytest
from rich import box
from rich.console import Console
from rich.table import Table
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import ListParseError, parse_list  # noqa: E402
from src import session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

MANAGER = "5a5a5a5a-6666-7777-8888-999999999999"
OTHER_MANAGER = "6b6b6b6b-6666-7777-8888-999999999999"


def _row(sid, name, *, bucket, activity="WaitingForInput", crashed=False, controller=MANAGER,
         raised=False, reason=None, **extra):
    row = {
        "sessionId": sid,
        "number": 100,
        "name": name,
        "machineName": "SOREN_NORTH",
        "repoPath": r"D:\ReposFred\devthrottle",
        "activityState": activity,
        "triageBucket": bucket,
        "crashed": crashed,
        "controllerSessionId": controller,
        "stateLabel": "Label the fold never reads",
        "needsManager": raised,
        "needsManagerReason": reason,
    }
    row.update(extra)
    return row


# The eighty-column case the inspection reproduced, plus the names that break a naive list, one of every
# state, a raised hand whose need has a comma, and a reason that outlived its lowered hand.
LONG_NAME = "Worker with a very long name that must remain readable even when the terminal is only eighty columns wide"
WORKERS = [
    _row("11111111-2222-3333-4444-555555555555", LONG_NAME, bucket="active", activity="Working",
         raised=True, reason="which branch, main or the mission branch?"),
    _row("11111111-2222-3333-4444-555555555556", 'review: "quoted" name', bucket="needsYou"),
    _row("11111111-2222-3333-4444-555555555557", "S\u00f8ren's caf\u00e9 \u2014 \U0001f680", bucket="active"),
    _row("11111111-2222-3333-4444-555555555558", "  padded  ", bucket="onHold", reason="stale words"),
    _row("11111111-2222-3333-4444-555555555559", None, bucket="needsYou", activity="Exited", crashed=True),
    _row("11111111-2222-3333-4444-55555555555a", "", bucket="active", activity="Working",
         raised=True, reason="multi\nline need"),
]
EXPECTED = [
    ("working", "up", "which branch, main or the mission branch?"),
    ("needs-you", "down", None),
    ("ready", "down", None),
    ("snoozed", "down", None),
    ("crashed", "down", None),
    ("working", "up", "multi\nline need"),
]
# Rows that are not this manager's: another manager's worker, and a session nobody drives.
NOT_MINE = [
    _row("22222222-2222-3333-4444-555555555555", "someone else's", bucket="active", controller=OTHER_MANAGER),
    _row("33333333-2222-3333-4444-555555555555", "undriven", bucket="active", controller=None),
]
FLEET = WORKERS[:3] + NOT_MINE + WORKERS[3:]


@pytest.fixture
def serve(monkeypatch):
    def serve_(sessions, complete=True, reason=None, stale=None, me=MANAGER):
        monkeypatch.setenv("CC_SESSION_ID", me)
        monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: (sessions, complete, reason, stale))

    return serve_


def _check_recoverable(output, workers):
    """Read id, name, state and need back from `output` and require an exact match with the roster."""
    _, records = parse_list(output, "workers")
    got = [(r["id"], r["name"], r["state"], r["need"]) for r in records]
    want = [
        (w["sessionId"], w["name"], session_ops.plain_state(w), w["needsManagerReason"] if w["needsManager"] else None)
        for w in workers
    ]
    assert got == want


def _old_table(workers):
    """The `session workers` table as it was before this change, frozen here as the negative control.

    Rendered the way an agent read it: stdout a pipe, so Rich lays it out at 80 columns.
    """
    table = Table(title=f"Sessions driven by {session_ops.gateway.short_id(MANAGER)}", box=box.ASCII)
    table.add_column("ID")
    table.add_column("NAME")
    table.add_column("STATE")
    table.add_column("HAND UP - WHAT THEY NEED")
    for x in workers:
        raised = x.get("needsManager") is True
        table.add_row(
            session_ops.gateway.short_id(x["sessionId"]),
            str(x["name"] or ""),
            str(x["stateLabel"] or ""),
            str(x["needsManagerReason"] or "") if raised else "",
        )
    console = Console(width=80, record=True, file=io.StringIO())
    console.print(table)
    return console.export_text()


# ---------------------------------------------------------------------------------------------------
# Recoverability
# ---------------------------------------------------------------------------------------------------


def test_listMyWorkers_DefaultOutput_EveryIdNameStateAndNeedReadBackExactly(serve, capsys):
    serve(FLEET)

    session_ops.list_my_workers()

    out = capsys.readouterr().out
    assert out.isascii()
    _check_recoverable(out, WORKERS)
    fields, records = parse_list(out, "workers")
    assert fields == ["id", "name", "state", "hand", "need"]
    # Pinned independently of plain_state, so the check above is not the fold agreeing with itself.
    assert [(r["state"], r["hand"], r["need"]) for r in records] == EXPECTED
    # "" and None are different names and both survive the round trip.
    assert [r["name"] for r in records][4:] == [None, ""]


def test_listMyWorkers_EightyColumns_TheLongIdAndNameAreEachOnOneLine(serve, capsys, monkeypatch):
    # The inspection's reproduction: one worker, a long name, an 80-column console.
    monkeypatch.setenv("COLUMNS", "80")
    serve([WORKERS[0]])

    session_ops.list_my_workers()

    out = capsys.readouterr().out
    line = next(line for line in out.splitlines() if WORKERS[0]["sessionId"] in line)
    assert LONG_NAME in line


def test_recoverability_check_OldRichTable_Fails():
    old = _old_table(WORKERS)

    with pytest.raises((ListParseError, AssertionError)):
        _check_recoverable(old, WORKERS)
    # And not merely for want of a header: the table itself loses the facts.
    assert WORKERS[0]["sessionId"] not in old
    assert LONG_NAME not in old


def test_recoverability_check_OldRichTableOfOneLongWorker_Fails():
    old = _old_table([WORKERS[0]])

    with pytest.raises((ListParseError, AssertionError)):
        _check_recoverable(old, [WORKERS[0]])
    assert "11111111 " in old or "11111111|" in old.replace(" ", "")
    assert WORKERS[0]["sessionId"] not in old
    assert LONG_NAME not in old


def test_listMyWorkers_Fields_ShowsExactlyTheRequestedFieldsInOrder(serve, capsys):
    serve(FLEET)

    session_ops.list_my_workers(fields="machine,id,number,hand,path")

    fields, records = parse_list(capsys.readouterr().out, "workers")
    assert fields == ["machine", "id", "number", "hand", "path"]
    assert records[0] == {
        "machine": "SOREN_NORTH",
        "id": WORKERS[0]["sessionId"],
        "number": "100",
        "hand": "up",
        "path": r"D:\ReposFred\devthrottle",
    }


# ---------------------------------------------------------------------------------------------------
# Counts, empty states, help
# ---------------------------------------------------------------------------------------------------


def test_listMyWorkers_CountsByStateHandsUpAndHelp(serve, capsys):
    serve(FLEET)

    session_ops.list_my_workers()

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == f"Sessions driven by {MANAGER}."
    assert lines[1] == "count: 6 (needs-you 1, working 2, ready 1, snoozed 1, crashed 1)"
    assert lines[2] == "hands-up: 2"
    assert session_ops.WORKERS_HAND_NOTE in lines
    help_at = lines.index("help[4]:")
    assert lines[help_at + 1:] == [
        "  cc-devthrottle session buffer <session-id>",
        '  cc-devthrottle message send <session-id> "<message>"',
        "  cc-devthrottle session workers --fields " + ",".join(session_ops.WORKERS_FIELDS),
        "  cc-devthrottle session workers --json",
    ]


def test_listMyWorkers_NoWorkers_PrintsCountZero(serve, capsys):
    serve(NOT_MINE)

    session_ops.list_my_workers()

    lines = capsys.readouterr().out.splitlines()
    assert lines[1] == "count: 0"
    assert lines[2] == "hands-up: 0"
    assert lines[3] == "workers[0]{id,name,state,hand,need}:"
    assert "You are not driving any sessions." in lines
    assert parse_list("\n".join(lines), "workers") == (["id", "name", "state", "hand", "need"], [])
    assert "help[2]:" in lines


def test_listMyWorkers_NoWorkersOnAnIncompleteRoster_DoesNotClaimNone(serve, capsys):
    serve(NOT_MINE, complete=False, reason="MACHINE_B is offline.", stale="A machine has not reported.")

    session_ops.list_my_workers()

    out = capsys.readouterr().out
    assert "count: 0" in out.splitlines()
    assert "You are not driving any sessions." not in out
    assert "not the whole fleet" in out
    assert "MACHINE_B is offline." in out
    assert "A machine has not reported." in out


def test_listMyWorkers_TargetIsAnotherManager_ListsItsWorkers(serve, capsys):
    other = _row(OTHER_MANAGER, "the other manager", bucket="active", controller=None)
    serve(FLEET + [other], me=MANAGER)

    session_ops.list_my_workers(target="the other manager")

    _, records = parse_list(capsys.readouterr().out, "workers")
    assert [r["id"] for r in records] == [NOT_MINE[0]["sessionId"]]


def test_listMyWorkers_ControllerIdIgnoresCase(serve, capsys):
    serve([dict(WORKERS[1], controllerSessionId=MANAGER.upper())])

    session_ops.list_my_workers()

    _, records = parse_list(capsys.readouterr().out, "workers")
    assert [r["id"] for r in records] == [WORKERS[1]["sessionId"]]


# ---------------------------------------------------------------------------------------------------
# --json
# ---------------------------------------------------------------------------------------------------


def test_sessionWorkers_Cli_Json_TheGatewayRowsForTheseWorkersUnchanged(serve):
    serve(FLEET)

    result = runner.invoke(app, ["session", "workers", "--json"])

    assert result.exit_code == 0, result.output
    assert result.stdout == json.dumps(WORKERS, indent=2) + "\n"


def test_sessionWorkers_Cli_JsonNoWorkers_EmptyArray(serve):
    serve(NOT_MINE)

    result = runner.invoke(app, ["session", "workers", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == []


def test_sessionWorkers_Cli_JsonIncompleteRoster_WarnsOnStandardErrorOnly(serve):
    serve(NOT_MINE, complete=False, reason="MACHINE_B is offline \u2014 down.", stale="Late machine.")

    result = runner.invoke(app, ["session", "workers", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == []
    assert result.stderr.isascii()
    assert "MACHINE_B is offline \\u2014 down." in result.stderr
    assert "Late machine." in result.stderr


def test_sessionWorkers_Cli_JsonDoesNotNeedTheFold(serve):
    # --json prints the Gateway's rows; a bucket this tool does not know is not its concern there.
    rows = [dict(WORKERS[0], triageBucket="brandNew")]
    serve(rows)

    result = runner.invoke(app, ["session", "workers", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == rows


def test_sessionWorkers_Cli_UnknownBucket_ExitsOneNamingIt(serve):
    serve([dict(WORKERS[0], triageBucket="brandNew")])

    result = runner.invoke(app, ["session", "workers"])

    assert result.exit_code == 1
    assert "brandNew" in result.stderr
    assert result.stdout == ""


@pytest.mark.parametrize("orphan", [
    {k: v for k, v in WORKERS[1].items() if k != "sessionId"},
    dict(WORKERS[1], sessionId="  "),
])
def test_sessionWorkers_Cli_WorkerWithNoSessionId_ExitsOneAndPrintsNoList(serve, orphan):
    serve([WORKERS[0], orphan])

    result = runner.invoke(app, ["session", "workers"])

    assert result.exit_code == 1
    assert "no session id" in result.stderr
    assert result.stdout == ""


# The field is required on every row. null means nobody drives the session; ABSENT is a missing answer,
# and reading it as null would print "count: 0" and "You are not driving any sessions." for a manager
# whose workers the Gateway simply did not describe (re-check 4, finding 1).
_NO_CONTROLLER = {k: v for k, v in WORKERS[0].items() if k != "controllerSessionId"}


@pytest.mark.parametrize("args", [["session", "workers"], ["session", "workers", "--json"]])
def test_sessionWorkers_Cli_RowWithNoControllerField_ExitsOneAndPrintsNoList(serve, args):
    serve([_NO_CONTROLLER])

    result = runner.invoke(app, args)

    assert result.exit_code == 1, result.output
    assert result.stdout == ""
    assert "11111111-2222-3333-4444-555555555555" in result.stderr
    assert "controllerSessionId missing" in result.stderr
    assert "help[" in result.stderr


def test_sessionWorkers_Cli_OneRowWithNoControllerFieldAmongWorkers_ExitsOne(serve):
    serve(FLEET + [dict(_NO_CONTROLLER, sessionId="44444444-2222-3333-4444-555555555555")])

    result = runner.invoke(app, ["session", "workers"])

    assert result.exit_code == 1, result.output
    assert "44444444-2222-3333-4444-555555555555" in result.stderr
    assert result.stdout == ""


def test_sessionWorkers_Cli_NullControllerAndPascalCaseField_AreAnswers(serve):
    pascal = {k: v for k, v in WORKERS[1].items() if k != "controllerSessionId"}
    pascal["ControllerSessionId"] = MANAGER
    serve([NOT_MINE[1], pascal])

    result = runner.invoke(app, ["session", "workers", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == [pascal]
