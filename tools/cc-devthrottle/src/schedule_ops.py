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

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import usage_errors  # noqa: E402

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


#: THE SAME CLASS THE SHARED TRANSPORT RAISES, not a look-alike beside it.
#:
#: This used to be its own `class GatewayError(Exception)`, while `gateway.gateway_base_url()` and
#: `gateway.session_key()` - called directly from this module - raise `cc_shared.gateway.GatewayError`.
#: Every `except GatewayError` here therefore missed the no-Gateway failure entirely, and the command
#: died with a Rich traceback. The owner accepted "no Gateway means no agent tooling" on the promise of
#: a CLEAR SENTENCE naming the remedy; a stack trace is not that sentence.
#:
#: Aliasing rather than catching both is deliberate: two names for one idea is what caused this, and a
#: second except clause on every handler would leave the trap in place for the next handler written.
GatewayError = gateway.GatewayError


def set_gateway_override(value: Optional[str]) -> None:
    global gateway_override
    gateway_override = value.rstrip("/") if value else None


def resolve_base_url() -> str:
    """The Gateway this SESSION was told to call. See mission_ops for the reasoning."""
    return gateway.gateway_base_url()


def _auth_token() -> str:
    """This session's own Gateway key, replacing the account-wide token this path used to present."""
    return gateway.session_key()


