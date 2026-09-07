"""Machines: find what is installed on another computer, find a file on it, and start something there.

Backed by the Gateway's /launchers and /machines routes, which reach that machine's cc-launcher. Every
call is scoped to the calling account by the Gateway, so a machine name only ever reaches a machine this
account registered - and since the Remove-the-network-port mission's phase 2 the credential presented is
this SESSION's own key, so it is scoped to one session inside that account as well.

Read the search commands as questions and `launch` as an instruction: `machine apps` and `machine files`
change nothing, while `machine launch` starts a program on a computer you may not be sitting at.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import typer
from rich import box
from rich.console import Console
from rich.table import Table

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

console = Console()


def _fail(message: str) -> None:
    console.print(f"[red]Error:[/red] {message}")
    raise typer.Exit(1)


def _call(path: str) -> Any:
    try:
        payload = gateway.get_json(path)
    except gateway.GatewayError as err:
        _fail(str(err))
    if isinstance(payload, dict) and payload.get("error"):
        _fail(str(payload["error"]))
    return payload


def _size(size: Any) -> str:
    try:
        count = int(size)
    except (TypeError, ValueError):
        return "-"
    if count >= 1_073_741_824:
        return f"{count / 1_073_741_824:.1f}G"
    if count >= 1_048_576:
        return f"{count / 1_048_576:.1f}M"
    if count >= 1024:
        return f"{count / 1024:.0f}K"
    return str(count)


def list_machines(json_output: bool) -> None:
    """Every machine this account can search and start things on."""
    rows: List[Dict[str, Any]] = _call("launchers") or []
    if json_output:
        print(json.dumps(rows, indent=2))
        return

    if not rows:
        console.print(
            "No machines are registered. A machine appears here once cc-launcher is running on it "
            "and has registered with the Gateway."
        )
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("MACHINE", "PORT", "ADDRESS", "VERSION", "LAST SEEN"):
        table.add_column(column)
    for row in rows:
        table.add_row(
            str(gateway.field(row, "machineName", "MachineName") or "-"),
            str(gateway.field(row, "port", "Port") or "-"),
            str(gateway.field(row, "networkAddress", "NetworkAddress") or "(same machine)"),
            str(gateway.field(row, "version", "Version") or "-"),
            str(gateway.field(row, "lastSeenUtc", "LastSeenUtc") or "-"),
        )
    console.print(table)
    console.print(f"{len(rows)} machines")


def list_directors(json_output: bool) -> None:
    """Every Director this account is running, on every machine - and how to name one.

    A machine can appear several times: each named Director instance registers its own row. The NAME
    column is what a person reads; the DIRECTOR ID is what `session spawn --director` should carry,
    because it survives a rename and cannot collide with a second Director called the same thing.
    """
    rows: List[Dict[str, Any]] = _call("directors") or []
    if json_output:
        print(json.dumps(rows, indent=2))
        return

    if not rows:
        console.print(
            "No Directors are registered. A Director appears here once it is running and has "
            "connected to the Gateway."
        )
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("NAME")
    table.add_column("MACHINE")
    # The id is the whole point of this table - it is what you paste into --director - so it WRAPS
    # rather than truncating. An ellipsised GUID looks like a value and is not one: pasting it fails
    # at the far end with "no Director ... is registered", which reads as a fleet problem.
    table.add_column("DIRECTOR ID", overflow="fold", no_wrap=False)
    table.add_column("VERSION")
    for row in rows:
        machine_name = str(gateway.field(row, "machineName", "MachineName") or "-")
        # An unnamed instance falls back to its machine name, exactly as the Director's own toolbar
        # does - the alternative is a blank cell in the column you pick a Director from.
        name = str(gateway.field(row, "displayName", "DisplayName") or "").strip() or machine_name
        table.add_row(
            name,
            machine_name,
            str(gateway.field(row, "directorId", "DirectorId") or "-"),
            str(gateway.field(row, "version", "Version") or "-"),
        )
    console.print(table)
    console.print(f"{len(rows)} Directors")


def list_apps(machine: str, query: Optional[str], limit: int, json_output: bool) -> None:
    """What is installed on one machine."""
    path = f"machines/{machine}/apps?q={query or ''}&limit={limit}"
    payload: Dict[str, Any] = _call(path) or {}
    apps: List[Dict[str, Any]] = payload.get("apps") or payload.get("Apps") or []

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    if not apps:
        console.print(f"Nothing on {machine} matches {query or '(everything)'}.")
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("APPLICATION", "SOURCE", "PATH"):
        table.add_column(column)
    for app in apps:
        table.add_row(
            str(gateway.field(app, "name", "Name") or "-"),
            str(gateway.field(app, "source", "Source") or "-"),
            str(gateway.field(app, "path", "Path") or "-"),
        )
    console.print(table)

    total = payload.get("totalMatches", payload.get("TotalMatches", len(apps)))
    line = f"{len(apps)} of {total} on {machine}"
    if payload.get("truncated") or payload.get("Truncated"):
        line += " - more matched than were returned; narrow the search or raise --count"
    console.print(line)

    # An unreadable directory means the catalogue is short by an unknown amount. Say so: a quietly
    # incomplete list looks exactly like a machine with less installed on it.
    skipped = payload.get("skipped") or payload.get("Skipped") or []
    if skipped:
        console.print(f"[yellow]{len(skipped)} directories could not be read, so this list may be incomplete.[/yellow]")


def search_files(machine: str, query: str, limit: int, timeout_seconds: int, json_output: bool) -> None:
    """Find files by name on one machine."""
    path = (
        f"machines/{machine}/files?q={query}"
        f"&limit={limit}&timeoutMilliseconds={timeout_seconds * 1000}"
    )
    payload: Dict[str, Any] = _call(path) or {}
    files: List[Dict[str, Any]] = payload.get("files") or payload.get("Files") or []

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    if not files:
        console.print(f"No file on {machine} matches {query}.")
    else:
        table = Table(show_header=True, header_style="bold", box=box.ASCII)
        for column in ("FILE", "SIZE", "MODIFIED", "PATH"):
            table.add_column(column)
        for hit in files:
            modified = str(gateway.field(hit, "modifiedUtc", "ModifiedUtc") or "-")
            table.add_row(
                str(gateway.field(hit, "name", "Name") or "-"),
                _size(hit.get("sizeBytes", hit.get("SizeBytes"))),
                modified[:19].replace("T", " "),
                str(gateway.field(hit, "path", "Path") or "-"),
            )
        console.print(table)

    elapsed = payload.get("elapsedMilliseconds", payload.get("ElapsedMilliseconds", 0))
    visited = payload.get("directoriesVisited", payload.get("DirectoriesVisited", 0))
    console.print(f"{len(files)} files - searched {visited} directories on {machine} in {elapsed} ms")

    # The whole point of the truncation fields: a partial answer must never read as a complete one, and the
    # advice differs by reason - a ceiling wants a narrower search, a deadline wants more time.
    if payload.get("truncated") or payload.get("Truncated"):
        reason = payload.get("truncationReason") or payload.get("TruncationReason") or "unknown"
        if reason == "limit":
            console.print(
                "[yellow]Stopped at the result limit - this is NOT the whole answer. "
                "Narrow the search or raise --count.[/yellow]"
            )
        elif reason == "timeout":
            console.print(
                "[yellow]Stopped at the time limit - this is NOT the whole answer. "
                "Narrow the search or raise --seconds.[/yellow]"
            )
        else:
            console.print(f"[yellow]Stopped early ({reason}) - this is NOT the whole answer.[/yellow]")

    unreadable = payload.get("unreadableDirectories", payload.get("UnreadableDirectories", 0))
    if unreadable:
        console.print(f"[yellow]{unreadable} directories could not be read and were not searched.[/yellow]")


def restart_capability(machine: str, json_output: bool) -> None:
    """Can this computer complete a Director restart? Ask BEFORE draining it, not after.

    This changes nothing on the machine. It sends no command, opens no connection and raises no
    signal - which matters, because the only other way to find out whether a launcher is listening
    for the restart signal is to RAISE that signal, and raising it restarts a Director as a side
    effect of the question.

    Why it exists: on 2026-09-06 a Director was drained of seventeen sessions and only then did it
    turn out that no route could restart it. The launcher was two days older than the code that lets
    a launcher be told anything, and it was running, registered and heartbeating the whole time.
    Every liveness check on that machine said yes.

    The Gateway computes the verdict and writes both sentences; this command renders them and does
    not re-derive anything. A verdict this printed itself is the one an agent acts on.
    """
    payload: Dict[str, Any] = _call(f"machines/{machine}/restart-capability") or {}

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    verdict = str(gateway.field(payload, "verdict", "Verdict") or "Unknown")
    reason = str(gateway.field(payload, "reason", "Reason") or "")
    guard = str(gateway.field(payload, "guardedRestart", "GuardedRestart") or "Unknown")
    guard_reason = str(gateway.field(payload, "guardedRestartReason", "GuardedRestartReason") or "")

    # ASCII only, and no colour carrying meaning on its own: this output is read in terminals, piped
    # into logs, and quoted into reports. The word is the answer; the colour only decorates it.
    headline = {
        "CanRestart": ("[green]", "CAN RESTART"),
        "CannotRestart": ("[red]", "CANNOT RESTART"),
    }.get(verdict, ("[yellow]", "UNKNOWN"))
    console.print(f"{headline[0]}{headline[1]}[/] {machine}")
    console.print(f"  {reason}")
    console.print("")

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("FACT", "VALUE"):
        table.add_column(column)
    table.add_row("launcher version", str(gateway.field(payload, "launcherVersion", "LauncherVersion") or "-"))
    table.add_row("reach", str(gateway.field(payload, "reach", "Reach") or "-"))
    table.add_row("declaration", str(gateway.field(payload, "declaration", "Declaration") or "-"))
    table.add_row("restart signal", str(gateway.field(payload, "restartSignal", "RestartSignal") or "-"))
    root_key = gateway.field(payload, "servingRootKey", "ServingRootKey")
    table.add_row("serving root key", str(root_key or "(not declared)"))
    instance_home = gateway.field(payload, "servingRootIsInstanceHome", "ServingRootIsInstanceHome")
    table.add_row(
        "serving an instance home",
        "(not declared)" if instance_home is None else ("YES - this is the fault" if instance_home else "no"),
    )
    declared: List[str] = gateway.field(payload, "declaredCommands", "DeclaredCommands") or []
    table.add_row("declares", ", ".join(str(d) for d in declared) if declared else "(nothing)")
    table.add_row("seconds since heartbeat", str(gateway.field(payload, "quietForSeconds", "QuietForSeconds") or 0))
    console.print(table)

    # The guarded restart is printed as its own line and never folded into the verdict above. A
    # machine can be perfectly restartable and offer no guard - which is what every launcher built
    # before the guard existed looks like - so one sentence cannot carry both answers.
    console.print("")
    console.print(f"Guarded restart (refuse while sessions are live): {guard}")
    console.print(f"  {guard_reason}")


def launch(machine: str, app: Optional[str], path: Optional[str], args: Optional[str],
           cwd: Optional[str], headless: bool, json_output: bool) -> None:
    """Start an application on one machine, by catalogue name or by absolute path."""
    if not app and not path:
        _fail("Name what to start: --app \"Chrome\" or --path \"C:\\\\Tools\\\\thing.exe\".")

    # confirmProtected carries this command's explicit intent through the relay: the Gateway refuses any
    # launch without it (tenant-boundary hardening, CR-5). Typing `machine launch` IS the confirmation -
    # the flag exists to stop programs being started as a side effect of something else.
    body = {"app": app, "path": path, "args": args, "cwd": cwd, "headless": headless,
            "confirmProtected": True}
    try:
        payload = gateway.post_json(f"machines/{machine}/launch", body, timeout=60)
    except gateway.GatewayError as err:
        _fail(str(err))

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    if isinstance(payload, dict) and payload.get("error"):
        _fail(str(payload["error"]))

    console.print(f"Started {app or path} on {machine}.")
