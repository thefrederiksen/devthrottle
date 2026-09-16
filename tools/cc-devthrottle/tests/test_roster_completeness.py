"""Tests for issue #1051: the command line never presents a partial roster as the whole fleet.

The Gateway drops an unreachable Director's sessions and still answers 200, so a short roster is
indistinguishable from a complete one. Three sentences in these tools were flatly false in that state:

  * "No session matches '<id>'" - it may exist perfectly well on the machine we could not read.
  * "No sessions are running in the fleet" - a claim about the whole fleet from a partial list.
  * "(no sessions running)" - the same claim in cc-status.

Each reads as a fact about the world and was only ever a fact about the bytes that arrived. The two
readings call for opposite next steps - give up, or go and look at that machine - which is exactly why
the difference has to be said out loud.

The verdict itself is folded on the Director (ControlEndpoints.FoldRosterCompleteness) and rendered
here verbatim. These tests pin the rendering and the three-state handling, not the ruling.
"""

import sys
from pathlib import Path

import pytest
import typer

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import gateway  # noqa: E402
from src import session_ops  # noqa: E402

SESSION_ID = "11111111-2222-3333-4444-555555555555"
OFFLINE_REASON = "1 Director could not be reached, so its sessions below are the last it reported and may be out of date - MACHINE_B, last seen 4m ago"


@pytest.fixture
def fleet(monkeypatch):
    """Serve a chosen roster and completeness verdict, with no real HTTP."""

    def serve(sessions, complete, reason=None, stale=None):
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
        monkeypatch.setattr(session_ops, "_get_fleet", lambda: (sessions, complete, reason, stale))

    return serve


def _row():
    return {"sessionId": SESSION_ID, "name": "worker", "machineName": "MACHINE_A",
            "repoPath": r"D:\repo", "activityState": "Working", "triageBucket": "active"}


# ===== the caveat itself: three states, and absent is not complete =====

def test_a_complete_roster_says_nothing_extra():
    assert gateway.roster_caveat(True, None) == ""


def test_an_incomplete_roster_reports_the_directors_own_reason():
    assert gateway.roster_caveat(False, OFFLINE_REASON) == OFFLINE_REASON


def test_an_incomplete_roster_with_no_reason_still_warns():
    """Never go silent just because the reason was missing - not-complete is the load-bearing fact.

    This asserted the WORD "incomplete" until epic #1159 step A, and the word had to go: the roster no
    longer drops an unreachable Director's rows, so "this list may be incomplete" described an
    implementation that no longer exists. The fallback was reworded to the caveat that is still true -
    those rows are the last thing that machine said - and this test was left pinning the retired word,
    so it failed on the branch and passed on main. It now pins the two properties the test exists for
    and neither of them is a wording: it says SOMETHING, and it says the not-complete thing rather than
    the cannot-vouch thing. Both wordings can be edited again without this going red for no reason,
    and going silent or collapsing the two states still reddens it.
    """
    caveat = gateway.roster_caveat(False, None)

    assert caveat != ""
    assert "could not be reached" in caveat
    assert caveat != gateway.roster_caveat(None, None)


def test_an_unknown_verdict_is_not_treated_as_complete():
    # THE POINT OF THE WHOLE ISSUE, applied to ourselves. A Director that has not restarted since this
    # was added serves the bare array and cannot vouch either way. Reading that silence as "complete"
    # would rebuild the exact defect being fixed: absent reading identical to empty.
    caveat = gateway.roster_caveat(None, None)
    assert caveat != ""
    assert "cannot confirm" in caveat


# ===== "no session matches" must not imply "it does not exist" =====

def test_failed_resolve_on_an_incomplete_roster_says_the_list_may_be_short(fleet, capsys):
    fleet([], False, OFFLINE_REASON)

    with pytest.raises(typer.Exit):
        session_ops._resolve_target("abc123", command_name="cc-devthrottle session done")

    out = capsys.readouterr().out
    assert "No session matches" in out
    assert "may be incomplete" in out
    assert "MACHINE_B" in out        # names the machine to go and look at


