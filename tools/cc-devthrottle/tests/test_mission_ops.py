"""Tests for `cc-devthrottle mission attach` and `mission detach` (issue #2387).

Attaching a session that ALREADY EXISTS is the half that was missing: a Mission could only be
joined in the instant a session was spawned, so a body of work that GREW - which is most of them -
could never be shown as one.

These cases assert the DECISIONS, because each was settled deliberately and each is the kind of
thing that gets quietly reversed by a later edit if nothing holds it:

  * attaching is a MOVE, and the move is reported (you are told what the session left);
  * detaching is real, and a session that had no mission is told so rather than being told it was
    detached from nothing;
  * --with-children walks the controlling relationship ALL THE WAY DOWN, and does nothing at all
    without the flag.

No HTTP happens: the Director post and the Gateway mission list are both stubbed.
"""

import re
import sys
from pathlib import Path

import pytest
import typer

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import mission_ops, session_ops  # noqa: E402


def flowed(text: str) -> str:
    """Collapse Rich's console wrapping so an assertion is about WORDS, not column width.

    Rich wraps to the console width, which can split a mission name across two lines. The reader
    still sees the name; an assertion that fails because of where the wrap landed is testing the
    terminal, not the message.
    """
    return " ".join(text.split())


MISSION_ID = "aaaaaaaa-1111-2222-3333-444444444444"
OTHER_MISSION_ID = "bbbbbbbb-9999-8888-7777-666666666666"

MISSIONS = [
    {"missionId": MISSION_ID, "missionName": "Release 1.9.4"},
    {"missionId": OTHER_MISSION_ID, "missionName": "Voice cleanup"},
]

# An Architect controlling a Manager controlling two Workers, plus an unrelated session that no one
# controls. The three-deep shape is the point: stopping at the first level would attach the Manager
# and leave the Workers behind, which reads as success while producing the split view the whole
# feature exists to end.
ARCHITECT = {"sessionId": "sess-architect", "name": "Release - Architect"}
MANAGER = {"sessionId": "sess-manager", "name": "Release - Manager",
           "controllerSessionId": "sess-architect"}
WORKER_ONE = {"sessionId": "sess-worker-1", "name": "Release - Worker one",
              "controllerSessionId": "sess-manager"}
WORKER_TWO = {"sessionId": "sess-worker-2", "name": "Release - Worker two",
              "controllerSessionId": "sess-manager"}
UNRELATED = {"sessionId": "sess-elsewhere", "name": "Something else entirely"}

ROSTER = [ARCHITECT, MANAGER, WORKER_ONE, WORKER_TWO, UNRELATED]


def _gateway_answer(flat):
    """The Gateway's MissionAttachResultDto for a flat description: the session row (its id and the
    mission it now carries) under `session`, and everything else beside it."""
    answer = {k: v for k, v in flat.items() if k not in ("sessionId", "missionId", "missionName", "applied")}
    answer["session"] = {
        "sessionId": flat["sessionId"],
        "missionId": flat.get("missionId"),
        "missionName": flat.get("missionName"),
    }
    return answer


@pytest.fixture
def wired(monkeypatch):
    """Stub the Gateway mission list, the fleet roster, and the Director post; record every call."""
    calls = []

    def fake_post_json(path, body, timeout=30):
        # Remove-the-network-port mission, phase 2: the TARGET is in the path now, not in the body.
        # Recorded as toSessionId so the assertions below still read as "which sessions were moved".
        m = re.fullmatch(r"sessions/([^/]+)/mission", path)
        assert m, f"unexpected path {path}"
        calls.append({"toSessionId": m.group(1), **body})
        return _gateway_answer({
            "applied": True,
            "sessionId": m.group(1),
            "missionId": body.get("missionId"),
            "missionName": "Release 1.9.4" if body.get("missionId") else None,
            "previousMissionId": OTHER_MISSION_ID,
            "previousMissionName": "Voice cleanup",
        })

    # list_all now takes a state filter (missions can be completed or removed); the resolver asks
    # for "all" so an ended mission can still be renamed or reopened.
    monkeypatch.setattr(mission_ops.MissionClient, "list_all",
                        lambda self, state=None: list(MISSIONS))
    monkeypatch.setattr(mission_ops.MissionClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: (list(ROSTER), True, None, None))
    monkeypatch.setattr(session_ops.gateway, "post_json", fake_post_json)
    # mission_ops imports the gateway helper lazily, and it is the SAME module object, so patching
    # it once here covers both call sites.
    return calls


def test_attach_sends_the_full_mission_id_for_the_named_session(wired):
    mission_ops.attach_session("sess-manager", MISSION_ID, with_children=False)

    assert wired == [{"toSessionId": "sess-manager", "missionId": MISSION_ID}]