class ScheduleClient:
    """Talks to one Gateway's cron/schedule surface."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or resolve_base_url()).rstrip("/")
        # A session key is REQUIRED, and _auth_token raises with the remedy when there is none.
        # The old exemption - "a loopback Gateway on this machine needs no token" - is deliberately
        # gone: the credential identifies WHICH SESSION is calling, and that is as necessary on this
        # machine as on any other. It was the address, never the caller, that made loopback special.
        self._token = _auth_token()

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
            # The shared guard, not a bare resp.json(): a request no endpoint matches falls
            # through to the Gateway's web app and answers HTTP 200 with text/html (issue #2486).
            return gateway.parse_json_body(resp, self.base_url)
        raise GatewayError(self._gateway_message(resp))

    def list_jobs(self) -> List[Dict[str, Any]]:
        data = self._ok_or_raise(self._request("GET", "/cron/jobs"))
        # Absent is not empty: an answer with no list of jobs must never read as "no schedules".
        jobs = data.get("jobs") if isinstance(data, dict) else None
        if not isinstance(jobs, list):
            raise GatewayError(
                f"the Gateway at {self.base_url} answered /cron/jobs with no list of jobs; "
                "this tool will not report that as no schedules."
            )
        return jobs

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


# The issue #2201 scope guard that used to sit here (assert_scope_is_unambiguous, reading the
# ports directory) is GONE, and every piece of its premise went separately:
#   * its trigger, CC_DIRECTOR_API, is no longer stamped into any session - the Remove-the-network-
#     port mission deleted the Director listener that variable addressed;
#   * its evidence, the <director-id>.port reservation files, went with the port allocator, and
#     each Director deletes its leftover file on startup;
#   * its hazard - the schedule half reading the account-wide token from config.json under
#     CC_DIRECTOR_ROOT while the session half pointed elsewhere - ended when this client moved to
#     the SESSION's own environment pair (CC_GATEWAY_URL + CC_GATEWAY_SESSION_KEY). Both halves
#     now read the same environment, so the two-intentions mismatch the guard refused can no
#     longer be expressed.


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


# Every field `schedule list --fields` accepts, and the four shown when it is not given. Each is the
# Gateway's own value, unreworded; the seed prompt is long free text and is left to `schedule get`.
SCHEDULE_LIST_FIELDS = (
    "id", "name", "enabled", "next-run", "machine", "kind", "cron", "run-at", "time-zone",
    "work-list", "path", "last-fired", "last-status", "notify", "created",
)
SCHEDULE_LIST_DEFAULT_FIELDS = ("id", "name", "enabled", "next-run")


_usage_error = usage_errors.usage_error


def _bad_job(job_id: Any, what: str) -> None:
    """Refuse a schedule the Gateway would never send, naming it and what is wrong with it."""
    print(
        f"Error: the Gateway returned schedule {axi_output.format_value(job_id)} with {what}. "
        "--json shows the raw rows.",
        file=sys.stderr,
    )
    raise typer.Exit(1)


def _describe(value: Any) -> str:
    return "null" if value is None else f"a {type(value).__name__}"


def _check_jobs(jobs: List[Any]) -> None:
    """A schedule with no id cannot be named by any verb, one whose enabled flag is not true or false
    cannot be counted, and one with no target machine cannot be filtered by machine - the Gateway
    refuses to store any of them. Each is a broken answer from the Gateway, and fails loudly rather
    than dropping out of a filtered list. Two rows with one id are refused too: the id is what every
    other verb addresses. Every other field listed in _JOB_READERS must be present and of the kind the
    Gateway stores."""
    seen = set()
    for index, job in enumerate(jobs):
        job_id = job.get("id") if isinstance(job, dict) else None
        if not isinstance(job_id, str) or not job_id.strip():
            print(
                f"Error: the Gateway returned a schedule with no id (row {index + 1}). "
                "This tool will not list a schedule it cannot name; --json shows the raw rows.",
                file=sys.stderr,
            )
            raise typer.Exit(1)
        if job_id in seen:
            _bad_job(job_id, f"an id that an earlier row already has (row {index + 1})")
        seen.add(job_id)
        machine = _job_machine(job)
        if not isinstance(machine, str) or not machine.strip():
            _bad_job(job_id, "no target machine; every schedule must have one")
        if not isinstance(job.get("enabled"), bool):
            _bad_job(job_id, f"enabled {axi_output.escape_ascii(repr(job.get('enabled')))}; it must be true or false")
        # Every other field is checked here too, before any filter runs, so a broken row fails on every
        # path that reads the rows - never only when its field happens to be on screen.
        for field in SCHEDULE_LIST_FIELDS:
            _JOB_READERS[field](job)


def _job_machine(job: Dict[str, Any]) -> Optional[str]:
    target = job.get("target") or {}
    return target.get("machine") if isinstance(target, dict) else None


def _job_field(
    job: Dict[str, Any], key: str, *, nullable: bool, blank_ok: bool, within: Optional[str] = None
) -> Optional[str]:
    """The value of one schedule field, checked where it is read.

    The Gateway always sends every field of CronJobDto (it serializes nulls), so a missing key is a
    broken answer, never an empty value. Null is accepted only where the DTO is nullable, and blank
    text only where the Gateway's own write check allows it. `within` names the nested object
    (`action`) the key lives in.
    """
    label = f"{within}.{key}" if within else key
    record = job
    if within is not None:
        record = job.get(within)
        if not isinstance(record, dict):
            _bad_job(job["id"], f"{within} {_describe(record) if within in job else 'missing'}; it must be an object")
    if key not in record:
        _bad_job(job["id"], f"no {label}")
    value = record[key]
    if value is None:
        if nullable:
            return None
        _bad_job(job["id"], f"{label} null; it is always text")
    if not isinstance(value, str):
        _bad_job(job["id"], f"{label} {_describe(value)}; it must be {'text or null' if nullable else 'text'}")
    if not blank_ok and not value.strip():
        _bad_job(job["id"], f"a blank {label}; the Gateway never stores one")
    return value


def _job_kind(job: Dict[str, Any]) -> str:
    """The schedule kind as sent. The Gateway accepts recurring or oneOff, ignoring case and spaces."""
    kind = _job_field(job, "scheduleKind", nullable=False, blank_ok=False)
    if kind.strip().lower() not in (SCHEDULE_RECURRING.lower(), SCHEDULE_ONE_OFF.lower()):
        _bad_job(job["id"], f"scheduleKind {axi_output.format_value(kind)}; "
                            f"this tool knows only {SCHEDULE_RECURRING} and {SCHEDULE_ONE_OFF}")
    return kind


def _job_timing(job: Dict[str, Any], key: str, needed_by: str) -> Optional[str]:
    """The cron expression or the run-at time: nullable, but required by the kind that uses it."""
    needed = _job_kind(job).strip().lower() == needed_by.lower()
    return _job_field(job, key, nullable=not needed, blank_ok=not needed)


def _job_notify(job: Dict[str, Any]) -> str:
    """The notify policy. The Gateway normalizes it on every write to one of the three it knows."""
    notify = _job_field(job, "notifyOn", nullable=False, blank_ok=False)
    if notify not in NOTIFY_CHOICES:
        _bad_job(job["id"], f"notifyOn {axi_output.format_value(notify)}; "
                            f"this tool knows only {', '.join(NOTIFY_CHOICES)}")
    return notify


# How each `schedule list` field is read from a CronJobDto (origin/main, CcDirector.Gateway.Contracts),
# with the Gateway's write check (CronSchedule.Validate) deciding which text may be blank. _check_jobs
# runs every reader on every row before any filtering, after checking the id, enabled flag and machine.
_JOB_READERS = {
    "id": lambda j: j["id"],
    "name": lambda j: _job_field(j, "name", nullable=False, blank_ok=False),
    "enabled": lambda j: "yes" if j["enabled"] else "no",
    "next-run": lambda j: _job_field(j, "nextRunUtc", nullable=True, blank_ok=False),
    "machine": _job_machine,
    "kind": _job_kind,
    "cron": lambda j: _job_timing(j, "cronExpression", SCHEDULE_RECURRING),
    "run-at": lambda j: _job_timing(j, "runAt", SCHEDULE_ONE_OFF),
    "time-zone": lambda j: _job_field(j, "timeZoneId", nullable=False, blank_ok=False),
    # A seed job may carry an empty work list name; the Gateway requires a seed or a work list, not both.
    "work-list": lambda j: _job_field(j, "workListName", nullable=True, blank_ok=True, within="action"),
    "path": lambda j: _job_field(j, "repoPath", nullable=False, blank_ok=False, within="action"),
    "last-fired": lambda j: _job_field(j, "lastFiredUtc", nullable=True, blank_ok=False),
    "last-status": lambda j: _job_field(j, "lastStatus", nullable=True, blank_ok=True),
    "notify": _job_notify,
    "created": lambda j: _job_field(j, "createdUtc", nullable=False, blank_ok=False),
}


def _job_record(job: Dict[str, Any], chosen_fields: List[str]) -> Dict[str, object]:
    """The fields `schedule list` shows, for one schedule, each checked as it is read. Ids and names
    are never shortened."""
    return {f: _JOB_READERS[f](job) for f in chosen_fields}


def _matches_machine(job: Dict[str, Any], machine: str) -> bool:
    return (_job_machine(job) or "").lower() == machine.strip().lower()


def list_jobs(
    json_output: bool,
    *,
    enabled: Optional[bool] = None,
    machine: Optional[str] = None,
    fields: Optional[str] = None,
) -> None:
    """List every schedule on the Gateway, optionally only enabled or disabled ones, or one machine's."""
    # Usage errors come before the fetch: a bad flag is the caller's to fix, whatever the Gateway holds.
    if json_output and fields is not None:
        _usage_error("--fields does not apply to --json, which always carries every field. Drop one of them.")
    chosen_fields = usage_errors.parse_fields(fields, SCHEDULE_LIST_FIELDS, SCHEDULE_LIST_DEFAULT_FIELDS)
    if machine is not None and not machine.strip():
        _usage_error("--machine needs a value.")

    try:
        jobs = _client().list_jobs()
    except GatewayError as ex:
        print(f"Error: {axi_output.escape_ascii(str(ex))}", file=sys.stderr)
        raise typer.Exit(1)

    filtered = enabled is not None or machine is not None
    # An unfiltered --json prints exactly what the Gateway sent, as it always has, so it never
    # depends on these checks. Everything else reads the rows, so the rows must be sound.
    if filtered or not json_output:
        _check_jobs(jobs)
    rows = [
        job for job in jobs
        if (enabled is None or job["enabled"] is enabled)
        and (machine is None or _matches_machine(job, machine))
    ]

    if json_output:
        # Plain print, not console.print: Rich wraps long values when stdout is not a terminal. A
        # filter narrows the same bare array; it never changes its shape.
        print(json.dumps(rows if filtered else jobs, indent=2))
        return

    records = [_job_record(job, chosen_fields) for job in rows]
    on = sum(1 for job in rows if job["enabled"])
    # No rows means no breakdown at all: the helper refuses an empty one, and "count: 0" says it all.
    breakdown = [(label, n) for label, n in (("enabled", on), ("disabled", len(rows) - on)) if n] or None
    blocks = [
        axi_output.format_count(len(rows), total=len(jobs) if filtered else None, breakdown=breakdown),
        axi_output.render_list("schedules", chosen_fields, records),
    ]
    if not rows:
        blocks.append("No schedule matches the filter." if jobs else "No schedules on the Gateway.")
    blocks.append(axi_output.format_help(_schedule_list_help(rows, bool(jobs), filtered, chosen_fields)))
    axi_output.write_blocks(sys.stdout, *blocks)


def _schedule_list_help(
    rows: List[Dict[str, Any]], any_jobs: bool, filtered: bool, chosen_fields: List[str]
) -> List[str]:
    """Concrete next commands. Runtime values are placeholders, never guessed."""
    if not rows:
        if not any_jobs:
            return ["cc-devthrottle schedule create --help"]
        return ["cc-devthrottle schedule list", "cc-devthrottle schedule list --help"]
    commands = []
    if not filtered:
        commands.append("cc-devthrottle schedule list --enabled")
    if list(chosen_fields) == list(SCHEDULE_LIST_DEFAULT_FIELDS):
        commands.append("cc-devthrottle schedule list --fields " + ",".join(SCHEDULE_LIST_FIELDS))
    commands.append("cc-devthrottle schedule list --json")
    commands.append("cc-devthrottle schedule get <id>")
    commands.append("cc-devthrottle schedule runs <id>")
    return commands


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
    # Say WHERE it landed. A scheduled job runs an agent unattended, so "which fleet did
    # that just go to" must be answerable from this output rather than by cross-checking
    # `schedule list` afterwards and recognising somebody else's jobs (issue #2201).
    console.print(f"  Gateway:   {gateway_override or resolve_base_url()}")


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
