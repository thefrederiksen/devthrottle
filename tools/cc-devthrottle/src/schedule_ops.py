"""Gateway schedule operations for cc-devthrottle."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import requests
import typer
from rich import box
from rich.console import Console
from rich.table import Table
from urllib.parse import urlparse

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared.config import CCDirectorConfig  # noqa: E402

LOOPBACK_DEFAULT = "http://127.0.0.1:7878"
TIMEOUT_SECONDS = 10
SCHEDULE_RECURRING = "recurring"
SCHEDULE_ONE_OFF = "oneOff"
NOTIFY_NONE = "none"
NOTIFY_ALWAYS = "always"
NOTIFY_FAILURE = "failure"
NOTIFY_CHOICES = (NOTIFY_NONE, NOTIFY_ALWAYS, NOTIFY_FAILURE)

console = Console()
err_console = Console(stderr=True)
gateway_override: Optional[str] = None


class GatewayError(Exception):
    """A handled, user-facing failure talking to the Gateway."""


def set_gateway_override(value: Optional[str]) -> None:
    global gateway_override
    gateway_override = value.rstrip("/") if value else None


def resolve_base_url() -> str:
    config = CCDirectorConfig().load()
    url = (config.gateway.url or "").strip()
    return url.rstrip("/") if url else LOOPBACK_DEFAULT


def _auth_token() -> str:
    config = CCDirectorConfig().load()
    return (config.gateway.token or "").strip()


def _is_loopback(url: str) -> bool:
    """True when the URL targets this machine (a loopback Gateway needs no token)."""
    host = (urlparse(url).hostname or "").lower()
    return host in ("127.0.0.1", "localhost", "::1")


class ScheduleClient:
    """Talks to one Gateway's cron/schedule surface."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or resolve_base_url()).rstrip("/")
        self._token = _auth_token()
        # Make the auth requirement explicit instead of silently issuing an unauthenticated
        # request to a remote Gateway: a loopback Gateway on this machine needs no token, but a
        # remote one does.
        if not self._token and not _is_loopback(self.base_url):
            raise GatewayError(
                f"Gateway URL {self.base_url} is remote but gateway.token is not set. "
                "Set it with 'cc-devthrottle settings set gateway.token <token>' "
                "(a loopback Gateway on this machine needs no token)."
            )

    def _headers(self) -> Dict[str, str]:
        headers = {"Accept": "application/json"}
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        return headers

    def _request(
        self, method: str, path: str, json_body: Optional[Dict[str, Any]] = None
    ) -> requests.Response:
        url = f"{self.base_url}{path}"
        try:
            return requests.request(
                method,
                url,
                json=json_body,
                headers=self._headers(),
                timeout=TIMEOUT_SECONDS,
            )
        except requests.exceptions.ConnectionError as exc:
            raise GatewayError(
                f"Gateway not reachable at {self.base_url}. "
                "Is the Gateway tray app running on this machine? "
                "If you target a remote Gateway, set gateway.url with "
                "'cc-devthrottle settings set gateway.url <url>'."
            ) from exc
        except requests.exceptions.Timeout as exc:
            raise GatewayError(
                f"Gateway at {self.base_url} did not respond within {TIMEOUT_SECONDS}s."
            ) from exc

    @staticmethod
    def _gateway_message(resp: requests.Response) -> str:
        try:
            data = resp.json()
            if isinstance(data, dict) and data.get("error"):
                return str(data["error"])
        except ValueError:
            pass
        text = (resp.text or "").strip()
        return text if text else f"Gateway returned HTTP {resp.status_code}"

    def _ok_or_raise(self, resp: requests.Response) -> Dict[str, Any]:
        if 200 <= resp.status_code < 300:
            if not resp.content:
                return {}
            return resp.json()
        raise GatewayError(self._gateway_message(resp))

    def list_jobs(self) -> List[Dict[str, Any]]:
        data = self._ok_or_raise(self._request("GET", "/cron/jobs"))
        return list(data.get("jobs", []))

    def get_job(self, job_id: str) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("GET", f"/cron/jobs/{job_id}"))

    def create_job(self, job: Dict[str, Any]) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("POST", "/cron/jobs", job))

    def update_job(self, job_id: str, job: Dict[str, Any]) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("PUT", f"/cron/jobs/{job_id}", job))

    def delete_job(self, job_id: str) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("DELETE", f"/cron/jobs/{job_id}"))

    def run_now(self, job_id: str) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("POST", f"/cron/jobs/{job_id}/run"))

    def list_runs(self, job_id: str) -> List[Dict[str, Any]]:
        data = self._ok_or_raise(self._request("GET", f"/cron/jobs/{job_id}/runs"))
        return list(data.get("runs", []))

    def set_enabled(self, job_id: str, enabled: bool) -> Dict[str, Any]:
        job = self.get_job(job_id)
        job["enabled"] = enabled
        return self.update_job(job_id, job)


