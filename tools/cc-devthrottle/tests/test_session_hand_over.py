"""Tests for `cc-devthrottle session hand-over <session> --to fleet-manager|owner` (the Fleet Manager mission,
step 8).

What the command sends and prints is pinned here: the route and the body are literals, never read from the module,
so a changed route turns a test red. No HTTP happens: the Gateway calls are stubbed. Output is compared with ANSI
styling stripped (conftest `plain`).
"""

import json
import sys
from pathlib import Path

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import fleet_manager_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

PLAIN_ID = "b3000000-0000-4000-8000-000000000003"
FM_ID = "f1000000-0000-4000-8000-000000000001"
FLEET = [
    {"sessionId": FM_ID, "name": "Fleet - Fleet Manager - the owner's work", "number": 101},
    {"sessionId": PLAIN_ID, "name": "Widgets - invoice export", "number": 102},
]


def _stub(monkeypatch, answer=None, error=None, status=409):
    """A Gateway that records every POST and answers with `answer`, or refuses with `error`."""
    state = {"posts": []}
    gw = fleet_manager_ops.gateway

    def fake_post_json(path, body=None, timeout=30):
        state["posts"].append((path, body))
        if error is not None:
            raise gw.GatewayError(error, status=status)
        return answer

    monkeypatch.setattr(gw, "post_json", fake_post_json)
    monkeypatch.setattr(gw, "get_fleet", lambda: (FLEET, True, None, None))
    return state


def _answer(to="fleet-manager", owner=FM_ID, sentence='Session "Widgets - invoice export" is now the Fleet Manager\'s.'):
    return {
        "sessionId": PLAIN_ID,
        "to": to,
        "ownerSessionId": owner,
        "previousOwnerSessionId": None,
        "sentence": sentence,
        "session": {"sessionId": PLAIN_ID, "controllerSessionId": owner},
    }


def test_hand_over_by_number_sends_the_full_id_and_the_direction(monkeypatch, plain):
    state = _stub(monkeypatch, answer=_answer())

    result = runner.invoke(app, ["session", "hand-over", "102", "--to", "fleet-manager"])

    assert result.exit_code == 0, result.output
    assert state["posts"] == [("gateway/fleet-manager/hand-over", {"session": PLAIN_ID, "to": "fleet-manager"})]
    out = plain(result.output)
    assert 'Session "Widgets - invoice export" is now the Fleet Manager\'s.' in out
    assert f"session: {PLAIN_ID}" in out
    assert f"owner: {FM_ID}" in out
    assert "help[2]:" in out
    assert f"cc-devthrottle session hand-over {PLAIN_ID} --to owner" in out


def test_hand_back_prints_that_the_owner_has_it(monkeypatch, plain):
    state = _stub(monkeypatch, answer=_answer(to="owner", owner=None, sentence="It is yours again."))

    result = runner.invoke(app, ["session", "hand-over", PLAIN_ID[:8], "--to", "OWNER"])

    assert result.exit_code == 0, result.output
    assert state["posts"][0][1] == {"session": PLAIN_ID, "to": "owner"}
    out = plain(result.output)
    assert "It is yours again." in out
    assert "owner: you" in out
    assert f"--to fleet-manager" in out


