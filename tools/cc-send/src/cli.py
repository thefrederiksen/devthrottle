"""CLI for cc-send - message another session in the fleet (or 'all' of them)."""

import sys
from pathlib import Path
from typing import Any, Dict, Optional

import typer
from rich.console import Console

# Share the one tools venv: make cc_shared importable when run from source (issue #705).
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402

from . import __version__  # noqa: E402

app = typer.Typer(
    name="cc-send",
    help="Send a message to another session in the fleet (use 'all' to broadcast).",
    add_completion=False,
)
console = Console()


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-send v{__version__}")
        raise typer.Exit()


def _report(resp: Any, who: str) -> None:
    accepted = False
    count = 0
    err: Optional[str] = None
    if isinstance(resp, dict):
        accepted = bool(resp.get("accepted", resp.get("Accepted", False)))
        count = int(resp.get("deliveredCount", resp.get("DeliveredCount", 0)) or 0)
        err = resp.get("error") or resp.get("Error")
    if accepted:
        console.print(f"[green]Delivered[/green] to {who} ({count} session(s)).")
    else:
        console.print(f"[red]Not delivered:[/red] {err or 'unknown error'}")
        raise typer.Exit(1)


@app.callback(invoke_without_command=True)
def main(
    target: str = typer.Argument(..., help="Target session id, id prefix, or name - or 'all'."),
    message: str = typer.Argument(..., help="The message text to send."),
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Send MESSAGE to the session identified by TARGET (or every session when TARGET is 'all')."""
    me = director.session_id()

    if target.strip().lower() == "all":
        try:
            resp = director.post_json("fleet/broadcast", {"text": message, "fromSessionId": me})
        except director.DirectorError as err:
            console.print(f"[red]Error:[/red] {err}")
            raise typer.Exit(1)
        _report(resp, "everyone")
        return

    try:
        sessions = director.get_json("fleet/sessions") or []
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    matches = director.resolve_target(sessions, target)
    if not matches:
        console.print(
            f"[red]No session matches '{target}'.[/red] Run cc-sessions to see the fleet."
        )
        raise typer.Exit(1)
    if len(matches) > 1:
        console.print(f"[yellow]'{target}' is ambiguous - {len(matches)} matches:[/yellow]")
        for s in matches:
            sid = director.field(s, "sessionId", "SessionId")
            name = director.field(s, "name", "Name") or "(unnamed)"
            machine = director.field(s, "machineName", "MachineName") or "-"
            console.print(f"  {director.short_id(sid)}  {name}  ({machine})")
        console.print("Re-run cc-send with a longer id prefix.")
        raise typer.Exit(1)

    chosen: Dict[str, Any] = matches[0]
    target_sid = director.field(chosen, "sessionId", "SessionId")
    try:
        resp = director.post_json(
            "fleet/send", {"toSessionId": target_sid, "text": message, "fromSessionId": me}
        )
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    name = director.field(chosen, "name", "Name") or director.short_id(target_sid)
    _report(resp, f'{name} ({director.short_id(target_sid)})')
