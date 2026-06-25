"""CLI for cc-ask - ask one session a question and print its answer."""

import sys
from pathlib import Path
from typing import Any, Dict

import typer
from rich.console import Console

# Share the one tools venv: make cc_shared importable when run from source (issue #717).
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402

from . import __version__  # noqa: E402

console = Console()


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-ask v{__version__}")
        raise typer.Exit()


def _run(
    target: str = typer.Argument(..., help="Target session id, id prefix, or name (single session)."),
    question: str = typer.Argument(..., help="The question to ask."),
    timeout_ms: int = typer.Option(
        120000, "--timeout-ms", help="How long to wait for the answer, in milliseconds."
    ),
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Ask TARGET the QUESTION and print the answer (or a clear timeout/error)."""
    if target.strip().lower() == "all":
        console.print("[red]cc-ask targets a single session.[/red] Use cc-send all for a broadcast.")
        raise typer.Exit(1)

    me = director.session_id()

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
        console.print(f"[yellow]'{target}' is ambiguous - {len(matches)} matches:[/yellow]")
        for s in matches:
            sid = director.field(s, "sessionId", "SessionId")
            name = director.field(s, "name", "Name") or "(unnamed)"
            console.print(f"  {director.short_id(sid)}  {name}")
        console.print("Re-run cc-ask with a longer id prefix.")
        raise typer.Exit(1)

    chosen: Dict[str, Any] = matches[0]
    target_sid = director.field(chosen, "sessionId", "SessionId")

    # The HTTP wait must outlast the answer wait; give the Director margin over timeout_ms.
    http_timeout = max(30.0, timeout_ms / 1000.0 + 15.0)
    try:
        resp = director.post_json(
            "fleet/ask",
            {"toSessionId": target_sid, "question": question, "fromSessionId": me, "timeoutMs": timeout_ms},
            timeout=http_timeout,
        )
    except director.DirectorError as err:
        # Covers the 504 timeout, 502 unreachable, and 404 unknown-target cases - each carries a
        # clear message extracted from the Director's response.
        console.print(f"[red]{err}[/red]")
        raise typer.Exit(1)

    answer = (director.field(resp, "answer", "Answer") if isinstance(resp, dict) else "").strip()
    name = director.field(chosen, "name", "Name") or director.short_id(target_sid)
    console.print(f"[dim]-- answer from {name} ({director.short_id(target_sid)}) --[/dim]")
    console.print(answer if answer else "(the target produced no output)")


def app() -> None:
    """Console-script entry point. typer.run builds a single-command CLI so the positional
    TARGET/QUESTION arguments bind cleanly (no subcommand ambiguity)."""
    typer.run(_run)


if __name__ == "__main__":
    app()
