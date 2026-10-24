import json
import sys
from pathlib import Path

import pytest
import typer
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402
from src import session_ops  # noqa: E402


SID = "11111111-2222-4333-8444-555555555555"
runner = CliRunner()


def _row(state, source="agent", fallback=False):
    return {
        "sessionId": SID,
        "activityState": state,
        "activityStateSource": source,
        "activityStateIsFallback": fallback,
    }


def test_wait_idle_already_waiting_reports_reached_state(monkeypatch):
    monkeypatch.setenv("CC_SESSION_ID", SID)
    monkeypatch.setattr(session_ops.gateway, "get_json", lambda path: _row("WaitingForInput", "screen"))

    result = runner.invoke(app, ["session", "wait", "--until", "idle"])

    assert result.exit_code == 0
    assert f"session: {SID}" in result.stdout
    assert "activity-state: WaitingForInput" in result.stdout
    assert "source: screen" in result.stdout
    assert "fallback: no" in result.stdout


def test_wait_needs_approval_json_keeps_machine_shape(monkeypatch):
    monkeypatch.setenv("CC_SESSION_ID", SID)
    monkeypatch.setattr(session_ops.gateway, "get_json", lambda path: _row("WaitingForPerm", "agent"))

    result = runner.invoke(app, ["session", "wait", "--until", "needs-approval", "--json"])

    assert result.exit_code == 0
    assert json.loads(result.stdout) == {
        "sessionId": SID,
        "until": "needs-approval",
        "activityState": "WaitingForPerm",
        "source": "agent",
        "fallback": False,
    }


def test_wait_unknown_until_is_usage_error_before_gateway_call(monkeypatch):
    monkeypatch.setenv("CC_SESSION_ID", SID)
    monkeypatch.setattr(session_ops.gateway, "get_json", lambda path: pytest.fail("Gateway must not be called"))

    result = runner.invoke(app, ["session", "wait", "--until", "finished"])

    assert result.exit_code == 2
    assert "Valid values: idle, needs-approval" in result.stderr


def test_wait_exited_before_outcome_is_runtime_error(monkeypatch):
    monkeypatch.setenv("CC_SESSION_ID", SID)
    monkeypatch.setattr(session_ops.gateway, "get_json", lambda path: _row("Exited", "core"))

    result = runner.invoke(app, ["session", "wait", "--until", "idle"])

    assert result.exit_code == 1
    assert "exited before it reached idle" in result.stderr


def test_wait_timeout_names_last_state(monkeypatch, capsys):
    monkeypatch.setenv("CC_SESSION_ID", SID)
    now = [0.0]

    with pytest.raises(typer.Exit) as stopped:
        session_ops.wait_for_session(
            None,
            "idle",
            1,
            read_state=lambda: _row("Working", "agent"),
            monotonic=lambda: now[0],
            sleep=lambda seconds: now.__setitem__(0, now[0] + seconds),
        )

    captured = capsys.readouterr()
    assert stopped.value.exit_code == 1
    assert "did not reach idle within 1 seconds" in captured.err
    assert "last activity state was Working" in captured.err
