"""CLI for cc-history - read another fleet session's recent conversation turns."""

import sys
from pathlib import Path
from typing import Any, Dict, List

import typer
from rich.console import Console

# Share the one tools venv: make cc_shared importable when run from source.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

from . import __version__  # noqa: E402

# Force UTF-8 with replacement so an answer glyph in another agent's transcript cannot crash the print.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass

console = Console()


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-history v{__version__}")
        raise typer.Exit()


def _format_parts(parts: List[Dict[str, Any]]) -> str:
    bits: List[str] = []
    for p in parts:
        kind = p.get("kind")
        text = (p.get("text") or "").strip()
        if not text and kind not in ("ToolUse",):
            continue
        if kind == "Text":
            bits.append(text)
        elif kind == "Thinking":
            bits.append(f"[dim](thinking) {text}[/dim]")
        elif kind == "ToolUse":
            bits.append(f"[magenta](tool: {p.get('toolName', '')})[/magenta] {text}")
        elif kind == "ToolResult":
            bits.append(f"[dim](tool result) {text}[/dim]")
        else:
            bits.append(text)
    return "\n".join(bits).strip()


def _run(
    target: str = typer.Argument(..., help="Target session id, id prefix, or name."),
    last: int = typer.Option(10, "--last", "-n", help="How many recent messages to show."),
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Show the last N conversation messages of TARGET (works for Claude, Codex, and Pi sessions)."""
    try:
        sessions, complete, roster_reason, stale_caution = gateway.get_fleet()
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    matches = gateway.resolve_target(sessions, target)
    if not matches:
        console.print(gateway.no_match_message(target))
        # Issue #1051, reworded for #1159 step A: an unreachable Director's sessions are now LISTED rather
        # than dropped, but they are the last thing that machine reported, so "no session matches" can still
        # mean "the list we searched was not current". Do not imply otherwise - "it does not exist" and "I
        # could not see that machine" call for opposite next steps.
        caveat = gateway.roster_caveat(complete, roster_reason)
        if caveat:
            console.print(f"[yellow]The fleet list searched may be incomplete.[/yellow] {caveat}")
        # THE negative answer this caution exists for. A machine that is connected but has not reported
        # recently can be hiding the very thing that was asked for, and this is the only place that
        # matters - a lookup that FOUND its target was plainly not hidden from. Printed here and not on
        # the success path, so it stays rare enough to be read.
        if stale_caution:
            console.print(f"[yellow]{stale_caution}[/yellow]")
        raise typer.Exit(1)
    if len(matches) > 1:
        console.print(f"[yellow]'{target}' is ambiguous - {len(matches)} matches.[/yellow] Use a longer id prefix.")
        raise typer.Exit(1)

    chosen = matches[0]
    sid = gateway.field(chosen, "sessionId", "SessionId")
    name = gateway.field(chosen, "name", "Name") or gateway.short_id(sid)

    try:
        resp = gateway.get_json(f"sessions/{sid}/history")
    except gateway.GatewayError as err:
        console.print(f"[red]{err}[/red]")
        raise typer.Exit(1)

    if not isinstance(resp, dict):
        console.print("(no history available)")
        return

    agent = resp.get("agent", "")
    if resp.get("isSupported") is False:
        console.print(f"[yellow]{name} ({agent}) does not support history reading.[/yellow]")
        return

    all_msgs = resp.get("messages") or []
    messages = all_msgs[-last:] if last and last > 0 else all_msgs
    console.print(f"[dim]-- last {len(messages)} of {len(all_msgs)} messages from {name} ({gateway.short_id(sid)}, {agent}) --[/dim]")
    if not messages:
        console.print("(no history yet)")
        return

    for m in messages:
        role = m.get("role", "?")
        body = _format_parts(m.get("parts") or [])
        color = "cyan" if role == "Assistant" else "green"
        console.print(f"[{color}][{role}][/{color}] {body}")


def app() -> None:
    """Console-script entry point."""
    typer.run(_run)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-history", app=app)


if __name__ == "__main__":
    tool_main()
