"""CLI for cc-spawn - open a session on the local Director and print its id."""

import sys
from pathlib import Path
from typing import Any, Dict, Optional

import typer
from rich.console import Console

# Share the one tools venv: make cc_shared importable when run from source (issue #721).
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402

from . import __version__  # noqa: E402

console = Console()


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-spawn v{__version__}")
        raise typer.Exit()


def _run(
    repo: str = typer.Argument(..., help="Absolute path to the repository / working directory."),
    agent: str = typer.Option(
        "ClaudeCode", "--agent",
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
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Open a new session on the local Director in REPO and print its id."""
    body: Dict[str, Any] = {"repoPath": repo, "agent": agent}
    if prompt:
        body["prePrompt"] = prompt
    if session_type:
        body["type"] = session_type
    if command:
        body["command"] = command
    if command_args:
        body["commandArgs"] = command_args

    try:
        resp = director.post_json("sessions", body)
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    sid = director.field(resp, "sessionId", "SessionId")
    if not sid:
        console.print("[red]Error:[/red] the Director did not return a session id.")
        raise typer.Exit(1)

    if name:
        # Set the display name with a follow-up rename; a failure here is non-fatal (the session
        # exists), but report it so the caller is not misled.
        try:
            director.patch_json(f"sessions/{sid}", {"name": name})
        except director.DirectorError as err:
            console.print(f"[yellow]Session created, but rename failed:[/yellow] {err}")

    short = director.short_id(sid)
    label = name or director.field(resp, "name", "Name") or short
    console.print(f"[green]Opened[/green] session {short} ({label}).")
    console.print(f"id: {sid}")
    console.print(f'Message it:  cc-send {short} "<message>"   |   Ask it:  cc-ask {short} "<question>"')


def app() -> None:
    """Console-script entry point. typer.run builds a single-command CLI so the positional
    REPO argument binds cleanly."""
    typer.run(_run)


if __name__ == "__main__":
    app()
