"""Command line for cc-secrets.

Agents call `list`, `run` and `login`. None of them ever prints a secret: every line of output passes
through the process-wide scrubber, and the commands that act with a secret hand back only the result.

The owner calls `add` and `remove` from their own terminal. Those commands refuse to run inside a
DevThrottle session, and `add` takes the secret only from a hidden prompt or piped on standard input -
never as a command-line argument, where it would land in shell history and the process list.
"""

from __future__ import annotations

import getpass
import json
import os
import sys
import warnings
from pathlib import Path
from typing import List, Optional
from urllib.parse import urlsplit

import typer
from rich.console import Console
from rich.table import Table

from . import _console  # noqa: F401  (installs the ASCII-only output patches)
from . import __version__, filelog, paths
from .audit import AuditLog
from .browser_login import CdpError, login as browser_login, OUTCOME_LOGGED_IN
from .redact import SCRUBBER
from .runner import DEFAULT_ENV_NAME, VIA_CHOICES, run_with_secret
from .store import USES, EntryNotAvailableError, SecretStore, make_entry
from .storefile import UserOnlyFile

# Make cc_shared importable when running from source, matching the other cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

app = typer.Typer(
    name="cc-secrets",
    help="Use a stored password without the model ever seeing it. Agents: list, run, login. Owner: add, remove. Anyone: log.",
    add_completion=False,
    no_args_is_help=True,
)
console = Console()

EXIT_FAILED = 1
EXIT_REFUSED = 2


def _say(text: str, err: bool = False) -> None:
    stream = sys.stderr if err else sys.stdout
    stream.write(SCRUBBER.scrub(text) + "\n")
    stream.flush()


def _say_json(payload: object) -> None:
    _say(json.dumps(payload, indent=2))


def _store() -> SecretStore:
    return SecretStore(UserOnlyFile(paths.store_path()))


def _audit() -> AuditLog:
    return AuditLog(paths.audit_path())


def _owner_only(command: str) -> None:
    """Owner commands are refused inside a DevThrottle session: a secret is never entered through one."""
    if os.environ.get("CC_SESSION_ID"):
        _say(f"'cc-secrets {command}' is for the owner, in their own terminal. It does not run inside a "
             "DevThrottle session, because a secret must never be entered through one.", err=True)
        raise typer.Exit(EXIT_REFUSED)


def _stdin_is_tty() -> bool:
    return sys.stdin.isatty()


def _read_secret_from_owner() -> str:
    """The secret from a hidden prompt (typed twice), or piped on standard input."""
    if not _stdin_is_tty():
        piped = sys.stdin.read()
        secret = piped[:-1] if piped.endswith("\n") else piped
        secret = secret[:-1] if secret.endswith("\r") else secret
        if "\n" in secret:
            raise ValueError("The piped secret has more than one line. Pipe exactly one line.")
        return secret
    with warnings.catch_warnings():
        # getpass falls back to a VISIBLE prompt when it cannot hide input; make that an error instead.
        warnings.simplefilter("error", getpass.GetPassWarning)
        first = getpass.getpass("Secret (hidden): ")
        second = getpass.getpass("Secret again: ")
    if first != second:
        raise ValueError("The two entries did not match. Nothing was saved.")
    return first


def _split_list(value: str) -> List[str]:
    return [part.strip() for part in value.split(",") if part.strip()]


@app.command()
def add(
    name: str = typer.Argument(..., help="Entry name, for example devlinux or github-work."),
    username: Optional[str] = typer.Option(None, "--username", help="The user name that goes with the secret."),
    domains: Optional[str] = typer.Option(None, "--domains", help="Comma-separated hosts login may fill, for example example.com,*.example.com."),
    notes: Optional[str] = typer.Option(None, "--notes", help="A note for yourself. Agents see it in list."),
    agents: Optional[bool] = typer.Option(None, "--agents/--no-agents", help="Whether sessions on this machine may use it."),
    uses: Optional[str] = typer.Option(None, "--uses", help="Comma-separated: login, run. Default both."),
    replace: bool = typer.Option(False, "--replace", help="Replace an existing entry without asking."),
):
    """OWNER: add or replace an entry. The secret comes from a hidden prompt, or piped on stdin."""
    _owner_only("add")
    try:
        interactive = _stdin_is_tty()
        if not interactive and (username is None or domains is None or agents is None):
            _say("With the secret piped on stdin there is no prompt for the other fields: pass --username, "
                 "--domains and --agents or --no-agents.", err=True)
            raise typer.Exit(EXIT_FAILED)
        store = _store()
        if store.get(name) is not None and not replace:
            if not interactive or not typer.confirm(f"'{name}' already exists. Replace it?", default=False):
                _say(f"'{name}' already exists. Nothing was changed (use --replace to replace it).", err=True)
                raise typer.Exit(EXIT_FAILED)
        if username is None:
            username = typer.prompt("Username", default="", show_default=False)
        if domains is None:
            domains = typer.prompt("Allowed domains for login (comma-separated, blank for none)", default="", show_default=False)
        if notes is None:
            notes = typer.prompt("Notes", default="", show_default=False) if interactive else ""
        if agents is None:
            agents = typer.confirm("May sessions on this machine use it?", default=False)
        use_list = _split_list(uses) if uses is not None else list(USES)
        secret = _read_secret_from_owner()
        SCRUBBER.add(secret, username)
        entry = make_entry(name, username, secret, _split_list(domains), notes, agents, use_list)
        replaced = store.put(entry)
        _audit().record(name, "add", "ok", "replaced" if replaced else "added")
        _say(f"{'Replaced' if replaced else 'Added'} '{name}' in {store.location}. "
             f"Agents may use it: {'yes' if entry.agents_may_use else 'no'}. Uses: {', '.join(entry.uses)}.")
    except typer.Exit:
        raise
    except Exception as exc:
        filelog.write(f"[cli] add FAILED: {exc}")
        _say(f"add failed: {exc}", err=True)
        raise typer.Exit(EXIT_FAILED)