def test_failed_resolve_on_a_complete_roster_stays_terse(fleet, capsys):
    # The counterpart: when the Director vouches for the roster, "no session matches" IS the whole truth
    # and must not be padded with a hedge that would train the reader to ignore it.
    fleet([], True, None)

    with pytest.raises(typer.Exit):
        session_ops._resolve_target("abc123", command_name="cc-devthrottle session done")

    out = capsys.readouterr().out
    assert "No session matches" in out
    assert "may be incomplete" not in out


# ===== "no sessions in the fleet" is a claim a partial list cannot support =====

def test_empty_and_incomplete_does_not_claim_the_fleet_is_empty(fleet, capsys):
    fleet([], False, OFFLINE_REASON)

    session_ops.list_sessions(json_output=False)

    out = capsys.readouterr().out
    assert "not the whole fleet" in out
    assert "No sessions are running in the fleet" not in out


def test_empty_and_complete_still_says_the_fleet_is_empty(fleet, capsys):
    fleet([], True, None)

    session_ops.list_sessions(json_output=False)

    assert "No sessions are running in the fleet" in capsys.readouterr().out


def test_a_listed_roster_that_is_incomplete_is_qualified_after_the_table(fleet, capsys):
    fleet([_row()], False, OFFLINE_REASON)

    session_ops.list_sessions(json_output=False)

    out = capsys.readouterr().out
    assert "11111111" in out                 # the rows the reader can trust come first
    assert "not the whole fleet" in out


def test_a_listed_roster_that_is_complete_is_not_qualified(fleet, capsys):
    fleet([_row()], True, None)

    session_ops.list_sessions(json_output=False)

    assert "not the whole fleet" not in capsys.readouterr().out


# ===== the machine-readable path keeps its shape =====

def test_json_output_stays_a_bare_array_and_warns_on_stderr(fleet, capsys):
    # Agents and pipes parse stdout, so the shape must not change - but a caller acting on a partial
    # roster still has to be told. stderr carries the warning without corrupting the parse.
    import json

    fleet([_row()], False, OFFLINE_REASON)

    session_ops.list_sessions(json_output=True)

    captured = capsys.readouterr()
    parsed = json.loads(captured.out)         # would raise if the warning had gone to stdout
    assert isinstance(parsed, list)
    assert parsed[0]["sessionId"] == SESSION_ID
    assert "WARNING" in captured.err
    assert "MACHINE_B" in captured.err


def test_json_output_on_a_complete_roster_writes_nothing_to_stderr(fleet, capsys):
    fleet([_row()], True, None)

    session_ops.list_sessions(json_output=True)

    captured = capsys.readouterr()
    assert captured.err == ""


# ===== the transport: an older Director cannot be mistaken for a vouching one =====

def test_get_fleet_reads_the_envelope(monkeypatch):
    monkeypatch.setattr(gateway, "get_json", lambda path: {
        "sessions": [_row()],
        "rosterComplete": False,
        "rosterIncompleteReason": OFFLINE_REASON,
    })

    sessions, complete, reason, _ = gateway.get_fleet()

    assert len(sessions) == 1
    assert complete is False
    assert reason == OFFLINE_REASON


def test_get_fleet_asks_for_the_envelope(monkeypatch):
    seen = {}

    def get_json(path):
        seen["path"] = path
        return {"sessions": [], "rosterComplete": True}

    monkeypatch.setattr(gateway, "get_json", get_json)
    gateway.get_fleet()

    assert "envelope=true" in seen["path"]


def test_get_fleet_tolerates_an_older_director_serving_a_bare_array(monkeypatch):
    # A running Director is long-lived and the command line is invoked fresh, so a newer tool WILL meet
    # an older Director. It must still work - and must report "cannot vouch", never "complete".
    monkeypatch.setattr(gateway, "get_json", lambda path: [_row()])

    sessions, complete, reason, stale = gateway.get_fleet()

    assert len(sessions) == 1
    assert complete is None
    assert reason is None
    assert stale is None


def test_get_fleet_ignores_a_non_boolean_completeness_value(monkeypatch):
    # A malformed field must degrade to "cannot vouch", not to a truthy string reading as complete.
    monkeypatch.setattr(gateway, "get_json", lambda path: {"sessions": [], "rosterComplete": "yes"})

    _, complete, _, _ = gateway.get_fleet()

    assert complete is None
