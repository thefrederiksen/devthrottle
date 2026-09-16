"""Tests for `cc-devthrottle session list` in the AXI shape (issue #2922, docs/axi-standard.md).

What "done" means for a list command, and what each group below pins:

- Recoverability: every session's full id, full name and state can be read back EXACTLY from the
  default output, with the same `parse_list` the helper ships. The same check is run against the old
  Rich table, and against a list that shortens names, to prove the check can fail.
- `--json` is byte-for-byte what it was when no filter is given, and a filter narrows the same bare
  array without changing its shape.
- An empty answer says `count: 0`, and `count: 0 of N total` when a filter matched nothing.
- An unknown state, an unknown field or an unknown flag exits 2 and lists the valid values.
- The plain state is folded from the roster exactly as the Architect ruled, and an unknown bucket
  fails loudly instead of being guessed.
"""

import io
import json
import sys
from pathlib import Path

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
from src import session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


def _row(sid, name, *, bucket, activity="WaitingForInput", crashed=False,
         machine="SOREN_NORTH", repo=r"D:\ReposFred\devthrottle", **extra):
    row = {
        "sessionId": sid,
        "number": 100,
        "name": name,
        "machineName": machine,
        "repoPath": repo,
        "activityState": activity,
        "triageBucket": bucket,
        "crashed": crashed,
        "lastStatusReason": "Working on it",  # never read: a status sentence is not a state
    }
    row.update(extra)
    return row


# One of every state, with the names that break a naive list: a comma, quotes, non-ASCII, a name far
# longer than any table column, leading whitespace, no name at all, and a name that is the empty string
# (which must read back as the empty string, not as no name).
FLEET = [
    _row("d2a4069f-1111-4111-8111-000000000001", "AXI Tools - Worker - step 3, session list", bucket="needsYou"),
    _row("d2a4069f-1111-4111-8111-000000000002", 'review: "quoted" name', bucket="active", activity="Working",
         machine="devthrottle-mac-mini", repo="/Users/soren/ReposFred/devthrottle-axi-session-list"),
    _row("d2a4069f-1111-4111-8111-000000000003", "S\u00f8ren's caf\u00e9 \u2014 \U0001f680 launch", bucket="active"),
    _row("d2a4069f-1111-4111-8111-000000000004",
         "A very long session name that no eighty column table could ever show in full without cutting it",
         bucket="onHold", repo=r"C:\ReposFred\cc-consult"),
    _row("d2a4069f-1111-4111-8111-000000000005", "  padded  ", bucket="needsYou", activity="Exited", crashed=True),
    _row("d2a4069f-1111-4111-8111-000000000006", None, bucket="active", activity="Working"),
    _row("d2a4069f-1111-4111-8111-000000000007", "", bucket="onHold"),
]
EXPECTED_STATES = ["needs-you", "working", "ready", "snoozed", "crashed", "working", "snoozed"]

# Rows no list may show: a session with no id cannot be named by any verb.
ORPHANS = [
    {k: v for k, v in _row("unused", "orphan", bucket="active").items() if k != "sessionId"},
    _row("", "blank id", bucket="active"),
    _row("   ", "whitespace id", bucket="active"),
    _row(None, None, bucket="active"),
]


@pytest.fixture
def serve(monkeypatch):
    """Serve a chosen roster through the one shared fetch, as a complete roster, with no real HTTP."""

    def serve_(sessions, complete=True, reason=None, stale=None):
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
        monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: (sessions, complete, reason, stale))

    return serve_


def _check_recoverable(output, sessions):
    """Read id, name and state back from `output` and require an exact match with the roster."""
    _, records = parse_list(output, "sessions")
    got = [(r["id"], r["name"], r["state"]) for r in records]
    want = [(s["sessionId"], s["name"], session_ops.plain_state(s)) for s in sessions]
    assert got == want


def _old_table(sessions):
    """The `session list` table as it was on main before #2922, frozen here as the negative control.

    Rendered the way an agent read it: stdout a pipe, so Rich lays it out at 80 columns.
    """
    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("NO.", "ID", "NAME", "MACHINE", "REPOSITORY", "MODEL", "STATUS"):
        table.add_column(column)
    for s in sessions:
        status = s["activityState"]
        if s.get("crashed") is True:
            status = f"{status} (crashed)"
        table.add_row(
            str(s["number"]),
            session_ops.gateway.short_id(s["sessionId"]),
            s["name"] or "(unnamed)",
            s["machineName"],
            session_ops._repo_name(s["repoPath"]),
            session_ops._model_text(s),
            status,
        )
    console = Console(width=80, record=True, file=io.StringIO())
    console.print(table)
    return console.export_text()


