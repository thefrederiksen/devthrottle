"""CLI interface for cc-cron.

A thin REST consumer of the Gateway cron surface (epic #479). Every subcommand maps to
exactly one Gateway call; cc-cron owns no job state and runs no scheduler of its own.

    cc-cron list                       GET    /cron/jobs
    cc-cron get <id>                   GET    /cron/jobs/{id}
    cc-cron runs <id>                  GET    /cron/jobs/{id}/runs
    cc-cron create ...                 POST   /cron/jobs
    cc-cron run <id>                   POST   /cron/jobs/{id}/run
    cc-cron enable <id>                PUT    /cron/jobs/{id}   (enabled = true)
    cc-cron disable <id>               PUT    /cron/jobs/{id}   (enabled = false)
    cc-cron delete <id>                DELETE /cron/jobs/{id}
"""

import json
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

from . import __version__
from .gateway import CronClient, GatewayError, resolve_base_url

app = typer.Typer(
    name="cc-cron",
    help="Manage cron jobs on the DevThrottle Gateway (schedule sessions and work-list drains).",
    no_args_is_help=True,
)
console = Console()
err_console = Console(stderr=True)

# Schedule-kind values on the wire - must match CcDirector.Gateway.CronSchedule.
SCHEDULE_RECURRING = "recurring"
SCHEDULE_ONE_OFF = "oneOff"

# Run-complete notification policy values on the wire - must match
# CcDirector.Gateway.Contracts.CronNotify.
NOTIFY_NONE = "none"
NOTIFY_ALWAYS = "always"
NOTIFY_FAILURE = "failure"
NOTIFY_CHOICES = (NOTIFY_NONE, NOTIFY_ALWAYS, NOTIFY_FAILURE)

# Set by the --gateway global option; None means "discover from config" (the default).
_gateway_override: Optional[str] = None


def version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-cron v{__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(
        None, "--version", "-v", callback=version_callback, help="Show version"
    ),
    gateway: Optional[str] = typer.Option(
        None,
        "--gateway",
        help="Override the Gateway base URL (default: discover from config gateway.url, "
        "else the loopback default).",
    ),
) -> None:
    """Manage cron jobs on the DevThrottle Gateway."""
    global _gateway_override
    _gateway_override = gateway.rstrip("/") if gateway else None


# ---- helpers ----


def _fail(message: str) -> None:
    """Print a handled error to stderr (no stack trace) and exit non-zero."""
    err_console.print(f"[red]Error:[/red] {message}")
    raise typer.Exit(1)


def _client() -> CronClient:
    return CronClient(base_url=_gateway_override)


def _fmt(value: Optional[str]) -> str:
    return value if value else "-"


def _schedule_label(job: dict) -> str:
    kind = (job.get("scheduleKind") or "").lower()
    if kind == SCHEDULE_RECURRING.lower():
        return f"cron {_fmt(job.get('cronExpression'))}"
    return f"once @ {_fmt(job.get('runAt'))}"


def _runs_label(job: dict) -> str:
    action = job.get("action") or {}
    work_list = action.get("workListName")
    if work_list:
        return f"work list {work_list}"
    return f"skill {_fmt(action.get('seed'))}"


def _notify_label(job: dict) -> str:
    policy = (job.get("notifyOn") or NOTIFY_NONE).lower()
    if policy == NOTIFY_NONE:
        return "off"
    webhook = job.get("notifyWebhookUrl")
    base = "always (success + failure)" if policy == NOTIFY_ALWAYS else "on failure"
    return f"{base} + webhook {webhook}" if webhook else base


# ---- read commands ----


