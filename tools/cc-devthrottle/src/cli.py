"""CLI for cc-devthrottle - unified DevThrottle command surface."""

from __future__ import annotations

import json
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

from . import __version__
from .session_ops import (
    ask_session,
    list_sessions,
    rename_session,
    selftest as run_selftest,
    send_message,
    spawn_session,
    whoami as show_whoami,
)

app = typer.Typer(
    name="cc-devthrottle",
    help="Unified DevThrottle command-line surface.",
    add_completion=False,
    no_args_is_help=True,
)
session_app = typer.Typer(help="Manage running sessions.", add_completion=False)
message_app = typer.Typer(help="Send messages between sessions.", add_completion=False)
app.add_typer(session_app, name="session")
app.add_typer(message_app, name="message")
console = Console()

_ACTIONS = [
    {
        "id": "session-list",
        "description": "List every session in the fleet.",
        "command": "cc-devthrottle session list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "session-whoami",
        "description": "Show this session's id, name, machine, and repository.",
        "command": "cc-devthrottle session whoami",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "session-rename-current",
        "description": "Rename the current session using CC_SESSION_ID.",
        "command": 'cc-devthrottle session rename "<new name>"',
        "mutatesState": True,
        "args": [{"name": "new_name", "required": True}],
    },
    {
        "id": "session-rename-target",
        "description": "Rename a session selected by full id, id prefix, or exact name.",
        "command": 'cc-devthrottle session rename <target> "<new name>"',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "new_name", "required": True},
        ],
    },
    {
        "id": "session-spawn",
        "description": "Open a new session on the local Director.",
        "command": "cc-devthrottle session spawn <repo>",
        "mutatesState": True,
        "args": [{"name": "repo", "required": True}],
    },
    {
        "id": "message-send",
        "description": "Send a one-way message to a session, or broadcast to all sessions.",
        "command": 'cc-devthrottle message send <target|all> "<message>"',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "message", "required": True},
        ],
    },
    {
        "id": "message-ask",
        "description": "Ask one session a question and print its answer.",
        "command": 'cc-devthrottle message ask <target> "<question>"',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "question", "required": True},
        ],
    },
    {
        "id": "fleet-selftest",
        "description": "Run an end-to-end fleet messaging smoke test.",
        "command": "cc-devthrottle selftest",
        "mutatesState": True,
        "args": [],
    },
]


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-devthrottle v{__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Unified DevThrottle command-line surface."""


@app.command()
def actions(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List agent-discoverable actions."""
    if json_output:
        console.print(json.dumps({"actions": _ACTIONS}, indent=2))
        return

    table = Table(show_header=True, header_style="bold")
    table.add_column("ACTION")
    table.add_column("COMMAND")
    for action in _ACTIONS:
        table.add_row(str(action["id"]), str(action["command"]))
    console.print(table)


@session_app.command("list")
def session_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """List every session running across the fleet."""
    list_sessions(json_output)


@session_app.command()
def whoami() -> None:
    """Show this session's own fleet identity."""
    show_whoami()


@session_app.command()
def rename(
    target_or_name: str = typer.Argument(
        ..., help="New name for this session, or a target when NEW_NAME is also provided."
    ),
    new_name: Optional[str] = typer.Argument(
        None, help="New name when an explicit target is provided."
    ),
) -> None:
    """Rename a session, defaulting to the current session."""
    if new_name is None:
        rename_session(None, target_or_name)
    else:
        rename_session(target_or_name, new_name)


@session_app.command()
def spawn(
    repo: str = typer.Argument(..., help="Absolute path to the repository / working directory."),
    agent: str = typer.Option(
        "ClaudeCode",
        "--agent",
        help="Agent CLI: ClaudeCode, Pi, Codex, Gemini, OpenCode, Grok, Copilot, RawCli.",
    ),
    prompt: Optional[str] = typer.Option(
        None, "--prompt", help="First prompt to send once the session is ready."
    ),
    name: Optional[str] = typer.Option(None, "--name", help="Custom display name for the session."),
    session_type: Optional[str] = typer.Option(
        None, "--type", help="Session type: Developer, Implementation, Discuss, Product, QA, Support."
    ),
    command: Optional[str] = typer.Option(
        None, "--command", help="For --agent RawCli: the executable to run (e.g. cmd, pwsh)."
    ),
    command_args: Optional[str] = typer.Option(
        None, "--command-args", help="For --agent RawCli: arguments for the command."
    ),
) -> None:
    """Open a new session on the local Director and print its id."""
    spawn_session(repo, agent, prompt, name, session_type, command, command_args)


@message_app.command("send")
def message_send(
    target: str = typer.Argument(..., help="Target session id, id prefix, or name - or 'all'."),
    message: str = typer.Argument(..., help="The message text to send."),
) -> None:
    """Send a message to one session, or every session when TARGET is 'all'."""
    send_message(target, message)


@message_app.command("ask")
def message_ask(
    target: str = typer.Argument(..., help="Target session id, id prefix, or name (single session)."),
    question: str = typer.Argument(..., help="The question to ask."),
    timeout_ms: int = typer.Option(
        120000, "--timeout-ms", help="How long to wait for the answer, in milliseconds."
    ),
) -> None:
    """Ask TARGET the QUESTION and print the answer."""
    ask_session(target, question, timeout_ms)


@app.command()
def selftest(
    timeout_ms: int = typer.Option(
        25000, "--timeout-ms", help="How long the ask step waits for the responder."
    ),
) -> None:
    """Run the fleet messaging self-test against the local Director."""
    run_selftest(timeout_ms)


if __name__ == "__main__":
    app()
