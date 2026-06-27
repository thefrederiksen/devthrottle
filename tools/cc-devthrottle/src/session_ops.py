"""Fleet/session operations for cc-devthrottle."""

from __future__ import annotations

import json
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import typer
from rich.console import Console
from rich.table import Table

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass

console = Console()
SELFTEST_MARKER = "FLEETPONG"


def _repo_name(repo: str) -> str:
    return repo.replace("\\", "/").rstrip("/").split("/")[-1] if repo else "-"


def _get_sessions() -> List[Dict[str, Any]]:
    try:
        sessions = director.get_json("fleet/sessions") or []
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)
    return sessions


def _resolve_target(target: str, *, command_name: str) -> Dict[str, Any]:
    sessions = _get_sessions()
    matches = director.resolve_target(sessions, target)
    if not matches:
        console.print(
            f"[red]No session matches '{target}'.[/red] "
            "Run cc-devthrottle session list to see the fleet."
        )
        raise typer.Exit(1)
    if len(matches) > 1:
        console.print(f"[yellow]'{target}' is ambiguous - {len(matches)} matches:[/yellow]")
        for s in matches:
            sid = director.field(s, "sessionId", "SessionId")
            name = director.field(s, "name", "Name") or "(unnamed)"
            machine = director.field(s, "machineName", "MachineName") or "-"
            console.print(f"  {director.short_id(sid)}  {name}  ({machine})")
        console.print(f"Re-run {command_name} with a longer id prefix.")
        raise typer.Exit(1)
    return matches[0]


def resolve_target_or_current(target: Optional[str]) -> str:
    """Return the requested session id, defaulting to this session."""
    if target is None or not target.strip():
        sid = director.session_id()
        if not sid:
            console.print(
                "[red]Error:[/red] no target was provided and CC_SESSION_ID is not set."
            )
            raise typer.Exit(1)
        return sid

    chosen = _resolve_target(target, command_name="cc-devthrottle session rename")
    return director.field(chosen, "sessionId", "SessionId")


def list_sessions(json_output: bool) -> None:
    """List every session running across the fleet."""
    sessions = _get_sessions()

    if json_output:
        console.print(json.dumps(sessions, indent=2))
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
        status = director.field(s, "activityState", "ActivityState") or "-"
        marker = " (you)" if me and sid.lower() == me.lower() else ""
        table.add_row(director.short_id(sid) + marker, name, machine, _repo_name(repo), status)

    console.print(table)


def whoami() -> None:
    """Show this session's own fleet identity."""
    sid = director.session_id()
    if not sid:
        console.print(
            "[red]Error:[/red] CC_SESSION_ID is not set. "
            "cc-devthrottle session whoami only works inside a DevThrottle session."
        )
        raise typer.Exit(1)

    short = director.short_id(sid)
    sessions = _get_sessions()
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
        console.print(f'You are session {short} ("{name}") on {machine}, repo {_repo_name(repo)}.')

    console.print('To message another session:  cc-devthrottle message send <id> "<message>"')
    console.print('To message everyone:         cc-devthrottle message send all "<message>"')
    console.print("To see all sessions:         cc-devthrottle session list")


def rename_session(target: Optional[str], new_name: str) -> Dict[str, Any]:
    """Rename a target session, defaulting to the current session."""
    name = new_name.strip()
    if not name:
        console.print("[red]Error:[/red] the new session name cannot be blank.")
        raise typer.Exit(1)

    sid = resolve_target_or_current(target)
    try:
        resp = director.patch_json(f"sessions/{sid}", {"name": name})
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if not isinstance(resp, dict):
        console.print("[red]Error:[/red] the Director did not return the renamed session.")
        raise typer.Exit(1)

    actual = director.field(resp, "name", "Name") or name
    actual_sid = director.field(resp, "sessionId", "SessionId") or sid
    console.print(f'[green]Renamed[/green] {director.short_id(actual_sid)} to "{actual}".')
    return resp


def _report_delivery(resp: Any, who: str) -> None:
    accepted = False
    count = 0
    err: Optional[str] = None
    if isinstance(resp, dict):
        accepted = bool(resp.get("accepted", resp.get("Accepted", False)))
        count = int(resp.get("deliveredCount", resp.get("DeliveredCount", 0)) or 0)
        err = resp.get("error") or resp.get("Error")
    if accepted:
        console.print(f"[green]Delivered[/green] to {who} ({count} session(s)).")
    else:
        console.print(f"[red]Not delivered:[/red] {err or 'unknown error'}")
        raise typer.Exit(1)


def send_message(target: str, message: str) -> None:
    """Send a message to one session, or broadcast with target 'all'."""
    me = director.session_id()

    if target.strip().lower() == "all":
        try:
            resp = director.post_json("fleet/broadcast", {"text": message, "fromSessionId": me})
        except director.DirectorError as err:
            console.print(f"[red]Error:[/red] {err}")
            raise typer.Exit(1)
        _report_delivery(resp, "everyone")
        return

    chosen = _resolve_target(target, command_name="cc-devthrottle message send")
    target_sid = director.field(chosen, "sessionId", "SessionId")
    try:
        resp = director.post_json(
            "fleet/send", {"toSessionId": target_sid, "text": message, "fromSessionId": me}
        )
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    name = director.field(chosen, "name", "Name") or director.short_id(target_sid)
    _report_delivery(resp, f'{name} ({director.short_id(target_sid)})')


