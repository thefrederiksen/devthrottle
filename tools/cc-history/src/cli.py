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

from cc_shared import director  # noqa: E402

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
        sessions = director.get_json("fleet/sessions") or []
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    matches = director.resolve_target(sessions, target)
    if not matches:
        console.print(f"[red]No session matches '{target}'.[/red] Run cc-sessions to see the fleet.")
        raise typer.Exit(1)
    if len(matches) > 1:
        console.print(f"[yellow]'{target}' is ambiguous - {len(matches)} matches.[/yellow] Use a longer id prefix.")
        raise typer.Exit(1)

    chosen = matches[0]
    sid = director.field(chosen, "sessionId", "SessionId")
    name = director.field(chosen, "name", "Name") or director.short_id(sid)

    try:
        resp = director.get_json(f"sessions/{sid}/history")
    except director.DirectorError as err:
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
    console.print(f"[dim]-- last {len(messages)} of {len(all_msgs)} messages from {name} ({director.short_id(sid)}, {agent}) --[/dim]")
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


if __name__ == "__main__":
    app()