def test_json_prints_the_gateway_answer_unchanged(monkeypatch, plain):
    answer = _answer()
    _stub(monkeypatch, answer=answer)

    result = runner.invoke(app, ["session", "hand-over", PLAIN_ID, "--to", "fleet-manager", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(plain(result.output)) == answer


def test_a_refusal_prints_the_gateways_sentence_and_exits_1(monkeypatch, plain):
    sentence = (f"Session {FM_ID} may not hand session {PLAIN_ID} over: it does not own that session, and it is "
                "not this account's Fleet Manager session. A session may hand over a session it OWNS, and only "
                "to the owner (--to owner).")
    _stub(monkeypatch, error=sentence, status=403)

    result = runner.invoke(app, ["session", "hand-over", PLAIN_ID, "--to", "fleet-manager"])

    assert result.exit_code == 1
    out = plain(result.output)
    assert f"Error: session {PLAIN_ID} was not handed over: {sentence}" in out
    assert "help[" in out
    assert "cc-devthrottle fleet-manager show" in out


def test_a_refusal_with_json_still_fails_with_the_sentence(monkeypatch, plain):
    _stub(monkeypatch, error="Session is already yours: no session owns it.")

    result = runner.invoke(app, ["session", "hand-over", PLAIN_ID, "--to", "owner", "--json"])

    assert result.exit_code == 1
    assert "already yours" in plain(result.output)


def test_an_unknown_direction_is_a_usage_error_and_sends_nothing(monkeypatch, plain):
    state = _stub(monkeypatch, answer=_answer())

    result = runner.invoke(app, ["session", "hand-over", PLAIN_ID, "--to", "the-architect"])

    assert result.exit_code == 2
    out = plain(result.output)
    assert "--to must be one of: fleet-manager, owner, me (got 'the-architect')." in out
    assert state["posts"] == []


def test_a_missing_direction_is_a_usage_error_and_sends_nothing(monkeypatch, plain):
    state = _stub(monkeypatch, answer=_answer())

    result = runner.invoke(app, ["session", "hand-over", PLAIN_ID])

    assert result.exit_code == 2
    assert "--to must be one of: fleet-manager, owner, me - it was not given." in plain(result.output)
    assert state["posts"] == []


def test_an_unknown_flag_fails(monkeypatch, plain):
    state = _stub(monkeypatch, answer=_answer())

    result = runner.invoke(app, ["session", "hand-over", PLAIN_ID, "--to", "owner", "--force"])

    assert result.exit_code == 2
    assert state["posts"] == []


def test_a_session_that_is_not_in_the_fleet_is_an_error_and_sends_nothing(monkeypatch, plain):
    state = _stub(monkeypatch, answer=_answer())

    result = runner.invoke(app, ["session", "hand-over", "no-such-session", "--to", "owner"])

    assert result.exit_code == 1
    assert "No session matches 'no-such-session'" in plain(result.output)
    assert state["posts"] == []


def test_releasing_a_session_you_own_sends_to_owner_and_reports_the_user_has_it(monkeypatch, plain):
    """Issue #3086: a session lets go of a session it owns. The command sends the same one body; the Gateway
    decides who may ask. What is pinned here is that `--to owner` reaches the route unchanged and the answer,
    with no owner, is reported as the user's."""
    state = _stub(monkeypatch, answer=_answer(
        to="owner", owner=None,
        sentence='Session "Widgets - invoice export" is yours again. It asks you directly from now on.'))

    result = runner.invoke(app, ["session", "hand-over", PLAIN_ID, "--to", "owner"])

    assert result.exit_code == 0, result.output
    assert state["posts"] == [("gateway/fleet-manager/hand-over", {"session": PLAIN_ID, "to": "owner"})]
    out = plain(result.output)
    assert "is yours again. It asks you directly from now on." in out
    assert "owner: you" in out


def test_taking_a_session_sends_to_me_and_reports_the_new_owner(monkeypatch, plain):
    """Issue #3096: a session takes a session that answers to the owner, on his direction. The command sends the
    one body; the Gateway decides who may ask. Pinned here: `--to me` reaches the route unchanged, and the answer's
    owner - which is the calling session, not the target - is what gets reported."""
    taker = "a5000000-0000-4000-8000-000000000009"
    state = _stub(monkeypatch, answer=_answer(
        to="me", owner=taker,
        sentence='Session "Widgets - invoice export" is yours now. When it stops, you are told instead of the owner.'))

    result = runner.invoke(app, ["session", "hand-over", PLAIN_ID, "--to", "me"])

    assert result.exit_code == 0, result.output
    assert state["posts"] == [("gateway/fleet-manager/hand-over", {"session": PLAIN_ID, "to": "me"})]
    out = plain(result.output)
    assert "is yours now." in out
    assert f"owner: {taker}" in out
    # the next step offered is the way back to the owner, never a third session
    assert f"cc-devthrottle session hand-over {PLAIN_ID} --to owner" in out


def test_taking_answered_without_an_owner_exits_one_without_claiming_it(monkeypatch, plain):
    """A take whose answer names no owner did not happen, and the command must not say it did."""
    _stub(monkeypatch, answer=_answer(to="me", owner=None, sentence="It is yours now."))

    result = runner.invoke(app, ["session", "hand-over", PLAIN_ID, "--to", "me"])

    assert result.exit_code == 1
    assert "ownerSessionId" in plain(result.output)


def test_the_help_says_a_session_may_take_as_well_as_release(plain):
    """An agent that reads the help learns both directions and the rule that binds them (issue #3096)."""
    result = runner.invoke(app, ["session", "hand-over", "--help"])

    assert result.exit_code == 0
    out = " ".join(plain(result.output).split())
    assert "--to me" in out
    assert "THE ONLY OWNER A SESSION MAY NAME IS ITSELF" in out
    assert "under a third session" in out


def test_the_help_says_a_session_may_release_what_it_owns(plain):
    """An agent that reads the help learns the rule without hitting the refusal (issue #3086)."""
    result = runner.invoke(app, ["session", "hand-over", "--help"])

    assert result.exit_code == 0
    out = " ".join(plain(result.output).split())
    assert "RELEASE WHAT IT OWNS" in out.upper()
    assert "--to owner" in out
    assert "owner's to direct" in out


def test_the_action_description_says_which_direction_a_session_may_hand_over(plain):
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    actions = json.loads(plain(result.output))
    rows = actions if isinstance(actions, list) else actions.get("actions", [])
    row = next(a for a in rows if a["id"] == "session-hand-over")
    assert "release a session YOU own" in row["description"]
    assert "--to owner" in row["description"]
    assert "--to me" in row["description"]
    assert "never put under a third session" in row["description"]


def test_the_action_is_listed_for_agents(plain):
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    actions = json.loads(plain(result.output))
    rows = actions if isinstance(actions, list) else actions.get("actions", [])
    row = next(a for a in rows if a["id"] == "session-hand-over")
    assert row["mutatesState"] is True
    assert row["command"] == "cc-devthrottle session hand-over <session> --to fleet-manager|owner|me [--json]"