def ask_session(target: str, question: str, timeout_ms: int) -> None:
    """Ask one session a question and print its answer."""
    if target.strip().lower() == "all":
        console.print(
            "[red]message ask targets a single session.[/red] "
            "Use cc-devthrottle message send all for a broadcast."
        )
        raise typer.Exit(1)

    me = director.session_id()
    chosen = _resolve_target(target, command_name="cc-devthrottle message ask")
    target_sid = director.field(chosen, "sessionId", "SessionId")

    http_timeout = max(30.0, timeout_ms / 1000.0 + 15.0)
    try:
        resp = director.post_json(
            "fleet/ask",
            {
                "toSessionId": target_sid,
                "question": question,
                "fromSessionId": me,
                "timeoutMs": timeout_ms,
            },
            timeout=http_timeout,
        )
    except director.DirectorError as err:
        console.print(f"[red]{err}[/red]")
        raise typer.Exit(1)

    answer = (director.field(resp, "answer", "Answer") if isinstance(resp, dict) else "").strip()
    name = director.field(chosen, "name", "Name") or director.short_id(target_sid)
    console.print(f"[dim]-- answer from {name} ({director.short_id(target_sid)}) --[/dim]")
    console.print(answer if answer else "(the target produced no output)")


def spawn_session(
    repo: str,
    agent: str,
    prompt: Optional[str],
    name: Optional[str],
    session_type: Optional[str],
    command: Optional[str],
    command_args: Optional[str],
) -> None:
    """Open a new session on the local Director."""
    body: Dict[str, Any] = {"repoPath": repo, "agent": agent}
    if prompt:
        body["prePrompt"] = prompt
    if session_type:
        body["type"] = session_type
    if command:
        body["command"] = command
    if command_args:
        body["commandArgs"] = command_args

    try:
        resp = director.post_json("sessions", body)
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    sid = director.field(resp, "sessionId", "SessionId")
    if not sid:
        console.print("[red]Error:[/red] the Director did not return a session id.")
        raise typer.Exit(1)

    if name:
        try:
            director.patch_json(f"sessions/{sid}", {"name": name})
        except director.DirectorError as err:
            console.print(f"[yellow]Session created, but rename failed:[/yellow] {err}")

    short = director.short_id(sid)
    label = name or director.field(resp, "name", "Name") or short
    console.print(f"[green]Opened[/green] session {short} ({label}).")
    console.print(f"id: {sid}")
    console.print(
        f'Message it:  cc-devthrottle message send {short} "<message>"'
        f'   |   Ask it:  cc-devthrottle message ask {short} "<question>"'
    )


def _spawn_selftest(repo: str, command_args: str, name: str) -> str:
    resp = director.post_json(
        "sessions",
        {"repoPath": repo, "agent": "RawCli", "command": "cmd", "commandArgs": command_args},
    )
    sid = director.field(resp, "sessionId", "SessionId")
    if not sid:
        raise director.DirectorError("the Director did not return a session id when spawning.")
    try:
        director.patch_json(f"sessions/{sid}", {"name": name})
    except director.DirectorError:
        pass
    return sid


def _fleet_ids() -> List[str]:
    sessions = director.get_json("fleet/sessions") or []
    return [director.field(s, "sessionId", "SessionId") for s in sessions]


def selftest(timeout_ms: int) -> None:
    """Run the fleet messaging self-test against the local Director."""
    repo = tempfile.gettempdir()
    results: List[Tuple[str, bool, str]] = []
    responder: Optional[str] = None
    recipient: Optional[str] = None

    def record(step: str, ok: bool, detail: str = "") -> None:
        results.append((step, ok, detail))
        mark = "[green]PASS[/green]" if ok else "[red]FAIL[/red]"
        console.print(f"  {mark}  {step}{('  - ' + detail) if detail else ''}")

    try:
        responder = _spawn_selftest(repo, f"/k prompt {SELFTEST_MARKER}$G", "selftest-responder")
        recipient = _spawn_selftest(repo, "/k", "selftest-recipient")
        record(
            "spawn two sessions",
            True,
            f"responder={director.short_id(responder)} recipient={director.short_id(recipient)}",
        )
        time.sleep(2)

        ids = _fleet_ids()
        listed = responder in ids and recipient in ids
        record("session list includes both", listed)

        send = director.post_json(
            "fleet/send",
            {"toSessionId": recipient, "text": "fleet self-test message", "fromSessionId": responder},
        )
        accepted = bool(isinstance(send, dict) and send.get("accepted", send.get("Accepted", False)))
        record("message send delivers", accepted, str(director.field(send, "error", "Error") or ""))

        ask = director.post_json(
            "fleet/ask",
            {
                "toSessionId": responder,
                "question": "selftest ping",
                "fromSessionId": recipient,
                "timeoutMs": timeout_ms,
            },
            timeout=timeout_ms / 1000.0 + 15.0,
        )
        answer = director.field(ask, "answer", "Answer") if isinstance(ask, dict) else ""
        got_marker = SELFTEST_MARKER in answer
        record(
            "message ask returns the answer",
            got_marker,
            "marker found" if got_marker else f"status={director.field(ask, 'status', 'Status')}",
        )

    except director.DirectorError as err:
        record("fleet messaging reachable", False, str(err))
    finally:
        for sid in (responder, recipient):
            if sid:
                try:
                    director.delete(f"sessions/{sid}")
                except director.DirectorError:
                    pass
        try:
            time.sleep(1)
            remaining = _fleet_ids()
            leaked = [s for s in (responder, recipient) if s and s in remaining]
            record("throwaway sessions cleaned up", not leaked, "" if not leaked else f"leaked {len(leaked)}")
        except director.DirectorError as err:
            record("throwaway sessions cleaned up", False, str(err))

    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    if passed == total and total > 0:
        console.print(f"[green]PASS[/green] - fleet messaging self-test: {passed}/{total} checks passed.")
        raise typer.Exit(0)
    console.print(f"[red]FAIL[/red] - fleet messaging self-test: {passed}/{total} checks passed.")
    raise typer.Exit(1)
