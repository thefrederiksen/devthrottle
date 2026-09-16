"""Tests for `cc-devthrottle` with no arguments (issue #2922, docs/axi-standard.md principle 8).

With no arguments the tool shows live state instead of the help screen:

- inside a session (CC_SESSION_ID set), this session's full id, full name and state, read back
  exactly with `parse_list`; the old no-argument output (the help screen) fails the same check;
- outside a session, a plain sentence saying so, and no identity at all;
- the fleet count with its breakdown, and the number of sessions needing the owner, zero included;
- `help[]` lines with the next commands;
- an unreachable Gateway or a malformed roster exits 1 with a sentence an agent can act on;
- `--help` and `--version` are unchanged and never reach the Gateway.
"""

import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import ListParseError, parse_list  # noqa: E402
from src import session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

MY_ID = "2e7b6504-4fc2-44bd-9bb3-cebcccba554b"
MY_NAME = 'AXI Tools - Worker - no-args, "live" state Søren \U0001f680'


def _row(sid, name, *, bucket, activity="WaitingForInput", crashed=False, repo="/Users/soren/ReposFred/devthrottle"):
    return {
        "sessionId": sid,
        "number": 100,
        "name": name,
        "machineName": "devthrottle-mac-mini",
        "repoPath": repo,
        "activityState": activity,
        "triageBucket": bucket,
        "crashed": crashed,
        "lastStatusReason": "Waiting for you",  # never read: a status sentence is not a state
    }


FLEET = [
    _row("aaaaaaaa-0000-4000-8000-000000000001", "first", bucket="needsYou"),
    _row(MY_ID, MY_NAME, bucket="active", activity="Working", repo="/Users/soren/ReposFred/devthrottle-axi-noargs"),
    _row("aaaaaaaa-0000-4000-8000-000000000003", "third", bucket="needsYou"),
    _row("aaaaaaaa-0000-4000-8000-000000000004", "fourth", bucket="active"),
    _row("aaaaaaaa-0000-4000-8000-000000000005", "fifth", bucket="onHold"),
    _row("aaaaaaaa-0000-4000-8000-000000000006", "sixth", bucket="needsYou", crashed=True),
]

NOBODY_NEEDS_YOU = [
    _row("bbbbbbbb-0000-4000-8000-000000000001", "one", bucket="active", activity="Working"),
    _row("bbbbbbbb-0000-4000-8000-000000000002", "two", bucket="onHold"),
]


@pytest.fixture
def serve(monkeypatch):
    """Serve a chosen roster through the one shared fetch, with no real HTTP, as a chosen session."""

    def serve_(sessions, *, session_id=None, complete=True, reason=None, stale=None):
        if session_id is None:
            monkeypatch.delenv("CC_SESSION_ID", raising=False)
        else:
            monkeypatch.setenv("CC_SESSION_ID", session_id)
        monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: (sessions, complete, reason, stale))

    return serve_


@pytest.fixture
def no_fetch(monkeypatch):
    """Fail the test if anything asks the Gateway for the fleet."""

    def refuse():
        raise AssertionError("the Gateway was asked for the fleet")

    monkeypatch.setattr(session_ops.gateway, "get_fleet", refuse)


def _invoke(args):
    result = runner.invoke(app, args, prog_name="cc-devthrottle")
    if result.exception and not isinstance(result.exception, SystemExit):
        raise result.exception
    return result


# ---------------------------------------------------------------------------------------------------
# Inside and outside a session
# ---------------------------------------------------------------------------------------------------


def test_no_args_InsideSession_FullIdNameAndStateReadBackExactly(serve):
    serve(FLEET, session_id=MY_ID)

    result = _invoke([])

    assert result.exit_code == 0
    assert result.stdout.isascii()
    fields, records = parse_list(result.stdout, "session")
    assert fields == ["id", "name", "state", "repo"]
    assert records == [{"id": MY_ID, "name": MY_NAME, "state": "working", "repo": "devthrottle-axi-noargs"}]


def test_recoverability_check_OldNoArgumentOutput_Fails(no_fetch):
    # Before #2922 the tool printed its help screen with no arguments; `--help` still prints exactly
    # that, and no session can be read back out of it.
    old_output = _invoke(["--help"]).stdout

    with pytest.raises(ListParseError):
        parse_list(old_output, "session")
    assert MY_ID not in old_output


