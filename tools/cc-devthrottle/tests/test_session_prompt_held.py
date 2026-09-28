"""Tests for how the command line's `session prompt` verb reads the Gateway's answer (Voice Delivery mission, phase 5).

A typed prompt the Director has not answered as delivered is HELD by the Gateway: the route answers 202
`{ delivering: true, directorState, deliveryId }` and the Gateway itself asks what became of it. That is a
success-class answer - the words may already be in - so the command must exit zero, say it is held, and SEND
NOTHING AGAIN. Before this, the held body had no `accepted` key, the command reported the prompt as unconfirmed and
exited 1, and an agent reading that would send the same text a second time.

The exit code is asserted through the real command line, because the exit code IS the thing under test.
"""

import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

SESSION_ID = "9c41e7a2-1111-2222-3333-444455556666"
DELIVERY_ID = "0123456789abcdef0123456789abcdef"


@pytest.fixture
def serve(monkeypatch):
    calls = []

    def _serve(response):
        monkeypatch.setattr(session_ops, "resolve_target_or_current", lambda target, command: SESSION_ID)

        def post_json(path, body=None, timeout=30):
            calls.append({"path": path, "body": body})
            return response

        monkeypatch.setattr(session_ops.gateway, "post_json", post_json)
        return calls

    return _serve


@pytest.fixture(autouse=True)
def wide_console(monkeypatch):
    from rich.console import Console

    monkeypatch.setattr(session_ops, "console", Console(width=400))


def test_session_prompt_held_answer_exits_zero_and_sends_once(serve):
    calls = serve({"delivering": True, "directorState": "no-answer", "deliveryId": DELIVERY_ID})

    result = runner.invoke(app, ["session", "prompt", SESSION_ID, "hello there"])

    assert result.exit_code == 0, result.output
    assert "Held" in result.output
    assert DELIVERY_ID in result.output
    assert "Do not send it again" in result.output
    assert len(calls) == 1, "a held prompt must never be sent a second time"
    assert calls[0]["path"] == f"sessions/{SESSION_ID}/prompt"


def test_session_prompt_delivered_answer_still_reports_sent(serve):
    calls = serve({"accepted": True, "deliveryId": DELIVERY_ID})

    result = runner.invoke(app, ["session", "prompt", SESSION_ID, "hello there"])

    assert result.exit_code == 0, result.output
    assert "Sent" in result.output
    assert len(calls) == 1


def test_session_prompt_refused_answer_still_fails(serve):
    serve({"accepted": False, "error": "a menu owns the screen", "deliveryId": DELIVERY_ID})

    result = runner.invoke(app, ["session", "prompt", SESSION_ID, "hello there"])

    assert result.exit_code == 1, result.output
    assert "a menu owns the screen" in result.output
