"""Tests for `cc-devthrottle mission rename | complete | remove | reopen | list --all` (Phase 3).

These are the verbs that let an AGENT end a body of work, not just the owner from the Cockpit. The
gap they close is the state the owner actually found the fleet in: eleven missions, several finished
days earlier, with no way out of the list - because nothing anywhere could end one.

The cases assert the decisions, because each was settled deliberately:

  * a rename NAMES THE OLD NAME, so it is visible WHICH mission moved;
  * a rename keeps the id, and the message says so - that is why attached sessions do not move;
  * complete and remove are DIFFERENT ENDINGS, and each says where the record went and how to get
    it back, because the Cockpit has no archive view yet and this is the only way to see it;
  * resolution sees ENDED missions, or a completed mission could never be reopened;
  * the Gateway's note about the workflow run is passed through VERBATIM, never re-worded here.

No HTTP happens: the Gateway calls are stubbed.
"""

import re
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import mission_ops, usage_errors  # noqa: E402

# Rich emits colour when its console decides the destination can take it, and that decision differs
# between a developer's machine and continuous integration. The escapes land INSIDE the sentences
# asserted on here, so a substring a reader plainly sees on screen is absent from the captured
# string. That is what made these three tests pass locally and fail in continuous integration.
ANSI = re.compile(r"\x1b\[[0-9;]*m")


def flowed(text: str) -> str:
    """Strip Rich's colour and collapse its wrapping, so an assertion is about WORDS.

    Two pieces of presentation come out, for one reason: neither is the message. Colour varies with
    where the output is going and the wrap column varies with console width, so an assertion that
    fails on either is testing the terminal rather than what was said.
    """
    return " ".join(ANSI.sub("", text).split())


ACTIVE_ID = "aaaaaaaa-1111-2222-3333-444444444444"
DONE_ID = "cccccccc-5555-6666-7777-888888888888"

# Every field the Gateway's MissionDto carries, as it sends them: `mission list` refuses a row missing one.
ACTIVE = {"missionId": ACTIVE_ID, "missionName": "Release 2.0.0", "state": "active", "why": "ship it",
          "whyUpdatedAt": "2026-09-01T07:00:00+00:00", "stateChangedAt": None, "workflowRunId": None}
DONE = {"missionId": DONE_ID, "missionName": "Remove the network port", "state": "complete", "why": "",
        "whyUpdatedAt": None, "stateChangedAt": "2026-09-02T07:00:00+00:00", "workflowRunId": None}


