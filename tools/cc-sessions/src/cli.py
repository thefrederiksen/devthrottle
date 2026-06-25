"""CLI for cc-sessions - list every session running across the fleet."""

import json as _json
import sys
from pathlib import Path

import typer
from rich.console import Console
from rich.table import Table

# Share the one tools venv: make cc_shared importable when run from source (issue #705).
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402

from . import __version__  # noqa: E402

app = typer.Typer(
    name="cc-sessions",
    help="List every session running across the fleet.",
    add_completion=False,
)
console = Console()


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-sessions v{__version__}")
        raise typer.Exit()


@app.callback(invoke_without_command=True)
def main(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """List every session running across the fleet."""
    try:
        sessions = director.get_json("fleet/sessions") or []
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if json_output:
        console.print(_json.dumps(sessions, indent=2))
        return

    if not sessions:
        console.print("No sessions are running in the fleet.")
        return

    table = Table(show_header=True, header_style="bold")
    table.add_column("ID")
    table.add_column("NAME")
    table.add_column("MACHINE")
    table.add_column("REPOSITORY")
    table.add_column("STATUS")

    me = director.session_id()
    for s in sessions:
        sid = director.field(s, "sessionId", "SessionId")
        name = director.field(s, "name", "Name") or "(unnamed)"
        machine = director.field(s, "machineName", "MachineName") or "-"
        repo = director.field(s, "repoPath", "RepoPath")
        repo_name = repo.replace("\\", "/").rstrip("/").split("/")[-1] if repo else "-"
        status = director.field(s, "activityState", "ActivityState") or "-"
        marker = " (you)" if me and sid.lower() == me.lower() else ""
        table.add_row(director.short_id(sid) + marker, name, machine, repo_name, status)

    console.print(table)