# ---------------------------------------------------------------------------------------------------
# Recoverability
# ---------------------------------------------------------------------------------------------------


def test_list_sessions_DefaultOutput_EveryIdNameAndStateReadBackExactly(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=False)

    out = capsys.readouterr().out
    assert out.isascii()
    _check_recoverable(out, FLEET)
    fields, records = parse_list(out, "sessions")
    assert fields == ["id", "name", "state", "repo"]
    # Pinned independently of plain_state, so the check above is not the fold agreeing with itself.
    assert [r["state"] for r in records] == EXPECTED_STATES
    # Pinned independently too: "" and None are different names and both survive the round trip.
    assert [r["name"] for r in records][5:] == [None, ""]


@pytest.mark.parametrize("orphan", ORPHANS)
def test_session_list_Cli_RowWithNoSessionId_ExitsOneAndPrintsNoList(serve, orphan):
    serve(FLEET + [orphan])

    result = runner.invoke(app, ["session", "list"])

    assert result.exit_code == 1
    assert "no session id" in result.stderr
    assert "row 8" in result.stderr
    assert result.stdout == ""


def test_recoverability_check_OldRichTable_Fails():
    # The proof the check above can fail: the table it replaced cannot be read back at all.
    old = _old_table(FLEET)

    with pytest.raises((ListParseError, AssertionError)):
        _check_recoverable(old, FLEET)
    # And not merely for want of a header: the table itself loses the facts. Full ids are cut to eight
    # characters and the long name is elided to fit 80 columns.
    assert FLEET[0]["sessionId"] not in old
    assert FLEET[3]["name"] not in old


def test_recoverability_check_ListThatShortensNames_Fails():
    # A list in the right shape that cuts names short, the way the old table did, still fails.
    records = [
        {"id": s["sessionId"], "name": (s["name"] or "")[:6] + "...", "state": session_ops.plain_state(s)}
        for s in FLEET
    ]
    shortened = axi_output.render_list("sessions", ["id", "name", "state"], records)

    with pytest.raises(AssertionError):
        _check_recoverable(shortened, FLEET)


def test_list_sessions_Fields_ShowsExactlyTheRequestedFieldsInOrder(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=False, fields="machine,id,number,path")

    fields, records = parse_list(capsys.readouterr().out, "sessions")
    assert fields == ["machine", "id", "number", "path"]
    assert records[1] == {
        "machine": "devthrottle-mac-mini",
        "id": FLEET[1]["sessionId"],
        "number": "100",
        "path": "/Users/soren/ReposFred/devthrottle-axi-session-list",
    }


# ---------------------------------------------------------------------------------------------------
# Counts, empty states, help
# ---------------------------------------------------------------------------------------------------


def test_list_sessions_Unfiltered_CountsByStateAndHelp(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=False)

    lines = capsys.readouterr().out.splitlines()
    assert lines[0] == "count: 7 (needs-you 1, working 2, ready 1, snoozed 2, crashed 1)"
    help_index = next(i for i, line in enumerate(lines) if line.startswith("help["))
    assert "  cc-devthrottle session list --state needs-you" in lines[help_index:]


def test_list_sessions_Filtered_CountSaysOfTotal(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=False, state="working,ready")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 3 of 7 total (working 2, ready 1)"
    _, records = parse_list(out, "sessions")
    assert [r["id"] for r in records] == [FLEET[1]["sessionId"], FLEET[2]["sessionId"], FLEET[5]["sessionId"]]


def test_list_sessions_EmptyRoster_PrintsCountZero(serve, capsys):
    serve([])

    session_ops.list_sessions(json_output=False)

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 0"
    assert "sessions[0]{id,name,state,repo}:" in out.splitlines()
    assert "No sessions are running in the fleet." in out


def test_list_sessions_FilterMatchesNothing_PrintsCountZeroOfTotal(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=False, machine="NO_SUCH_MACHINE")

    out = capsys.readouterr().out
    assert out.splitlines()[0] == "count: 0 of 7 total"
    assert "No session matches the filter." in out
    assert "  cc-devthrottle session list" in out.splitlines()


def test_list_sessions_NonAsciiCaution_IsWrittenAsAscii(serve, capsys):
    serve([], complete=False, reason="Director on S\u00d8REN is offline")

    session_ops.list_sessions(json_output=False)

    out = capsys.readouterr().out
    assert out.isascii()
    assert "S\\u00d8REN" in out


