"""CLI for cc-whoami - show this session's own fleet identity and how to message others."""

import sys
from pathlib import Path

import typer
from rich.console import Console

# Share the one tools venv: make cc_shared importable when run from source (issue #705).
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402

from . import __version__  # noqa: E402

app = typer.Typer(
    name="cc-whoami",
    help="Show this session's own fleet identity.",
    add_completion=False,
)
console = Console()


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-whoami v{__version__}")
        raise typer.Exit()


@app.callback(invoke_without_command=True)
def main(
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Show this session's own id, name, machine, and repository, plus how to message others."""
    sid = director.session_id()
    if not sid:
        console.print(
            "[red]Error:[/red] CC_SESSION_ID is not set. "
            "cc-whoami only works inside a cc-director session."
        )
        raise typer.Exit(1)

    short = director.short_id(sid)
    try:
        sessions = director.get_json("fleet/sessions") or []
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    me = next(
        (s for s in sessions if director.field(s, "sessionId", "SessionId").lower() == sid.lower()),
        None,
    )
    if me is None:
        console.print(f"You are session {short} (id {sid}).")
    else:
        name = director.field(me, "name", "Name") or "(unnamed)"
        machine = director.field(me, "machineName", "MachineName") or "this machine"
        repo = director.field(me, "repoPath", "RepoPath")
        repo_name = repo.replace("\\", "/").rstrip("/").split("/")[-1] if repo else "-"
        console.print(f'You are session {short} ("{name}") on {machine}, repo {repo_name}.')

    console.print('To message another session:  cc-send <id> "<message>"')
    console.print('To message everyone:         cc-send all "<message>"')
    console.print("To see all sessions:         cc-sessions")