def test_no_args_SessionIdInOtherCase_StillFound(serve):
    serve(FLEET, session_id=MY_ID.upper())

    result = _invoke([])

    _, records = parse_list(result.stdout, "session")
    assert records[0]["id"] == MY_ID


def test_no_args_OutsideSession_SaysSoAndNamesNobody(serve):
    serve(FLEET)

    result = _invoke([])

    assert result.exit_code == 0
    lines = result.stdout.splitlines()
    assert lines[0] == (
        "session: none - CC_SESSION_ID is not set, so this is not running inside a DevThrottle session."
    )
    with pytest.raises(ListParseError):
        parse_list(result.stdout, "session")
    assert not any(s["sessionId"] in result.stdout for s in FLEET)
    assert "session spawn" not in result.stdout


def test_no_args_BlankSessionId_TreatedAsOutsideSession(serve, monkeypatch):
    serve(FLEET)
    monkeypatch.setenv("CC_SESSION_ID", "   ")

    result = _invoke([])

    assert result.stdout.startswith("session: none - ")


def test_no_args_SessionNotInRoster_SaysNotFoundAndGuessesNothing(serve):
    # A prefix of a real id must never be matched: only the full id names this session.
    serve(FLEET, session_id="aaaaaaaa")

    result = _invoke([])

    assert result.exit_code == 0
    assert result.stdout.splitlines()[0] == (
        "session: aaaaaaaa - this is CC_SESSION_ID, but the fleet list the Gateway returned does not hold it."
    )
    with pytest.raises(ListParseError):
        parse_list(result.stdout, "session")
    assert "first" not in result.stdout


def test_no_args_SessionIdWithComma_IsQuotedInTheNotFoundLine(serve):
    serve(FLEET, session_id="odd,id")

    result = _invoke([])

    assert result.stdout.startswith('session: "odd,id" - ')


# ---------------------------------------------------------------------------------------------------
# Counts
# ---------------------------------------------------------------------------------------------------


def test_no_args_NeedsYou_CountsOnlyNeedsYouAndNotCrashed(serve):
    serve(FLEET, session_id=MY_ID)

    result = _invoke([])

    lines = result.stdout.splitlines()
    assert "count: 6 (needs-you 2, working 1, ready 1, snoozed 1, crashed 1)" in lines
    assert "needs-you: 2" in lines
    assert "  cc-devthrottle session list --state needs-you" in lines


def test_no_args_NobodyNeedsYou_SaysZero(serve):
    serve(NOBODY_NEEDS_YOU)

    result = _invoke([])

    lines = result.stdout.splitlines()
    assert "count: 2 (working 1, snoozed 1)" in lines
    assert "needs-you: 0" in lines
    assert "--state needs-you" not in result.stdout


def test_no_args_EmptyRoster_PrintsCountZero(serve):
    serve([])

    result = _invoke([])

    assert result.exit_code == 0
    assert result.stdout == (
        "session: none - CC_SESSION_ID is not set, so this is not running inside a DevThrottle session.\n"
        "count: 0\n"
        "needs-you: 0\n"
        "help[2]:\n"
        "  cc-devthrottle director list\n"
        "  cc-devthrottle --help\n"
    )


def test_no_args_InsideSession_ExactOutput(serve):
    serve(NOBODY_NEEDS_YOU, session_id="bbbbbbbb-0000-4000-8000-000000000001")

    result = _invoke([])

    assert result.stdout == (
        "session[1]{id,name,state,repo}:\n"
        "  bbbbbbbb-0000-4000-8000-000000000001,one,working,devthrottle\n"
        "count: 2 (working 1, snoozed 1)\n"
        "needs-you: 0\n"
        "help[3]:\n"
        "  cc-devthrottle session list\n"
        "  cc-devthrottle session spawn <repo> --controlled-by self\n"
        "  cc-devthrottle --help\n"
    )


def test_no_args_IncompleteRoster_SaysSoInAscii(serve):
    serve(FLEET, complete=False, reason="The machine SOREN_NORTH — did not answer.")

    result = _invoke([])

    assert result.exit_code == 0
    assert result.stdout.isascii()
    assert "This is not the whole fleet. The machine SOREN_NORTH \\u2014 did not answer." in result.stdout


