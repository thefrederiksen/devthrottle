"""Tests for the terminal-input commands' visible answers.

A key, paste, or resize changes a remote terminal. The route decides whether it was written
or refused, so the command must print that answer rather than silently treating every completed
request as success. A session credential is refused on these owner routes; the nonzero exit lets
an automated caller distinguish that refusal from a written input.
"""

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import gateway  # noqa: E402
from src import session_ops  # noqa: E402
from src.cli import app  # noqa: E402


runner = CliRunner()
SESSION_ID = "11111111-2222-3333-4444-555555555555"
ANSWER = {"outcome": "written", "reason": None}


@pytest.mark.parametrize(
    ("arguments", "expected_path", "expected_body"),
    [
        (["session", "keys", SESSION_ID, "ctrl+c"], f"sessions/{SESSION_ID}/keys",
         {"key": "c", "shift": False, "alt": False, "control": True}),
        (["session", "paste", SESSION_ID, "the pasted words"], f"sessions/{SESSION_ID}/paste",
         {"text": "the pasted words"}),
        (["session", "resize", SESSION_ID, "120", "40"], f"sessions/{SESSION_ID}/resize",
         {"cols": 120, "rows": 40}),
    ],
)
def test_finding8_terminal_commands_print_the_routes_answer(
    monkeypatch, arguments, expected_path, expected_body
):
    def post(path, body):
        assert path == expected_path
        assert body == expected_body
        return ANSWER

    monkeypatch.setattr(session_ops, "resolve_target_or_current", lambda target, action: SESSION_ID)
    monkeypatch.setattr(session_ops.gateway, "post_json", post)

    result = runner.invoke(app, arguments)

    assert result.exit_code == 0
    assert json.loads(result.output) == ANSWER


@pytest.mark.parametrize(
    "arguments",
    [
        ["session", "keys", SESSION_ID, "down"],
        ["session", "paste", SESSION_ID, "the pasted words"],
        ["session", "resize", SESSION_ID, "120", "40"],
    ],
)
def test_finding8_terminal_commands_print_a_refusal_and_exit_nonzero(monkeypatch, arguments, plain):
    refusal = "the session credential cannot type into this terminal"

    def refused(path, body):
        raise gateway.GatewayError(refusal)

    monkeypatch.setattr(session_ops, "resolve_target_or_current", lambda target, action: SESSION_ID)
    monkeypatch.setattr(session_ops.gateway, "post_json", refused)

    result = runner.invoke(app, arguments)

    assert result.exit_code != 0
    assert refusal in plain(result.output)
