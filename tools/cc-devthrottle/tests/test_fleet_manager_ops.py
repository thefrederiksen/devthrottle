"""Tests for `cc-devthrottle fleet-manager show | set | clear`.

The account has exactly one Fleet Manager and the Gateway holds which session it is. These tests pin
what the command sends and prints: the route and body are written out as literals, never read from the
module, so a changed route turns a test red. No HTTP happens: the Gateway calls are stubbed.
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

FM_ID = "f1000000-0000-4000-8000-000000000001"
OTHER_ID = "a2000000-0000-4000-8000-000000000002"
FLEET = [
    {"sessionId": FM_ID, "name": "Fleet - Fleet Manager - the owner's work"},
    {"sessionId": OTHER_ID, "name": "Release - Architect"},
]


def _stub(monkeypatch, stored=None):
    """A Gateway holding one mark (or none), recording every PUT."""
    state = {"sessionId": stored, "puts": []}
    gw = fleet_manager_ops.gateway

    def fake_get_json(path, timeout=30):
        assert path == "gateway/fleet-manager", path
        return {"sessionId": state["sessionId"]}

    def fake_put_json(path, body, timeout=30):
        assert path == "gateway/fleet-manager", path
        state["puts"].append(body)
        state["sessionId"] = body["sessionId"]
        return {"sessionId": body["sessionId"]}

    monkeypatch.setattr(gw, "get_json", fake_get_json)
    monkeypatch.setattr(gw, "put_json", fake_put_json)
    monkeypatch.setattr(gw, "get_fleet", lambda: (FLEET, True, None, None))
    return state


def test_show_with_no_mark_says_none_and_how_to_set_one(monkeypatch, plain):
    _stub(monkeypatch)

    result = runner.invoke(app, ["fleet-manager", "show"])

    assert result.exit_code == 0
    assert "fleet-manager: none" in plain(result.output)
    assert "cc-devthrottle fleet-manager set <session>" in plain(result.output)


def test_show_prints_the_full_id_and_the_name(monkeypatch, plain):
    _stub(monkeypatch, stored=FM_ID)

    result = runner.invoke(app, ["fleet-manager", "show"])

    assert result.exit_code == 0
    assert f"id: {FM_ID}" in plain(result.output)
    assert "name: Fleet - Fleet Manager - the owner's work" in plain(result.output)


def test_show_json_keeps_the_gateway_shape(monkeypatch, plain):
    _stub(monkeypatch, stored=FM_ID)

    result = runner.invoke(app, ["fleet-manager", "show", "--json"])

    assert result.exit_code == 0
    assert json.loads(plain(result.output)) == {"sessionId": FM_ID}


def test_set_by_name_sends_the_resolved_full_id(monkeypatch, plain):
    state = _stub(monkeypatch)

    result = runner.invoke(app, ["fleet-manager", "set", "Release - Architect", "--json"])

    assert result.exit_code == 0, plain(result.output)
    assert state["puts"] == [{"sessionId": OTHER_ID}]
    assert json.loads(plain(result.output)) == {"sessionId": OTHER_ID}


def test_set_with_no_target_marks_this_session(monkeypatch, plain):
    state = _stub(monkeypatch)
    monkeypatch.setenv("CC_SESSION_ID", FM_ID)

    result = runner.invoke(app, ["fleet-manager", "set"])

    assert result.exit_code == 0, plain(result.output)
    assert state["puts"] == [{"sessionId": FM_ID}]
    assert f"id: {FM_ID}" in plain(result.output)


def test_set_with_no_target_outside_a_session_fails_and_sends_nothing(monkeypatch, plain):
    state = _stub(monkeypatch)
    monkeypatch.delenv("CC_SESSION_ID", raising=False)

    result = runner.invoke(app, ["fleet-manager", "set"])

    assert result.exit_code == 1
    assert state["puts"] == []
    assert "fleet-manager set <session>" in " ".join(plain(result.output).split())


def test_set_unknown_session_fails_and_sends_nothing(monkeypatch):
    state = _stub(monkeypatch)

    result = runner.invoke(app, ["fleet-manager", "set", "no-such-session"])

    assert result.exit_code == 1
    assert state["puts"] == []


def test_clear_sends_a_null_id(monkeypatch, plain):
    state = _stub(monkeypatch, stored=FM_ID)

    result = runner.invoke(app, ["fleet-manager", "clear"])

    assert result.exit_code == 0
    assert state["puts"] == [{"sessionId": None}]
    assert "fleet-manager: none (cleared)" in plain(result.output)


def test_gateway_error_is_a_sentence_and_exit_one(monkeypatch, plain):
    gw = fleet_manager_ops.gateway

    def refuse(path, timeout=30):
        raise gw.GatewayError("Cannot reach the Gateway at http://example.invalid")

    monkeypatch.setattr(gw, "get_json", refuse)

    result = runner.invoke(app, ["fleet-manager", "show"])

    assert result.exit_code == 1
    assert "Cannot reach the Gateway" in plain(result.output)


def test_actions_list_the_three_fleet_manager_commands(plain):
    result = runner.invoke(app, ["actions", "--json"])

    ids = {a["id"] for a in json.loads(plain(result.output))["actions"]}
    assert {"fleet-manager-show", "fleet-manager-set", "fleet-manager-clear"}.issubset(ids)
