"""Free text is always escaped to one line of ASCII (issue #2922 step 6a).

Several commands escaped a Gateway sentence, a filter value or a name only when it held a non-ASCII
character (`text if text.isascii() else escape_ascii(text)`). A control character is ASCII, so a
newline, a carriage return or an escape sequence passed straight through: it split a line, or moved
the terminal's cursor, in output an agent reads line by line. Every such site now escapes always.

Each test here puts control characters - and nothing else unusual - into one piece of free text and
checks the line it lands on stays whole. With the old conditional escaping each of them fails.
"""

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import mission_ops, repo_ops, schedule_ops, session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

# ASCII only, so `str.isascii()` is true: newline, carriage return, tab, escape and bell.
CONTROL = "first\nsecond\rthird\tfourth\x1b[31mred\x07"
CONTROL_ESCAPED = "first\\nsecond\\rthird\\tfourth\\u001b[31mred\\u0007"


def _invoke(args):
    return runner.invoke(app, args, prog_name="cc-devthrottle")


def _error_text(result):
    try:
        return result.stderr
    except ValueError:
        return result.output


def _stdout(result):
    try:
        result.stderr
    except ValueError:
        return result.output
    return result.stdout


def _assert_clean(text):
    for ch in text:
        assert ch == "\n" or 0x20 <= ord(ch) <= 0x7E, repr(text)


def _line_with(text, needle):
    lines = [line for line in text.splitlines() if needle in line]
    assert len(lines) == 1, repr(text)
    return lines[0]


def test_control_fixture_IsAscii():
    # The whole point: the old check let these through because they are ASCII.
    assert CONTROL.isascii()


# ----- session list -----------------------------------------------------------------------------------

ROW = {
    "sessionId": "d2a4069f-1111-4111-8111-000000000001",
    "name": "worker",
    "machineName": "devthrottle-mac-mini",
    "repoPath": "/Users/soren/ReposFred/devthrottle",
    "activityState": "Working",
    "triageBucket": "active",
    "crashed": False,
}


@pytest.fixture
def fleet(monkeypatch):
    def serve(sessions, complete=True, reason=None, stale=None):
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
        monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: (sessions, complete, reason, stale))

    return serve


def test_session_list_IncompleteRosterReason_StaysOneLine(fleet):
    fleet([ROW], complete=False, reason=CONTROL)

    result = _invoke(["session", "list"])

    assert result.exit_code == 0, result.output
    _assert_clean(result.output)
    assert _line_with(result.output, "This is not the whole fleet.") == f"This is not the whole fleet. {CONTROL_ESCAPED}"


def test_session_list_StaleCautionOnEmptyAnswer_StaysOneLine(fleet):
    fleet([], stale=CONTROL)

    result = _invoke(["session", "list"])

    assert result.exit_code == 0, result.output
    _assert_clean(result.output)
    assert CONTROL_ESCAPED in result.output.splitlines()