def test_a_mission_can_be_named_by_prefix_or_by_name(wired):
    # Only the TYPING is relaxed. Whatever was typed, the id that goes to the Director is the full
    # one out of the caller's own mission list.
    mission_ops.attach_session("sess-manager", "Release", with_children=False)
    mission_ops.attach_session("sess-manager", MISSION_ID[:8], with_children=False)

    assert [c["missionId"] for c in wired] == [MISSION_ID, MISSION_ID]


def test_an_ambiguous_mission_name_is_refused_rather_than_guessed(wired):
    # Two missions match "e" - refusing is the only honest answer; picking one would attach the
    # session to a body of work nobody chose.
    with pytest.raises(typer.Exit):
        mission_ops.attach_session("sess-manager", "e", with_children=False)
    assert wired == []


def test_the_move_reports_the_mission_the_session_left(wired, capsys, plain):
    # Attaching is a MOVE. A move that reports only its destination hides that something was
    # displaced, which is exactly how a session goes missing from the pod somebody is looking at.
    mission_ops.attach_session("sess-manager", MISSION_ID, with_children=False)
    out = flowed(plain(capsys.readouterr().out))

    assert "Release 1.9.4" in out
    assert "Voice cleanup" in out   # the mission it left, named


def test_without_the_flag_only_the_named_session_moves(wired):
    # The default. A controlling session routinely commissions work that is NOT part of its own
    # mission, so bringing its children has to be asked for.
    mission_ops.attach_session("sess-architect", MISSION_ID, with_children=False)

    assert [c["toSessionId"] for c in wired] == ["sess-architect"]


def test_with_children_walks_the_whole_controlled_subtree(wired):
    # Transitive, not one level: the Architect brings the Manager AND both of the Manager's Workers.
    mission_ops.attach_session("sess-architect", MISSION_ID, with_children=True)

    moved = [c["toSessionId"] for c in wired]
    assert moved[0] == "sess-architect"                 # the named session first
    assert set(moved) == {"sess-architect", "sess-manager", "sess-worker-1", "sess-worker-2"}
    assert "sess-elsewhere" not in moved                # and nothing it does not control


def test_with_children_names_every_session_it_moves(wired, capsys, plain):
    # Names, not a count. "4 sessions attached" is unreviewable; a list can be read and disputed
    # before the next command is typed.
    mission_ops.attach_session("sess-architect", MISSION_ID, with_children=True)
    out = flowed(plain(capsys.readouterr().out))

    for name in ("Release - Architect", "Release - Manager", "Release - Worker one",
                 "Release - Worker two"):
        assert name in out
    assert "Something else entirely" not in out


def test_detach_sends_no_mission_id(wired):
    mission_ops.detach_session("sess-manager")

    assert wired == [{"toSessionId": "sess-manager"}]


def test_detach_reports_the_mission_the_session_left(wired, capsys, plain):
    mission_ops.detach_session("sess-manager")
    out = flowed(plain(capsys.readouterr().out))

    assert "Detached" in out
    assert "Voice cleanup" in out


def test_detaching_a_session_that_had_no_mission_says_nothing_changed(monkeypatch, capsys, plain):
    # Claiming a detach that did not happen is the small lie that makes the next person distrust
    # the whole command. Say what is true.
    monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: (list(ROSTER), True, None, None))
    monkeypatch.setattr(
        session_ops.gateway, "post_json",
        lambda path, body, timeout=30: _gateway_answer({"applied": True, "sessionId": path.split("/")[1]}),
    )

    mission_ops.detach_session("sess-manager")
    out = flowed(plain(capsys.readouterr().out))

    assert "nothing changed" in out
    assert "Detached" not in out


def test_the_subtree_walk_survives_a_cycle_in_the_roster():
    # A cycle cannot happen through legitimate spawns, but a corrupted roster must not hang the
    # command line. Asserted directly on the walk, because a hang has no failure message.
    a = {"sessionId": "a", "controllerSessionId": "b"}
    b = {"sessionId": "b", "controllerSessionId": "a"}

    found = mission_ops._controlled_subtree([a, b], "a")

    assert [s["sessionId"] for s in found] == ["b"]