@pytest.fixture
def wired(monkeypatch):
    """Stub the mission list and the PATCH; record every patch call."""
    patches = []
    listed_states = []

    def fake_list_all(self, state=None):
        listed_states.append(state)
        # The Gateway's own default is active-only; "all" is what includes an ended mission.
        return [ACTIVE, DONE] if state == "all" else [ACTIVE]

    def fake_patch(self, mission_id, body):
        patches.append({"missionId": mission_id, **body})
        before = ACTIVE if mission_id == ACTIVE_ID else DONE
        updated = dict(before)
        if "missionName" in body:
            updated["missionName"] = body["missionName"].strip()
        if "state" in body:
            updated["state"] = body["state"]
        return {"mission": updated, "note": None, "attachedSessionCount": 0}

    monkeypatch.setattr(mission_ops.MissionClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(mission_ops.MissionClient, "list_all", fake_list_all)
    monkeypatch.setattr(mission_ops.MissionClient, "patch", fake_patch)
    return {"patches": patches, "listed_states": listed_states}


# ---- rename ----------------------------------------------------------------------------------


def test_rename_sends_the_new_name_and_reports_the_old_one(wired, capsys):
    mission_ops.rename_mission(ACTIVE_ID, "  Release 2.0.1  ")

    assert wired["patches"] == [{"missionId": ACTIVE_ID, "missionName": "  Release 2.0.1  "}]

    out = flowed(capsys.readouterr().out)
    # Both names, so it is visible WHICH mission moved - the same reason attach names what a session left.
    assert '"Release 2.0.0" to "Release 2.0.1"' in out
    # And the reassurance that matters: renaming does not detach anything.
    assert "id is unchanged" in out


def test_rename_refuses_a_blank_name_without_calling_the_gateway(wired):
    with pytest.raises(usage_errors.CommandUsageError):
        mission_ops.rename_mission(ACTIVE_ID, "   ")

    assert wired["patches"] == []


# ---- the two endings -------------------------------------------------------------------------


def test_complete_sends_complete_and_says_where_the_record_went(wired, capsys):
    mission_ops.end_mission(ACTIVE_ID, "complete")

    assert wired["patches"] == [{"missionId": ACTIVE_ID, "state": "complete"}]

    out = flowed(capsys.readouterr().out)
    assert "Completed" in out
    # An ending that reports only success leaves the owner unable to find the record afterwards.
    assert "mission list --state complete" in out
    assert "mission reopen" in out


def test_remove_sends_removed_and_is_a_different_ending(wired, capsys):
    mission_ops.end_mission(ACTIVE_ID, "removed")

    assert wired["patches"] == [{"missionId": ACTIVE_ID, "state": "removed"}]

    out = flowed(capsys.readouterr().out)
    assert "Removed" in out
    assert "Completed" not in out


def test_reopen_returns_a_mission_to_active(wired, capsys):
    mission_ops.reopen_mission(DONE_ID)

    assert wired["patches"] == [{"missionId": DONE_ID, "state": "active"}]
    assert "Reopened" in flowed(capsys.readouterr().out)


# ---- resolution has to see ended missions ----------------------------------------------------


def test_an_ended_mission_can_still_be_resolved(wired, capsys):
    """The regression this guards: resolving against the ACTIVE-only list would answer 'no mission
    matches' for a completed mission that is plainly there - making reopen impossible."""
    mission_ops.reopen_mission("Remove the network port")

    assert wired["patches"] == [{"missionId": DONE_ID, "state": "active"}]
    # It asked for every state, not the default.
    assert "all" in wired["listed_states"]


# ---- the Gateway's note is passed through ----------------------------------------------------


def test_the_gateways_note_is_printed_verbatim(monkeypatch, capsys):
    note = "The mission was ended, but its workflow run could not be closed with it."

    monkeypatch.setattr(mission_ops.MissionClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(mission_ops.MissionClient, "list_all", lambda self, state=None: [ACTIVE])
    monkeypatch.setattr(
        mission_ops.MissionClient, "patch",
        lambda self, mission_id, body: {"mission": dict(ACTIVE, state="complete"), "note": note},
    )

    mission_ops.end_mission(ACTIVE_ID, "complete")

    # Verbatim. Only the Gateway knows what happened to the run; a client that re-worded it would be
    # writing its own account of a decision it did not make.
    assert note in flowed(capsys.readouterr().out)


# ---- listing ---------------------------------------------------------------------------------


def test_list_is_active_only_by_default(wired, capsys):
    mission_ops.list_missions(json_output=False)

    # The plain list asks for every mission, so it can count what the default view leaves out, and
    # then shows only the active ones.
    assert wired["listed_states"] == ["all"]
    out = flowed(capsys.readouterr().out)
    assert "count: 1 of 2 total (active 1)" in out
    assert "Release 2.0.0" in out
    assert "Remove the network port" not in out


def test_list_all_includes_ended_missions(wired, capsys):
    mission_ops.list_missions(json_output=False, state="all")

    assert wired["listed_states"] == ["all"]
    out = flowed(capsys.readouterr().out)
    assert "Release 2.0.0" in out
    assert "Remove the network port" in out


def test_a_mission_with_no_why_is_flagged_not_blank(wired, capsys):
    mission_ops.list_missions(json_output=False, state="all")

    # The same rule the Cockpit card follows: a mission whose reason nobody wrote down is the thing
    # worth noticing in this list, so it is flagged rather than shown as an empty cell.
    assert "no why set" in flowed(capsys.readouterr().out)


def test_an_empty_filtered_list_says_which_list_is_empty(monkeypatch, capsys):
    monkeypatch.setattr(mission_ops.MissionClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(mission_ops.MissionClient, "list_all", lambda self, state=None: [ACTIVE])

    mission_ops.list_missions(json_output=False, state="removed")

    # "No missions" under a filter would read as "you have none at all" - a different and much more
    # alarming statement than the truth.
    assert "state 'removed'" in flowed(capsys.readouterr().out)