def test_session_list_Json_WarningsOnStandardError_StayOneLineEach(fleet):
    fleet([], complete=False, reason=CONTROL, stale=CONTROL)

    result = _invoke(["session", "list", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(_stdout(result)) == []
    err = _error_text(result)
    _assert_clean(err)
    assert f"WARNING: the fleet list may be incomplete. {CONTROL_ESCAPED}" in err.splitlines()
    assert f"WARNING: {CONTROL_ESCAPED}" in err.splitlines()


def test_session_list_GatewayError_IsOnePlainLine(monkeypatch):
    def fail():
        raise session_ops.gateway.GatewayError(f"[/tmp/x] {CONTROL}")

    monkeypatch.setattr(session_ops.gateway, "get_fleet", fail)

    result = _invoke(["session", "list"])

    assert result.exit_code == 1
    err = _error_text(result)
    _assert_clean(err)
    # Plain text: the bracketed token is printed as written, not read as Rich markup.
    assert err.splitlines()[0] == f"Error: [/tmp/x] {CONTROL_ESCAPED}"


# ----- cc-devthrottle with no arguments -----------------------------------------------------------------


def test_live_state_IncompleteRosterReason_StaysOneLine(fleet):
    fleet([ROW], complete=False, reason=CONTROL)

    result = _invoke([])

    assert result.exit_code == 0, result.output
    _assert_clean(result.output)
    assert _line_with(result.output, "This is not the whole fleet.") == f"This is not the whole fleet. {CONTROL_ESCAPED}"


def test_live_state_StaleCautionWhenNobodyNeedsYou_StaysOneLine(fleet):
    fleet([ROW], stale=CONTROL)

    result = _invoke([])

    assert result.exit_code == 0, result.output
    _assert_clean(result.output)
    assert CONTROL_ESCAPED in result.output.splitlines()


def test_live_state_GatewayError_StaysOneLine(monkeypatch):
    def fail():
        raise session_ops.gateway.GatewayError(CONTROL)

    monkeypatch.setattr(session_ops.gateway, "get_fleet", fail)

    result = _invoke([])

    assert result.exit_code == 1
    err = _error_text(result)
    _assert_clean(err)
    assert err.splitlines()[0] == f"Error: {CONTROL_ESCAPED}"


# ----- session stop: an ambiguous target ----------------------------------------------------------------


def test_session_stop_AmbiguousTarget_NamesStayOneLineEach(monkeypatch, plain):
    rows = [
        dict(ROW, sessionId="9c41e7a2-0000-0000-0000-000000000001", name=CONTROL, machineName="[bold]A"),
        dict(ROW, sessionId="9c41e7a2-0000-0000-0000-000000000002", name="other"),
    ]
    monkeypatch.setattr(session_ops, "_get_fleet", lambda: (rows, True, None, None))

    result = _invoke(["session", "stop", "9c41e7a2", "--reason", "tidying up"])

    assert result.exit_code == 1
    # This verb prints through Rich, which may colour it; the colour codes are its own, so they are
    # removed before looking for any the name smuggled in.
    out = plain(result.output)
    _assert_clean(out)
    assert _line_with(out, "9c41e7a2  first").strip() == f"9c41e7a2  {CONTROL_ESCAPED}  ([bold]A)"


# ----- mission list -------------------------------------------------------------------------------------

MISSION = {
    "missionId": "aaaaaaaa-1111-4111-8111-000000000001",
    "missionName": "one",
    "why": "",
    "whyUpdatedAt": None,
    "state": "active",
    "stateChangedAt": None,
    "workflowRunId": None,
}


@pytest.fixture
def missions(monkeypatch):
    def serve(result):
        def list_all(self, state=None):
            if isinstance(result, Exception):
                raise result
            return list(result)

        monkeypatch.setattr(mission_ops.MissionClient, "__init__", lambda self, base_url=None: None)
        monkeypatch.setattr(mission_ops.MissionClient, "list_all", list_all)

    return serve


def test_mission_list_NameFilterMatchingNothing_StaysOneLine(missions):
    missions([MISSION])

    result = _invoke(["mission", "list", "--name", CONTROL])

    assert result.exit_code == 0, result.output
    _assert_clean(result.output)
    assert _line_with(result.output, "No missions with") == (
        f"No missions with state 'active' and a name containing '{CONTROL_ESCAPED}'."
    )


@pytest.mark.parametrize("args", [["mission", "list"], ["mission", "list", "--json"]])
def test_mission_list_GatewayError_StaysOneLine(missions, args):
    missions(mission_ops.GatewayError(CONTROL))

    result = _invoke(args)

    assert result.exit_code == 1
    err = _error_text(result)
    _assert_clean(err)
    assert err.splitlines()[0] == f"Error: {CONTROL_ESCAPED}"


@pytest.mark.parametrize(
    "row,expected",
    [
        (dict(MISSION, missionId=CONTROL, state="paused"), f"mission {CONTROL_ESCAPED} with state"),
        (dict(MISSION, missionId=CONTROL, missionName=None), f"mission {CONTROL_ESCAPED} with no mission name"),
        (dict(MISSION, missionId=CONTROL, missionName=" "), f"mission {CONTROL_ESCAPED} with a blank"),
    ],
)
def test_mission_list_BrokenRow_IdInTheErrorStaysOneLine(missions, row, expected):
    missions([row])

    result = _invoke(["mission", "list"])

    assert result.exit_code == 1
    err = _error_text(result)
    _assert_clean(err)
    assert len(err.splitlines()) == 1, err
    assert expected in err


# ----- schedule list ------------------------------------------------------------------------------------


@pytest.fixture
def schedules(monkeypatch):
    def serve(result):
        def list_jobs(self):
            if isinstance(result, Exception):
                raise result
            return result

        monkeypatch.setattr(schedule_ops.ScheduleClient, "__init__", lambda self, base_url=None: None)
        monkeypatch.setattr(schedule_ops.ScheduleClient, "list_jobs", list_jobs)

    return serve


@pytest.mark.parametrize("args", [["schedule", "list"], ["schedule", "list", "--json"]])
def test_schedule_list_GatewayError_StaysOneLine(schedules, args):
    schedules(schedule_ops.GatewayError(CONTROL))

    result = _invoke(args)

    assert result.exit_code == 1
    err = _error_text(result)
    _assert_clean(err)
    assert err.splitlines()[0] == f"Error: {CONTROL_ESCAPED}"


def test_schedule_list_EnabledNotABoolean_ValueIsWrittenAsAscii(schedules):
    schedules([{"id": "cj_1", "name": "n", "enabled": "ja \u00e6", "target": {"machine": "M"}}])

    result = _invoke(["schedule", "list"])

    assert result.exit_code == 1
    err = _error_text(result)
    _assert_clean(err)
    assert "enabled 'ja \\u00e6'; it must be true or false" in err


# ----- repo list and worktree list ----------------------------------------------------------------------


@pytest.mark.parametrize("command", ["repo", "worktree"])
@pytest.mark.parametrize("json_flag", [[], ["--json"]])
def test_repo_and_worktree_list_GatewayError_StaysOneLine(monkeypatch, command, json_flag):
    def fail(path):
        raise repo_ops.gateway.GatewayError(CONTROL)

    monkeypatch.setattr(repo_ops.gateway, "get_json", fail)

    result = _invoke([command, "list", *json_flag])

    assert result.exit_code == 1
    err = _error_text(result)
    _assert_clean(err)
    assert err.splitlines()[0] == f"Error: {CONTROL_ESCAPED}"


@pytest.mark.parametrize("command,path", [("repo", "repositories"), ("worktree", "worktrees")])
def test_repo_and_worktree_list_ErrorInTheAnswer_StaysOneLine(monkeypatch, command, path):
    monkeypatch.setattr(repo_ops.gateway, "get_json", lambda asked: {"error": CONTROL})

    result = _invoke([command, "list"])

    assert result.exit_code == 1
    err = _error_text(result)
    _assert_clean(err)
    assert err.splitlines()[0] == f"Error: {CONTROL_ESCAPED}"
