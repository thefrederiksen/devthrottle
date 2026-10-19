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

    def _serve(response, receipts=None, receipts_error=None):
        monkeypatch.setattr(session_ops, "resolve_target_or_current", lambda target, command: SESSION_ID)

        def post_json(path, body=None, timeout=30):
            calls.append({"path": path, "body": body})
            return response

        def get_json(path, timeout=30):
            calls.append({"path": path, "body": None})
            if receipts_error is not None:
                raise session_ops.gateway.GatewayError(receipts_error)
            # A terminal-mode session: the Gateway knows it and holds no receipts for it.
            return receipts if receipts is not None else {"sessionId": SESSION_ID, "count": 0, "receipts": []}

        monkeypatch.setattr(session_ops.gateway, "post_json", post_json)
        monkeypatch.setattr(session_ops.gateway, "get_json", get_json)
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
    assert [c["path"] for c in calls if c["body"] is not None] == [f"sessions/{SESSION_ID}/prompt"], \
        "a held prompt must never be sent a second time"


def test_session_prompt_queued_in_api_mode_says_the_receipts_own_words(serve):
    # Arrange: API mode, the user has the wheel - the Director holds the prompt and its verb answers "delivering",
    # while the Gateway's receipt for the same delivery id already says it is queued and why.
    calls = serve(
        {"delivering": True, "directorState": "delivering", "deliveryId": DELIVERY_ID},
        receipts={"sessionId": SESSION_ID, "count": 2, "receipts": [
            {"promptId": "another-prompt", "state": "Delivered", "words": "Delivered"},
            {"promptId": DELIVERY_ID, "state": "Queued", "queueReason": "userHasTheWheel",
             "words": "Queued, 1st in line: you have the wheel"},
        ]},
    )

    # Act
    result = runner.invoke(app, ["session", "prompt", SESSION_ID, "hello there"])

    # Assert: the Gateway's words, not "Held"; still exit zero and still sent once.
    assert result.exit_code == 0, result.output
    assert "Queued, 1st in line: you have the wheel" in result.output
    assert "Held" not in result.output
    assert "do not send it again" in result.output
    assert [c["path"] for c in calls] == [f"sessions/{SESSION_ID}/prompt", f"sessions/{SESSION_ID}/receipts"]


@pytest.mark.parametrize("state,words", [
    ("Delivered", "Delivered"),
    ("Refused", "Refused by Pi: Agent is already processing"),
])
def test_session_prompt_held_whose_receipt_is_no_longer_queued_prints_only_its_words(serve, state, words):
    # Arrange: by the time the held answer came, the Gateway's receipt already says how the prompt ended.
    serve(
        {"delivering": True, "directorState": "delivering", "deliveryId": DELIVERY_ID},
        receipts={"sessionId": SESSION_ID, "count": 1, "receipts": [{"promptId": DELIVERY_ID, "state": state, "words": words}]},
    )

    # Act
    result = runner.invoke(app, ["session", "prompt", SESSION_ID, "hello there"])

    # Assert: its words, and nothing that is only true of a queued prompt.
    assert result.exit_code == 0, result.output
    assert words in result.output
    assert "sends it on its own" not in result.output
    assert "Held" not in result.output


def test_session_prompt_held_with_no_receipt_for_it_keeps_the_held_sentence(serve):
    # Arrange: receipts exist, but none for this delivery id.
    serve(
        {"delivering": True, "directorState": "no-answer", "deliveryId": DELIVERY_ID},
        receipts={"sessionId": SESSION_ID, "count": 1, "receipts": [{"promptId": "another-prompt", "words": "Delivered"}]},
    )

    # Act
    result = runner.invoke(app, ["session", "prompt", SESSION_ID, "hello there"])

    # Assert
    assert result.exit_code == 0, result.output
    assert "Held" in result.output
    assert "Delivered" not in result.output


def test_session_prompt_held_when_the_receipts_cannot_be_read_says_so_and_still_exits_zero(serve):
    # Arrange
    serve({"delivering": True, "directorState": "no-answer", "deliveryId": DELIVERY_ID}, receipts_error="the Gateway answered 502")

    # Act
    result = runner.invoke(app, ["session", "prompt", SESSION_ID, "hello there"])

    # Assert: the prompt was accepted, so the command does not fail; the failed read is said, not hidden.
    assert result.exit_code == 0, result.output
    assert "Held" in result.output
    assert "Its receipt could not be read: the Gateway answered 502" in result.output


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
