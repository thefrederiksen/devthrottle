"""CLI for cc-status - show what fleet sessions are doing (state, repo, last reason)."""

import sys
from pathlib import Path

import typer
from rich.console import Console

# Share the one tools venv: make cc_shared importable when run from source.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

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
        # Named roster_reason, not reason: the per-session loop below binds `reason` to a status reason.
        sessions, complete, roster_reason, stale_caution = gateway.get_fleet()
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)
    # Issue #1051, reworded for #1159 step A: an unreachable Director's sessions are now LISTED rather than
    # dropped, so the list is no longer short - but those rows are the last that machine reported, not a
    # confirmed present state. Everything below reports on what came back; this is what says how far it can
    # be trusted.
    caveat = gateway.roster_caveat(complete, roster_reason)

    if target.strip().lower() != "all":
        sessions = gateway.resolve_target(sessions, target)
        if not sessions:
            console.print(gateway.no_match_message(target))
            # Not found, or not reached? The two call for opposite next steps, so never imply the first.
            if caveat:
                console.print(f"[yellow]The fleet list searched may be incomplete.[/yellow] {caveat}")
            # The negative answer the second caution exists for: a connected machine whose pushes are
            # late can be hiding the target. Printed here and not on the success path, so it stays rare
            # enough to be read.
            if stale_caution:
                console.print(f"[yellow]{stale_caution}[/yellow]")
            raise typer.Exit(1)

    me = gateway.session_id()
    if not sessions:
        # "(no sessions running)" is a claim about the whole fleet that an incomplete roster cannot
        # support - the sessions may be running fine on the machine we could not read (issue #1051).
        if caveat:
            console.print(f"[yellow](nothing came back, but this is not the whole fleet)[/yellow] {caveat}")
        elif stale_caution:
            console.print("[yellow](nothing came back, but this is not the whole fleet)[/yellow]")
        else:
            console.print("(no sessions running)")
        # Both cautions can be live at once - one Director offline, another connected but quiet - and on
        # an empty answer they say different things. Printed after, never instead of, the other.
        if stale_caution:
            console.print(f"[yellow]{stale_caution}[/yellow]")
        return

    for s in sessions:
        sid = gateway.field(s, "sessionId", "SessionId")
        name = gateway.field(s, "name", "Name") or "(unnamed)"
        agent = gateway.field(s, "agent", "Agent")
        state = gateway.field(s, "activityState", "ActivityState")
        reason = gateway.field(s, "lastStatusReason", "LastStatusReason")
        machine = gateway.field(s, "machineName", "MachineName")
        repo = gateway.field(s, "repoPath", "RepoPath")
        you = " [dim](you)[/dim]" if me and sid == me else ""
        console.print(f"[bold]{gateway.short_id(sid)}[/bold]{you}  {name}  [[cyan]{agent}[/cyan]]  [yellow]{state}[/yellow] - {reason}")
        console.print(f"    [dim]{machine}  {repo}[/dim]")

    if caveat:
        console.print(f"[yellow]This is not the whole fleet.[/yellow] {caveat}")


def app() -> None:
    """Console-script entry point."""
    typer.run(_run)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-status", app=app)


if __name__ == "__main__":
    tool_main()
