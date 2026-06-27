"""CLI for cc-status - show what fleet sessions are doing (state, repo, last reason)."""

import sys
from pathlib import Path

import typer
from rich.console import Console

# Share the one tools venv: make cc_shared importable when run from source.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402

from . import __version__  # noqa: E402

# Windows consoles default to a legacy codepage; a session name or reason could carry a glyph that
# cannot be encoded there. Force UTF-8 with replacement so cc-status never crashes while printing.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass

console = Console()


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-status v{__version__}")
        raise typer.Exit()


def _run(
    target: str = typer.Argument("all", help="Session id, id prefix, name, or 'all' (default)."),
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Show what fleet sessions are doing: activity state, agent, repo, and last status reason."""
    try:
        sessions = director.get_json("fleet/sessions") or []
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if target.strip().lower() != "all":
        sessions = director.resolve_target(sessions, target)
        if not sessions:
            console.print(
                f"[red]No session matches '{target}'.[/red] "
                "Run cc-devthrottle session list to see the fleet."
            )
            raise typer.Exit(1)

    me = director.session_id()
    if not sessions:
        console.print("(no sessions running)")
        return

    for s in sessions:
        sid = director.field(s, "sessionId", "SessionId")
        name = director.field(s, "name", "Name") or "(unnamed)"
        agent = director.field(s, "agent", "Agent")
        state = director.field(s, "activityState", "ActivityState")
        reason = director.field(s, "lastStatusReason", "LastStatusReason")
        machine = director.field(s, "machineName", "MachineName")
        repo = director.field(s, "repoPath", "RepoPath")
        you = " [dim](you)[/dim]" if me and sid == me else ""
        console.print(f"[bold]{director.short_id(sid)}[/bold]{you}  {name}  [[cyan]{agent}[/cyan]]  [yellow]{state}[/yellow] - {reason}")
        console.print(f"    [dim]{machine}  {repo}[/dim]")


def app() -> None:
    """Console-script entry point."""
    typer.run(_run)


if __name__ == "__main__":
    app()
