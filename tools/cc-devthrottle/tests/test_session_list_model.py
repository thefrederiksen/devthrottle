"""Tests for the model field of `cc-devthrottle session list --fields ...,model` (issue devthrottle_internal#1340).

The column exists because an agent driving this fleet had to parse `--json` to learn which model a session
was running, and a human reading the same table could not learn it at all - while it is the single fact
that drives both the cost and the quality of every session on the list.

Since #2922 the model is not a default field; `--fields` asks for it. What these pin is not "a model
appears". It is that the list RENDERS the Gateway's fold and never rules:
the full recorded id when there is one, and two DIFFERENT sentences for the two absences, which mean
opposite things ("the first turn has not finished" against "this agent can never report one"). Printed the
same, they would leave a reader waiting for a value that is never coming.
"""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import parse_list  # noqa: E402
from src import session_ops  # noqa: E402

SID = "7a1b2c3d-0000-0000-0000-000000000001"


@pytest.fixture
def serve_fleet(monkeypatch):
    """Serve a chosen fleet roster from the Director, without any real HTTP."""

    def serve(sessions):
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
        monkeypatch.setattr(session_ops.gateway, "get_json", lambda path: sessions)

    return serve


def _row(**extra):
    row = {
        "sessionId": SID,
        "name": "a session",
        "machineName": "SOREN",
        "repoPath": r"D:\ReposFred\devthrottle",
        "agent": "ClaudeCode",
        "activityState": "Working",
        "triageBucket": "active",
    }
    row.update(extra)
    return row


def _listing(capsys):
    """The model value of the one listed row, read back exactly."""
    session_ops.list_sessions(json_output=False, fields="id,state,model")
    _, records = parse_list(capsys.readouterr().out, "sessions")
    assert len(records) == 1
    return records[0]["model"]


def test_recorded_model_prints_the_full_id_not_the_shortened_badge(serve_fleet, capsys):
    # The rail shortens "claude-fable-5" to "fable-5" because a rail is narrow. A table is not, and a
    # truncated id is not a name anything else will match - so the column prints what the records spell.
    serve_fleet([_row(modelDisplay={"kind": "reported", "text": "fable-5", "modelId": "claude-fable-5"})])

    assert _listing(capsys) == "claude-fable-5"


def test_not_recorded_yet_says_so_in_the_gateways_words(serve_fleet, capsys):
    # No model id, but a verdict: this session CAN report one and simply has not finished a turn.
    serve_fleet([_row(modelDisplay={"kind": "notRecordedYet", "text": "no model yet", "modelId": None})])

    assert _listing(capsys) == "no model yet"


def test_the_two_absences_do_not_print_the_same_string(serve_fleet, capsys):
    # The whole issue in one test.
    serve_fleet([_row(modelDisplay={"kind": "notReported", "text": "model not reported", "modelId": None})])
    never = _listing(capsys)

    serve_fleet([_row(modelDisplay={"kind": "notRecordedYet", "text": "no model yet", "modelId": None})])
    not_yet = _listing(capsys)

    assert never == "model not reported"
    assert not_yet == "no model yet"
    assert never != not_yet


def test_unfolded_row_falls_back_to_the_raw_recorded_model(serve_fleet, capsys):
    # A Gateway too old to stamp the fold still puts the raw model on the wire. Printing it is not ruling -
    # it is the same fact, unfolded.
    serve_fleet([_row(currentModel="gpt-5.6-sol")])

    assert _listing(capsys) == "gpt-5.6-sol"


def test_no_model_and_no_fold_reads_as_unknown_not_as_either_absence(serve_fleet, capsys):
    # A third case, and it must not borrow the fold's words: an old Gateway told us nothing, which is not
    # the same as being told there is no model yet, nor that there never will be one. An empty cell would
    # have quietly claimed one of those.
    serve_fleet([_row()])

    assert _listing(capsys) == "(unknown)"


def test_a_crashed_state_is_read_back_beside_the_model(serve_fleet, capsys):
    # The old table paid for the model column by eliding "(crashed)" at 80 columns. The list has no width
    # budget, so both facts come back exactly, together.
    serve_fleet(
        [
            _row(
                activityState="Exited",
                crashed=True,
                modelDisplay={"kind": "reported", "text": "opus-5", "modelId": "claude-opus-5"},
            )
        ]
    )
    session_ops.list_sessions(json_output=False, fields="id,state,model")
    _, records = parse_list(capsys.readouterr().out, "sessions")
    assert records == [{"id": SID, "state": "crashed", "model": "claude-opus-5"}]