@app.command(name="list")
def list_jobs(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """List every cron job on the Gateway."""
    try:
        jobs = _client().list_jobs()
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        console.print(json.dumps(jobs, indent=2))
        return

    if not jobs:
        console.print("No cron jobs on the Gateway yet. Create one with 'cc-cron create'.")
        return

    table = Table(show_header=True, header_style="bold")
    table.add_column("Id")
    table.add_column("Name")
    table.add_column("Machine")
    table.add_column("Runs")
    table.add_column("Schedule")
    table.add_column("Next run (UTC)")
    table.add_column("Enabled")

    for job in jobs:
        target = job.get("target") or {}
        table.add_row(
            _fmt(job.get("id")),
            _fmt(job.get("name")),
            _fmt(target.get("machine")),
            _runs_label(job),
            _schedule_label(job),
            _fmt(job.get("nextRunUtc")),
            "yes" if job.get("enabled") else "no",
        )
    console.print(table)


@app.command()
def get(
    job_id: str = typer.Argument(..., help="The cron job id"),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show one cron job in full."""
    try:
        job = _client().get_job(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        console.print(json.dumps(job, indent=2))
        return

    target = job.get("target") or {}
    action = job.get("action") or {}
    console.print(f"[bold]{_fmt(job.get('name'))}[/bold]  ({_fmt(job.get('id'))})")
    console.print(f"  Enabled:    {'yes' if job.get('enabled') else 'no'}")
    console.print(f"  Machine:    {_fmt(target.get('machine'))}")
    console.print(f"  Repo:       {_fmt(action.get('repoPath'))}")
    console.print(f"  Runs:       {_runs_label(job)}")
    console.print(f"  Schedule:   {_schedule_label(job)}  ({_fmt(job.get('timeZoneId'))})")
    console.print(f"  Notify:     {_notify_label(job)}")
    console.print(f"  Next run:   {_fmt(job.get('nextRunUtc'))} UTC")
    console.print(f"  Last fired: {_fmt(job.get('lastFiredUtc'))}  ({_fmt(job.get('lastStatus'))})")
    console.print(f"  Created:    {_fmt(job.get('createdUtc'))} UTC")


@app.command()
def runs(
    job_id: str = typer.Argument(..., help="The cron job id"),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show the run history for a cron job (newest first as the Gateway returns it)."""
    try:
        history = _client().list_runs(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        console.print(json.dumps(history, indent=2))
        return

    if not history:
        console.print("No runs recorded yet for this job.")
        return

    table = Table(show_header=True, header_style="bold")
    table.add_column("Scheduled (UTC)")
    table.add_column("Fired (UTC)")
    table.add_column("Target")
    table.add_column("Session")
    table.add_column("Infra")
    table.add_column("Task")

    for run in history:
        table.add_row(
            _fmt(run.get("scheduledUtc")),
            _fmt(run.get("firedUtc")),
            _fmt(run.get("targetDirectorId")),
            _fmt(run.get("sessionId")),
            _fmt(run.get("infraStatus")),
            _fmt(run.get("taskStatus")),
        )
    console.print(table)


# ---- create ----


@app.command()
def create(
    name: str = typer.Option(..., "--name", help="Human-readable label for the job"),
    machine: str = typer.Option(..., "--machine", help="Target machine name (from 'GET /directors')"),
    repo: str = typer.Option(..., "--repo", help="Working directory the fired session runs in"),
    at: Optional[str] = typer.Option(
        None, "--at", help="One-off local timestamp, e.g. 2026-06-28T18:00:00 (use with --tz)"
    ),
    cron: Optional[str] = typer.Option(
        None, "--cron", help='Recurring 5-field cron expression, e.g. "0 0 * * *" (use with --tz)'
    ),
    tz: str = typer.Option(..., "--tz", help="IANA/Windows time zone id, e.g. America/New_York"),
    seed: Optional[str] = typer.Option(
        None, "--seed", help="Skill or prompt the session runs, e.g. /help (one of --seed/--worklist)"
    ),
    worklist: Optional[str] = typer.Option(
        None, "--worklist", help="Named work list to drain (one of --seed/--worklist)"
    ),
    notify_on: str = typer.Option(
        NOTIFY_NONE,
        "--notify-on",
        help="Run-complete notification: none (default), always, or failure. Opt-in per job - "
        "rides the existing fleet channel (desktop + phone).",
    ),
    notify_webhook: Optional[str] = typer.Option(
        None,
        "--notify-webhook",
        help="Optional outbound webhook URL that also receives the run-complete payload (with --notify-on).",
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the created job as JSON"),
) -> None:
    """Create a cron job (one-off with --at, or recurring with --cron)."""
    # Pre-flight the few client-side combinations that the Gateway cannot disambiguate from
    # a bad payload. The schedule grammar itself (bad cron, past one-off, unknown tz) is left
    # to the Gateway so its validation message is the single source of truth (AC4).
    if bool(at) == bool(cron):
        _fail("specify exactly one of --at (one-off) or --cron (recurring).")
        return
    if not seed and not worklist:
        _fail("specify what to run: either --seed <text> or --worklist <name>.")
        return
    if seed and worklist:
        _fail("specify only one of --seed or --worklist, not both.")
        return

    notify_value = (notify_on or NOTIFY_NONE).strip().lower()
    if notify_value not in NOTIFY_CHOICES:
        _fail(f"--notify-on must be one of {', '.join(NOTIFY_CHOICES)}.")
        return

    job = {
        "name": name,
        "enabled": True,
        "scheduleKind": SCHEDULE_ONE_OFF if at else SCHEDULE_RECURRING,
        "cronExpression": cron if cron else None,
        "runAt": at if at else None,
        "timeZoneId": tz,
        "target": {"machine": machine},
        "action": {
            "repoPath": repo,
            "seed": seed or "",
            "workListName": worklist if worklist else None,
        },
        "preventOverlap": True,
        "notifyOn": notify_value,
        "notifyWebhookUrl": notify_webhook if notify_webhook else None,
    }

    try:
        created = _client().create_job(job)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        console.print(json.dumps(created, indent=2))
        return

    console.print("[green]Created cron job.[/green]")
    console.print(f"  Id:        {_fmt(created.get('id'))}")
    console.print(f"  Name:      {_fmt(created.get('name'))}")
    console.print(f"  Next run:  {_fmt(created.get('nextRunUtc'))} UTC")


# ---- firing + lifecycle ----


@app.command()
def run(
    job_id: str = typer.Argument(..., help="The cron job id"),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the run record as JSON"),
) -> None:
    """Fire a cron job immediately (run-now)."""
    try:
        record = _client().run_now(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        console.print(json.dumps(record, indent=2))
        return

    console.print("[green]Fired the job.[/green]")
    console.print(f"  Fired:   {_fmt(record.get('firedUtc'))} UTC")
    console.print(f"  Target:  {_fmt(record.get('targetDirectorId'))}")
    console.print(f"  Session: {_fmt(record.get('sessionId'))}")
    console.print(f"  Infra:   {_fmt(record.get('infraStatus'))}")
    console.print(f"  Task:    {_fmt(record.get('taskStatus'))}")


@app.command()
def enable(
    job_id: str = typer.Argument(..., help="The cron job id"),
) -> None:
    """Enable a cron job so it fires on schedule again."""
    try:
        job = _client().set_enabled(job_id, True)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"[green]Enabled[/green] {_fmt(job.get('name'))} ({_fmt(job.get('id'))}).")


@app.command()
def disable(
    job_id: str = typer.Argument(..., help="The cron job id"),
) -> None:
    """Disable a cron job so it stops firing (the definition is kept)."""
    try:
        job = _client().set_enabled(job_id, False)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"[yellow]Disabled[/yellow] {_fmt(job.get('name'))} ({_fmt(job.get('id'))}).")


@app.command()
def delete(
    job_id: str = typer.Argument(..., help="The cron job id"),
) -> None:
    """Delete a cron job from the Gateway."""
    try:
        _client().delete_job(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"[green]Deleted[/green] cron job {job_id}.")


@app.command()
def endpoint(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show the Gateway base URL cc-cron resolves to (for diagnostics)."""
    base = _gateway_override or resolve_base_url()
    if json_output:
        console.print(json.dumps({"base_url": base}, indent=2))
    else:
        console.print(base)


if __name__ == "__main__":
    app()
