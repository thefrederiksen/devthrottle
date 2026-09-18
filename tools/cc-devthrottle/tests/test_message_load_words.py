"""Message Load mission, slice 5 (17 September 2026): the command line's own words teach the queue.

A message is queued, never typed; nobody waits for an answer; an agent may not type into a session;
and a session may not name another session as the owner of one it spawns. These tests pin the help
and docstrings that said otherwise before this slice.
"""

import sys
from pathlib import Path

import typer.main
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402

runner = CliRunner()


def _help_text(*command: str) -> str:
    """The full docstring and parameter help of one command, read from the command tree, so terminal
    width and wrapping cannot hide a phrase."""
    group = typer.main.get_command(app)
    cmd = group
    for name in command:
        cmd = cmd.commands[name]
    parts = [cmd.help or ""]
    parts += [getattr(p, "help", None) or "" for p in cmd.params]
    return " ".join(" ".join(parts).split())


def test_compact_continue_says_it_is_the_owners_tool():
    text = _help_text("session", "compact-continue")
    assert "refuses this verb to every session key" in text
    assert "queues a message instead" in text
    assert "supervising agent can rescue" not in text


def test_hold_says_another_agents_message_does_not_end_it():
    text = _help_text("session", "hold")
    assert "Another agent's message does NOT end it" in text
    assert "doorbell" in text
    assert "work the Director cannot attribute to anyone" in text
    assert "repainting, no longer un-holds it" not in text


def test_report_says_it_is_queued_and_rung():
    text = _help_text("session", "report")
    assert "QUEUED in their inbox, never typed into them" in text
    assert "doorbell line tells them to run 'cc-devthrottle message inbox'" in text


def test_selftest_does_not_offer_an_ask_step():
    text = _help_text("selftest")
    assert "ask step" not in text
    assert "nothing waits any more" in text
    assert "Windows only" in text


def test_spawn_never_offers_another_session_as_owner(monkeypatch, capsys):
    # --controlled-by self outside a session: the usage error must not suggest naming another session.
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    result = runner.invoke(app, ["session", "spawn", str(Path.cwd()), "--controlled-by", "self", "--name", "x"])
    out = result.output + (result.stderr if result.stderr_bytes is not None else "")
    assert result.exit_code == 2
    assert "--controlled-by <session-id>" not in out
    assert "--standalone" in out
    assert "--controlled-by <session-id>" not in _help_text("session", "spawn")


def test_message_group_has_no_blocking_ask():
    group = typer.main.get_command(app).commands["message"]
    assert "ask" not in group.commands
    assert {"send", "reply", "inbox"} <= set(group.commands)