def _fail(message: str) -> None:
    err_console.print(f"[red]Error:[/red] {message}")
    raise typer.Exit(1)


def _client() -> ScheduleClient:
    return ScheduleClient(base_url=gateway_override)


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


def list_jobs(json_output: bool) -> None:
    try:
        jobs = _client().list_jobs()
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(jobs, indent=2))
        return

    if not jobs:
        console.print(
            "No schedules on the Gateway yet. Create one with 'cc-devthrottle schedule create'."
        )
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
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


def get_job(job_id: str, json_output: bool) -> None:
    try:
        job = _client().get_job(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(job, indent=2))
        return

    target = job.get("target") or {}
    console.print(f"[bold]{_fmt(job.get('name'))}[/bold]  ({_fmt(job.get('id'))})")
    console.print(f"  Enabled:    {'yes' if job.get('enabled') else 'no'}")
    console.print(f"  Machine:    {_fmt(target.get('machine'))}")
    console.print(f"  Runs:       {_runs_label(job)}")
    console.print(f"  Schedule:   {_schedule_label(job)}  ({_fmt(job.get('timeZoneId'))})")
    console.print(f"  Notify:     {_notify_label(job)}")
    console.print(f"  Next run:   {_fmt(job.get('nextRunUtc'))} UTC")
    console.print(f"  Last fired: {_fmt(job.get('lastFiredUtc'))}  ({_fmt(job.get('lastStatus'))})")
    console.print(f"  Created:    {_fmt(job.get('createdUtc'))} UTC")


def list_runs(job_id: str, json_output: bool) -> None:
    try:
        history = _client().list_runs(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(history, indent=2))
        return

    if not history:
        console.print("No runs recorded yet for this schedule.")
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
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


def create_job(
    name: str,
    machine: str,
    repo: str,
    at: Optional[str],
    cron: Optional[str],
    tz: str,
    seed: Optional[str],
    worklist: Optional[str],
    notify_on: str,
    notify_webhook: Optional[str],
    json_output: bool,
) -> None:
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
        print(json.dumps(created, indent=2))
        return

    console.print("[green]Created schedule.[/green]")
    console.print(f"  Id:        {_fmt(created.get('id'))}")
    console.print(f"  Name:      {_fmt(created.get('name'))}")
    console.print(f"  Next run:  {_fmt(created.get('nextRunUtc'))} UTC")


def run_now(job_id: str, json_output: bool) -> None:
    try:
        record = _client().run_now(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(record, indent=2))
        return

    console.print("[green]Fired the schedule.[/green]")
    console.print(f"  Fired:   {_fmt(record.get('firedUtc'))} UTC")
    console.print(f"  Target:  {_fmt(record.get('targetDirectorId'))}")
    console.print(f"  Session: {_fmt(record.get('sessionId'))}")
    console.print(f"  Infra:   {_fmt(record.get('infraStatus'))}")
    console.print(f"  Task:    {_fmt(record.get('taskStatus'))}")


def enable_job(job_id: str) -> None:
    try:
        job = _client().set_enabled(job_id, True)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"[green]Enabled[/green] {_fmt(job.get('name'))} ({_fmt(job.get('id'))}).")


def disable_job(job_id: str) -> None:
    try:
        job = _client().set_enabled(job_id, False)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"[yellow]Disabled[/yellow] {_fmt(job.get('name'))} ({_fmt(job.get('id'))}).")


def delete_job(job_id: str) -> None:
    try:
        _client().delete_job(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"[green]Deleted[/green] schedule {job_id}.")


def endpoint(json_output: bool) -> None:
    base = gateway_override or resolve_base_url()
    if json_output:
        print(json.dumps({"base_url": base}, indent=2))
    else:
        console.print(base)