def test_no_args_StaleCaution_OnlyWhenNobodyNeedsYou(serve):
    caution = "A machine has not reported recently; a session may be missing."
    serve(NOBODY_NEEDS_YOU, stale=caution)
    assert caution in _invoke([]).stdout

    serve(FLEET, stale=caution)
    assert caution not in _invoke([]).stdout


# ---------------------------------------------------------------------------------------------------
# Failures
# ---------------------------------------------------------------------------------------------------


def test_no_args_GatewayUnreachable_ExitsOneWithActionableMessage(monkeypatch):
    # A real refused connection, not a stub: port 9 on loopback has nothing listening.
    monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:9")
    monkeypatch.setenv("CC_SESSION_ID", MY_ID)

    result = _invoke([])

    assert result.exit_code == 1
    assert result.stdout == ""
    assert result.stderr.isascii()
    assert "Error: Cannot reach the Gateway at http://127.0.0.1:9" in result.stderr
    assert "Run cc-devthrottle setup status to check this machine" in result.stderr


def test_no_args_NoGatewayConfigured_ExitsOne(monkeypatch):
    monkeypatch.delenv("CC_GATEWAY_URL", raising=False)

    result = _invoke([])

    assert result.exit_code == 1
    assert "CC_GATEWAY_URL is not set" in result.stderr


def test_no_args_NonAsciiGatewayError_IsWrittenAsAscii(monkeypatch):
    def fail():
        raise session_ops.gateway.GatewayError("The Gateway said — no.")

    monkeypatch.setattr(session_ops.gateway, "get_fleet", fail)

    result = _invoke([])

    assert result.exit_code == 1
    assert result.stderr.isascii()
    assert "The Gateway said \\u2014 no." in result.stderr


def test_no_args_EnvelopeWithNoSessionsField_ExitsOne(monkeypatch):
    monkeypatch.setattr(session_ops.gateway, "get_json", lambda path, timeout=30: {"rosterComplete": True})

    result = _invoke([])

    assert result.exit_code == 1
    assert result.stdout == ""
    assert "has no list of sessions" in result.stderr


def test_no_args_RowWithNoSessionId_ExitsOne(serve):
    serve(FLEET + [_row("", "orphan", bucket="active")], session_id=MY_ID)

    result = _invoke([])

    assert result.exit_code == 1
    assert result.stdout == ""
    assert "no session id" in result.stderr


def test_no_args_UnknownBucket_ExitsOneNamingTheValue(serve):
    serve(FLEET + [_row("cccccccc-0000-4000-8000-000000000001", "odd", bucket="parked")], session_id=MY_ID)

    result = _invoke([])

    assert result.exit_code == 1
    assert result.stdout == ""
    assert "'parked'" in result.stderr


def test_UnknownTopLevelFlag_ExitsTwo(no_fetch):
    result = _invoke(["--bogus"])

    assert result.exit_code == 2
    assert "--bogus" in result.stderr


# ---------------------------------------------------------------------------------------------------
# --help and --version are unchanged
# ---------------------------------------------------------------------------------------------------


def test_help_StillShowsTheHelpAndNeverFetches(no_fetch, plain):
    result = _invoke(["--help"])

    assert result.exit_code == 0
    text = plain(result.stdout)
    assert "Usage: cc-devthrottle [OPTIONS] COMMAND [ARGS]..." in text
    assert "Unified DevThrottle command-line surface." in text
    for command in ("session", "message", "mission", "director", "setup", "actions"):
        assert command in text
    assert "count:" not in text


def test_version_StillPrintsTheVersionAndNeverFetches(no_fetch, plain):
    result = _invoke(["--version"])

    assert result.exit_code == 0
    assert plain(result.stdout).startswith("cc-devthrottle v")


def test_subcommand_NeverShowsLiveState(serve):
    serve(FLEET, session_id=MY_ID)

    result = _invoke(["session", "list"])

    assert result.exit_code == 0
    assert "needs-you: " not in result.stdout
    parse_list(result.stdout, "sessions")