def test_a_remote_attach_still_reports_the_mission_it_left(monkeypatch, capsys, plain):
    # A remote target is relayed through the Gateway to a Director this machine never talked to
    # about that session, so nothing on the return path knows what the session left. The roster row
    # the caller was just resolved against does, so the move is still visible rather than silently
    # reported as a plain attach.
    # list_all now takes a state filter (missions can be completed or removed); the resolver asks
    # for "all" so an ended mission can still be renamed or reopened.
    monkeypatch.setattr(mission_ops.MissionClient, "list_all",
                        lambda self, state=None: list(MISSIONS))
    monkeypatch.setattr(mission_ops.MissionClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(
        session_ops.gateway, "get_fleet",
        lambda: ([dict(MANAGER, missionId=OTHER_MISSION_ID, missionName="Voice cleanup")],
                 True, None, None),
    )
    monkeypatch.setattr(
        session_ops.gateway, "post_json",
        # The relay response: applied, but carrying no previous attachment.
        lambda path, body, timeout=30: _gateway_answer({"applied": True, "sessionId": path.split("/")[1],
                                                         "missionId": body.get("missionId")}),
    )

    mission_ops.attach_session("sess-manager", MISSION_ID, with_children=False)
    out = flowed(plain(capsys.readouterr().out))

    assert "moved from Voice cleanup" in out


# --- The workflow seat travels with the mission (issue #2387, review finding) ---
#
# A Mission is also a run of the mission workflow, and the seat pins the conduct the agent follows.
# The command line has to SAY what happened to the seat: a move that silently re-governed a session
# would be the same invisibility the feature exists to end, and a move that says nothing about the
# already-injected conduct leaves a human believing it is complete when the agent still holds the
# old rules.


def _wire(monkeypatch, response):
    # list_all now takes a state filter (missions can be completed or removed); the resolver asks
    # for "all" so an ended mission can still be renamed or reopened.
    monkeypatch.setattr(mission_ops.MissionClient, "list_all",
                        lambda self, state=None: list(MISSIONS))
    monkeypatch.setattr(mission_ops.MissionClient, "__init__", lambda self, base_url=None: None)
    monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: (list(ROSTER), True, None, None))
    monkeypatch.setattr(
        session_ops.gateway, "post_json", lambda path, body, timeout=30: _gateway_answer(response)
    )


def test_a_seat_move_warns_that_a_running_agent_still_holds_its_old_conduct(
    monkeypatch, capsys, plain
):
    # The honest limit. Moving the seat fixes the RECORD; it cannot rewrite what is already in a
    # running agent's context. Saying nothing here would read as "the move is complete".
    _wire(monkeypatch, {"applied": True, "sessionId": "sess-manager",
                        "missionId": MISSION_ID, "seatMoved": True,
                        "workflowId": "mission", "workflowVersion": 9})

    mission_ops.attach_session("sess-manager", MISSION_ID, with_children=False)
    out = flowed(plain(capsys.readouterr().out))

    assert "still holds the conduct it was given at birth" in out
    # The exact command, with the workflow and version FILLED IN. A instruction with blanks makes the
    # human go and find two values somewhere else at the one moment they were told something is out of
    # step, which is how a warning gets skipped.
    assert "cc-devthrottle workflow instructions mission --version 9" in out


def test_no_warning_when_the_seat_did_not_move(monkeypatch, capsys, plain):
    # The control. The warning must be tied to the seat actually moving, or it becomes noise that
    # gets ignored on the one occasion it matters.
    _wire(monkeypatch, {"applied": True, "sessionId": "sess-manager",
                        "missionId": MISSION_ID, "seatMoved": False})

    mission_ops.attach_session("sess-manager", MISSION_ID, with_children=False)
    out = flowed(plain(capsys.readouterr().out))

    assert "still holds the conduct" not in out


def test_detach_says_the_seat_was_cleared_with_the_mission(monkeypatch, capsys, plain):
    # Detach clears the mission's seat too, which leaves the session governed by no workflow conduct
    # at all. That is a fact about how the session will now behave, so it is reported.
    _wire(monkeypatch, {"applied": True, "sessionId": "sess-manager", "seatMoved": True,
                        "previousMissionId": OTHER_MISSION_ID,
                        "previousMissionName": "Voice cleanup"})

    mission_ops.detach_session("sess-manager")
    out = flowed(plain(capsys.readouterr().out))

    assert "Detached" in out
    assert "no longer governed by that mission's workflow run" in out


def test_the_directors_seat_note_is_passed_through_verbatim(monkeypatch, capsys, plain):
    # What happened to the seat is decided at the Gateway. A client that re-worded it would be
    # writing its own account of a decision it did not make - which is how a surface starts saying
    # something plausible instead of something true.
    note = "No Gateway is configured, so the workflow seat was not moved."
    _wire(monkeypatch, {"applied": True, "sessionId": "sess-manager",
                        "missionId": MISSION_ID, "seatMoved": False, "seatNote": note})

    mission_ops.attach_session("sess-manager", MISSION_ID, with_children=False)
    out = flowed(plain(capsys.readouterr().out))

    assert note in out