# ---------------------------------------------------------------------------------------------------
# Filters
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("repo", [
    "devthrottle",
    "DEVTHROTTLE",
    r"D:\ReposFred\devthrottle",
    "d:/reposfred/devthrottle/",
])
def test_list_sessions_RepoFilter_MatchesFolderNameOrFullPath(serve, capsys, repo):
    serve(FLEET)

    session_ops.list_sessions(json_output=False, repo=repo)

    _, records = parse_list(capsys.readouterr().out, "sessions")
    assert [r["id"] for r in records] == [FLEET[i]["sessionId"] for i in (0, 2, 4, 5, 6)]


def test_list_sessions_MachineFilter_IgnoresCase(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=False, machine="DEVTHROTTLE-MAC-MINI")

    _, records = parse_list(capsys.readouterr().out, "sessions")
    assert [r["id"] for r in records] == [FLEET[1]["sessionId"]]


# ---------------------------------------------------------------------------------------------------
# --json keeps its shape
# ---------------------------------------------------------------------------------------------------


def test_list_sessions_JsonUnfiltered_ByteForByteTheRoster(serve, capsys):
    # Includes a row whose bucket this tool does not know: unfiltered --json never needs the fold,
    # so it prints what the Gateway sent, exactly as before.
    roster = FLEET + [_row("d2a4069f-1111-4111-8111-000000000007", "future", bucket="somethingNew")]
    serve(roster)

    session_ops.list_sessions(json_output=True)

    captured = capsys.readouterr()
    assert captured.out == json.dumps(roster, indent=2) + "\n"
    assert captured.err == ""


def test_list_sessions_JsonFiltered_SameBareArrayNarrowed(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=True, state="needs-you,crashed", repo="devthrottle")

    captured = capsys.readouterr()
    assert captured.out == json.dumps([FLEET[0], FLEET[4]], indent=2) + "\n"


def test_list_sessions_JsonRepoFilterAlone_ExcludesSameStateRowInAnotherRepo(serve, capsys):
    # FLEET[1] and FLEET[5] are both working; only the repository tells them apart. If --repo were
    # ignored under --json, FLEET[1] and FLEET[3] would come back and this fails.
    serve(FLEET)

    session_ops.list_sessions(json_output=True, repo="devthrottle")

    assert capsys.readouterr().out == json.dumps([FLEET[i] for i in (0, 2, 4, 5, 6)], indent=2) + "\n"


def test_list_sessions_JsonStateAndRepo_OnlyTheRowInThatRepo(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=True, state="working", repo="devthrottle")

    assert capsys.readouterr().out == json.dumps([FLEET[5]], indent=2) + "\n"


def test_list_sessions_JsonMachineFilter_MatchesThatMachineOnly(serve, capsys):
    # A real match, so a --json path that ignored --machine (every row) or answered [] both fail.
    serve(FLEET)

    session_ops.list_sessions(json_output=True, machine="devthrottle-MAC-mini")

    assert capsys.readouterr().out == json.dumps([FLEET[1]], indent=2) + "\n"


def test_list_sessions_JsonMachineFilter_ExcludesOtherMachines(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=True, machine="soren_north")

    assert json.loads(capsys.readouterr().out) == [FLEET[i] for i in (0, 2, 3, 4, 5, 6)]


def test_list_sessions_JsonNonAsciiCautions_StderrIsAscii(serve, capsys):
    serve([], complete=False, reason="Director on S\u00d8REN is offline",
          stale="Machine S\u00d8REN \u2014 quiet")

    session_ops.list_sessions(json_output=True)

    captured = capsys.readouterr()
    assert captured.out == "[]\n"
    assert captured.err.isascii()
    assert "Director on S\\u00d8REN is offline" in captured.err
    assert "Machine S\\u00d8REN \\u2014 quiet" in captured.err


def test_list_sessions_EnvelopeWithNoSessionsField_ExitsOne(monkeypatch, capsys):
    # Absent is not empty: the fetch refuses the answer, so nothing claims "no sessions".
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    monkeypatch.setattr(session_ops.gateway, "get_json", lambda path: {"rosterComplete": True})

    for json_output in (True, False):
        with pytest.raises(typer.Exit) as exc:
            session_ops.list_sessions(json_output=json_output)
        assert exc.value.exit_code == 1
        captured = capsys.readouterr()
        assert "[]" not in captured.out
        assert "no list of sessions" in " ".join((captured.out + captured.err).split())


def test_list_sessions_JsonFilterMatchesNothing_EmptyArray(serve, capsys):
    serve(FLEET)

    session_ops.list_sessions(json_output=True, machine="NO_SUCH_MACHINE")

    assert json.loads(capsys.readouterr().out) == []


def test_session_list_Cli_JsonWithFilter_CountsThroughAPipe(serve):
    serve(FLEET)

    result = runner.invoke(app, ["session", "list", "--state", "working", "--json"])

    assert result.exit_code == 0
    assert [s["sessionId"] for s in json.loads(result.stdout)] == [FLEET[1]["sessionId"], FLEET[5]["sessionId"]]