@app.command()
def remove(
    name: str = typer.Argument(..., help="Entry name."),
    yes: bool = typer.Option(False, "--yes", help="Do not ask for confirmation."),
):
    """OWNER: remove an entry."""
    _owner_only("remove")
    try:
        if not yes and not typer.confirm(f"Remove '{name}'?", default=False):
            raise typer.Exit(EXIT_FAILED)
        if not _store().remove(name):
            _say(f"There is no entry named '{name}'.", err=True)
            raise typer.Exit(EXIT_FAILED)
        _audit().record(name, "remove", "ok")
        _say(f"Removed '{name}'.")
    except typer.Exit:
        raise
    except Exception as exc:
        filelog.write(f"[cli] remove FAILED: {exc}")
        _say(f"remove failed: {exc}", err=True)
        raise typer.Exit(EXIT_FAILED)


@app.command("list")
def list_entries(
    all_entries: bool = typer.Option(False, "--all", help="OWNER: include entries agents may not use."),
    json_output: bool = typer.Option(False, "--json", help="Print JSON."),
):
    """Show the entries agents may use: names, usernames, allowed domains. Never secrets."""
    if all_entries:
        _owner_only("list --all")
    try:
        store = _store()
        entries = sorted(store.entries(), key=lambda e: e.name) if all_entries else store.agent_entries()
        views = [e.public_view() for e in entries]
        if json_output:
            _say_json({"entries": views})
            return
        if not views:
            _say("No secrets are available to agents on this machine." if not all_entries else "The store is empty.")
            return
        table = Table(show_lines=False)
        for column in ("Name", "Username", "Allowed domains", "Uses") + (("Agents",) if all_entries else ()) + ("Notes",):
            table.add_column(column)
        for v in views:
            row = [v["name"], v["username"], ", ".join(v["allowedDomains"]), ", ".join(v["uses"])]
            if all_entries:
                row.append("yes" if v["agentsMayUse"] else "no")
            row.append(v["notes"])
            table.add_row(*[SCRUBBER.scrub(str(c)) for c in row])
        console.print(table)
    except Exception as exc:
        filelog.write(f"[cli] list FAILED: {exc}")
        _say(f"list failed: {exc}", err=True)
        raise typer.Exit(EXIT_FAILED)


@app.command()
def run(
    name: str = typer.Argument(..., help="Entry name."),
    command: Optional[List[str]] = typer.Argument(None, help="The command, after '--'."),
    via: str = typer.Option("stdin", "--via", help=f"How the command receives the secret: {', '.join(VIA_CHOICES)}."),
    env_name: str = typer.Option(DEFAULT_ENV_NAME, "--env-name", help="The variable name for --via env."),
    timeout: float = typer.Option(600, "--timeout", help="Seconds before the command is stopped."),
    json_output: bool = typer.Option(False, "--json", help="Print JSON."),
):
    """Run a command with the secret supplied. Returns its exit code and output, with the secret removed.

    Example: cc-secrets run devlinux -- sudo -S apt-get update
    """
    audit = _audit()
    label = "run " + " ".join(command or [])[:200]
    try:
        entry = _store().entry_for_agent(name, "run")
    except EntryNotAvailableError as exc:
        audit.record(name, label, "refused", str(exc))
        _say(f"refused: {exc}", err=True)
        raise typer.Exit(EXIT_REFUSED)
    try:
        result = run_with_secret(entry, command or [], via, env_name, timeout)
    except Exception as exc:
        filelog.write(f"[cli] run FAILED: {exc}")
        audit.record(name, label, "failed", str(exc))
        _say(f"failed: {exc}", err=True)
        raise typer.Exit(EXIT_FAILED)
    outcome = "ok" if result.exit_code == 0 and not result.timed_out else "failed"
    audit.record(name, label, outcome, f"exit {result.exit_code}" + (", timed out" if result.timed_out else ""))
    if json_output:
        _say_json({"entry": name, "exitCode": result.exit_code, "timedOut": result.timed_out,
                   "stdout": result.stdout, "stderr": result.stderr})
    else:
        if result.stdout:
            sys.stdout.write(SCRUBBER.scrub(result.stdout))
            sys.stdout.flush()
        if result.stderr:
            sys.stderr.write(SCRUBBER.scrub(result.stderr))
            sys.stderr.flush()
        _say(f"[cc-secrets] command exited {result.exit_code}" + (" (timed out)" if result.timed_out else ""), err=True)
    raise typer.Exit(result.exit_code if result.exit_code != 0 else (EXIT_FAILED if result.timed_out else 0))


