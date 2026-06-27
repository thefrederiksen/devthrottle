"""CLI for cc-devthrottle - unified DevThrottle command surface."""

from __future__ import annotations

import json
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

from . import __version__
from . import schedule_ops
from . import setup_ops
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
schedule_app = typer.Typer(
    help="Manage Gateway schedules.", add_completion=False, no_args_is_help=True
)
setup_app = typer.Typer(
    help="Install, update, and repair DevThrottle.", add_completion=False, no_args_is_help=True
)
app.add_typer(session_app, name="session")
app.add_typer(message_app, name="message")
app.add_typer(schedule_app, name="schedule")
app.add_typer(setup_app, name="setup")
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
    {
        "id": "schedule-list",
        "description": "List Gateway schedules.",
        "command": "cc-devthrottle schedule list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "schedule-create",
        "description": "Create a Gateway schedule.",
        "command": "cc-devthrottle schedule create --name <name> --machine <machine> --repo <repo> --cron <expr> --tz <tz> --seed <prompt>",
        "mutatesState": True,
        "args": [
            {"name": "name", "required": True},
            {"name": "machine", "required": True},
            {"name": "repo", "required": True},
            {"name": "cron_or_at", "required": True},
            {"name": "tz", "required": True},
            {"name": "seed_or_worklist", "required": True},
        ],
    },
    {
        "id": "schedule-run",
        "description": "Fire a Gateway schedule immediately.",
        "command": "cc-devthrottle schedule run <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "setup-status",
        "description": "Show local DevThrottle setup status.",
        "command": "cc-devthrottle setup status",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "setup-install",
        "description": "Install or repair DevThrottle from the latest GitHub release.",
        "command": "cc-devthrottle setup install",
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


@schedule_app.callback()
def schedule_main(
    gateway: Optional[str] = typer.Option(
        None,
        "--gateway",
        help="Override the Gateway base URL.",
    ),
) -> None:
    """Manage Gateway schedules."""
    schedule_ops.set_gateway_override(gateway)


@schedule_app.command("list")
def schedule_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List every schedule on the Gateway."""
    schedule_ops.list_jobs(json_output)


@schedule_app.command("get")
def schedule_get(
    job_id: str = typer.Argument(..., help="The schedule id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show one schedule in full."""
    schedule_ops.get_job(job_id, json_output)


@schedule_app.command("runs")
def schedule_runs(
    job_id: str = typer.Argument(..., help="The schedule id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show run history for a schedule."""
    schedule_ops.list_runs(job_id, json_output)


@schedule_app.command("create")
def schedule_create(
    name: str = typer.Option(..., "--name", help="Human-readable label for the schedule."),
    machine: str = typer.Option(..., "--machine", help="Target machine name."),
    repo: str = typer.Option(..., "--repo", help="Working directory the fired session runs in."),
    at: Optional[str] = typer.Option(None, "--at", help="One-off local timestamp."),
    cron: Optional[str] = typer.Option(None, "--cron", help="Recurring 5-field cron expression."),
    tz: str = typer.Option(..., "--tz", help="IANA/Windows time zone id."),
    seed: Optional[str] = typer.Option(None, "--seed", help="Skill or prompt the session runs."),
    worklist: Optional[str] = typer.Option(None, "--worklist", help="Named work list to drain."),
    notify_on: str = typer.Option(
        schedule_ops.NOTIFY_NONE,
        "--notify-on",
        help="Run-complete notification: none, always, or failure.",
    ),
    notify_webhook: Optional[str] = typer.Option(
        None,
        "--notify-webhook",
        help="Optional outbound webhook URL.",
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the created schedule as JSON."),
) -> None:
    """Create a schedule, one-off with --at or recurring with --cron."""
    schedule_ops.create_job(
        name,
        machine,
        repo,
        at,
        cron,
        tz,
        seed,
        worklist,
        notify_on,
        notify_webhook,
        json_output,
    )


@schedule_app.command("run")
def schedule_run(
    job_id: str = typer.Argument(..., help="The schedule id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the run record as JSON."),
) -> None:
    """Fire a schedule immediately."""
    schedule_ops.run_now(job_id, json_output)


@schedule_app.command("enable")
def schedule_enable(job_id: str = typer.Argument(..., help="The schedule id.")) -> None:
    """Enable a schedule so it fires on schedule again."""
    schedule_ops.enable_job(job_id)


@schedule_app.command("disable")
def schedule_disable(job_id: str = typer.Argument(..., help="The schedule id.")) -> None:
    """Disable a schedule while keeping its definition."""
    schedule_ops.disable_job(job_id)


@schedule_app.command("delete")
def schedule_delete(job_id: str = typer.Argument(..., help="The schedule id.")) -> None:
    """Delete a schedule from the Gateway."""
    schedule_ops.delete_job(job_id)


@schedule_app.command("endpoint")
def schedule_endpoint(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show the Gateway base URL used by schedule commands."""
    schedule_ops.endpoint(json_output)


@setup_app.command("status")
def setup_status(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show local DevThrottle setup status."""
    setup_ops.status(json_output)


@setup_app.command("install")
def setup_install() -> None:
    """Install DevThrottle from the latest GitHub release."""
    setup_ops.install()


@setup_app.command("update")
def setup_update() -> None:
    """Update DevThrottle from the latest GitHub release."""
    setup_ops.install()


@setup_app.command("repair")
def setup_repair() -> None:
    """Repair the local DevThrottle install."""
    setup_ops.install()


@setup_app.command("doctor")
def setup_doctor(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show setup diagnostics."""
    setup_ops.status(json_output)


if __name__ == "__main__":
    app()