def test_list_sessions_JsonStateFilterWithUnknownBucket_ExitsOne(serve, capsys):
    serve(FLEET + [_row("d2a4069f-1111-4111-8111-000000000007", "future", bucket="somethingNew")])

    with pytest.raises(typer.Exit) as exc:
        session_ops.list_sessions(json_output=True, state="working")

    assert exc.value.exit_code == 1
    captured = capsys.readouterr()
    assert captured.out == ""
    assert "'somethingNew'" in captured.err


# ---------------------------------------------------------------------------------------------------
# Usage errors exit 2 and list the valid values
# ---------------------------------------------------------------------------------------------------


def test_session_list_Cli_UnknownState_ExitsTwoListingStates(serve):
    serve(FLEET)

    result = runner.invoke(app, ["session", "list", "--state", "waiting"])

    assert result.exit_code == 2
    assert "'waiting'" in result.stderr
    assert "needs-you, working, ready, snoozed, crashed" in result.stderr
    assert result.stdout == ""


def test_session_list_Cli_UnknownField_ExitsTwoListingFields(serve):
    serve(FLEET)

    result = runner.invoke(app, ["session", "list", "--fields", "id,colour"])

    assert result.exit_code == 2
    assert "colour" in result.stderr
    assert "id, name, state, repo, machine, number, model, agent, mission, path" in result.stderr


def test_session_list_Cli_FieldsWithJson_ExitsTwo(serve):
    serve(FLEET)

    result = runner.invoke(app, ["session", "list", "--json", "--fields", "id"])

    assert result.exit_code == 2
    assert "--fields does not apply to --json" in result.stderr


def test_session_list_Cli_UnknownFlag_ExitsTwo(serve):
    serve(FLEET)

    result = runner.invoke(app, ["session", "list", "--status", "working"])

    assert result.exit_code == 2


def test_session_list_Cli_EmptyRepo_ExitsTwo(serve):
    serve(FLEET)

    result = runner.invoke(app, ["session", "list", "--repo", " "])

    assert result.exit_code == 2
    assert "--repo needs a value" in result.stderr


# ---------------------------------------------------------------------------------------------------
# The state fold (Architect ruling 1)
# ---------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("bucket, activity, crashed, expected", [
    ("needsYou", "WaitingForInput", False, "needs-you"),
    ("needsYou", "Working", False, "needs-you"),
    ("onHold", "WaitingForInput", False, "snoozed"),
    ("onHold", "Working", False, "snoozed"),
    ("active", "Working", False, "working"),
    ("active", "WaitingForInput", False, "ready"),
    ("active", "Exited", False, "ready"),
    ("active", None, False, "ready"),
    ("active", "Working", True, "crashed"),
    ("needsYou", "Exited", True, "crashed"),
    ("onHold", "Exited", True, "crashed"),
    ("somethingNew", "Exited", True, "crashed"),
    ("active", "Working", "true", "working"),
])
def test_plain_state_EveryBucket_FoldsPerRuling(bucket, activity, crashed, expected):
    row = {"sessionId": "x", "triageBucket": bucket, "activityState": activity, "crashed": crashed}

    assert session_ops.plain_state(row) == expected


def test_plain_state_NeverReadsLastStatusReason():
    row = {"sessionId": "x", "triageBucket": "active", "activityState": "WaitingForInput",
           "lastStatusReason": "Working"}

    assert session_ops.plain_state(row) == "ready"


@pytest.mark.parametrize("bucket, named", [
    ("somethingNew", "'somethingNew'"),
    ("NeedsYou", "'NeedsYou'"),
    ("", "''"),
    (None, "missing"),
])
def test_plain_state_UnknownOrMissingBucket_RaisesNamingTheValue(bucket, named):
    row = {"sessionId": "abc-123", "activityState": "Working"}
    if bucket is not None:
        row["triageBucket"] = bucket

    with pytest.raises(session_ops.SessionStateError) as exc:
        session_ops.plain_state(row)

    assert named in str(exc.value)
    assert "abc-123" in str(exc.value)


def test_session_list_Cli_UnknownBucket_ExitsOneNamingIt(serve):
    serve(FLEET + [_row("d2a4069f-1111-4111-8111-000000000007", "future", bucket="somethingNew")])

    result = runner.invoke(app, ["session", "list"])

    assert result.exit_code == 1
    assert "'somethingNew'" in result.stderr
    assert "d2a4069f-1111-4111-8111-000000000007" in result.stderr
    assert result.stdout == ""