def _browser_port(target: str) -> int:
    """The debug port of a Director-owned browser on THIS machine, resolved through the Director.

    Only a registered profile can be named - never a raw port or address - so login cannot be pointed at
    something an agent started itself.
    """
    from cc_shared import gateway

    director_id = (os.environ.get("CC_DIRECTOR_ID") or "").strip()
    if not director_id:
        raise RuntimeError("CC_DIRECTOR_ID is not set. login drives a Director-owned browser and only works inside a DevThrottle session.")
    payload = gateway.get_json(f"directors/{director_id}/browsers") or {}
    browsers = payload.get("browsers", payload.get("Browsers", [])) or []
    key = target.strip().lower()
    match = next((b for b in browsers if gateway.field(b, "id", "Id").lower() == key), None) \
        or next((b for b in browsers if gateway.field(b, "name", "Name").lower() == key), None)
    if match is None:
        names = ", ".join(gateway.field(b, "id", "Id") for b in browsers) or "none"
        raise RuntimeError(f"No Director-owned browser named '{target}' on this machine (browsers: {names}).")
    address = urlsplit(gateway.field(match, "buCdpUrl", "BuCdpUrl"))
    if address.hostname not in ("127.0.0.1", "localhost") or not address.port:
        raise RuntimeError(f"Browser '{target}' does not have a loopback debug address.")
    return address.port


@app.command()
def login(
    name: str = typer.Argument(..., help="Entry name."),
    browser: str = typer.Option(..., "--browser", help="The Director-owned browser profile (see cc-devthrottle browser list)."),
    timeout: float = typer.Option(30, "--timeout", help="Seconds to wait for the login to complete."),
    json_output: bool = typer.Option(False, "--json", help="Print JSON."),
):
    """Fill and submit the login form on the open tab of the entry's site. Refuses any other domain.

    Open the login page in that browser first (cc-devthrottle browser start, then browser-harness).
    """
    audit = _audit()
    label = f"login --browser {browser}"
    try:
        entry = _store().entry_for_agent(name, "login")
    except EntryNotAvailableError as exc:
        audit.record(name, label, "refused", str(exc))
        _say(f"refused: {exc}", err=True)
        raise typer.Exit(EXIT_REFUSED)
    try:
        result = browser_login(entry, _browser_port(browser), timeout)
    except (CdpError, RuntimeError, OSError) as exc:
        filelog.write(f"[cli] login FAILED: {exc}")
        audit.record(name, label, "failed", str(exc))
        _say(f"failed: {exc}", err=True)
        raise typer.Exit(EXIT_FAILED)
    outcome = {"logged in": "ok", "refused": "refused"}.get(result.outcome, "failed")
    audit.record(name, label, outcome, f"{result.outcome}: {result.reason} host={result.host}")
    if json_output:
        _say_json({"entry": name, "outcome": result.outcome, "reason": result.reason, "host": result.host})
    elif result.outcome == OUTCOME_LOGGED_IN:
        _say(f"logged in ({result.host})")
    else:
        _say(f"{result.outcome}: {result.reason}", err=True)
    if result.outcome != OUTCOME_LOGGED_IN:
        raise typer.Exit(EXIT_REFUSED if result.outcome == "refused" else EXIT_FAILED)


@app.command("log")
def show_log(
    count: int = typer.Option(50, "--count", "-n", help="How many of the most recent lines."),
    json_output: bool = typer.Option(False, "--json", help="Print JSON."),
):
    """Show the audit log: time, entry, session, command, outcome. Never a secret."""
    try:
        lines = _audit().read(count)
        if json_output:
            _say_json({"lines": lines})
            return
        if not lines:
            _say("The audit log is empty.")
            return
        table = Table(show_lines=False)
        for column in ("Time", "Entry", "Session", "Command", "Outcome", "Detail"):
            table.add_column(column)
        for l in lines:
            table.add_row(*[SCRUBBER.scrub(str(l.get(k, ""))) for k in ("time", "entry", "session", "command", "outcome", "detail")])
        console.print(table)
    except Exception as exc:
        filelog.write(f"[cli] log FAILED: {exc}")
        _say(f"log failed: {exc}", err=True)
        raise typer.Exit(EXIT_FAILED)


@app.command()
def version():
    """Print the version."""
    _say(__version__)
