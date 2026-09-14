"""Mission operations for cc-devthrottle.

A Mission is a first-class persisted record that a pod of sessions is collectively chartered to
accomplish (see docs/new_architecture/fleet.html). Missions are a
FLEET-level concept - they span Directors and machines and nest - so their source of truth lives at
the GATEWAY, like fleet messaging and scheduling, not on any one Director (Gateway Cleanup mission,
Wave 4b). These commands create and list Mission records via the Gateway Control API
(POST /missions, GET /missions), mirroring the schedule command style.

ATTACH AND DETACH (issue #2387) go a different way, and deliberately: through this machine's own
Gateway, at POST /sessions/{sid}/mission. A session lives on a Director, so attaching one is a session write
and follows the same route every other session verb takes - local target attached directly, remote
target relayed by the Gateway to the owning Director over the tunnel. Going straight to the Gateway
from here would work only for sessions on other machines, which is the wrong half.
"""

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

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

console = Console()
err_console = Console(stderr=True)

TIMEOUT_SECONDS = 10


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


def resolve_base_url() -> str:
    """The Gateway this SESSION was told to call.

    Remove-the-network-port mission, phase 2. This used to read gateway.url out of config.json and fall
    back to a loopback address, which meant the command line kept its own opinion about where the
    Gateway is - one that could be right on the machine and wrong for the session. The session is TOLD
    the address at launch, beside the credential that goes with it, and one source for both is what
    makes them impossible to mismatch.
    """
    return gateway.gateway_base_url()


def _auth_token() -> str:
    """This session's own Gateway key.

    IT USED TO BE THE ACCOUNT'S. This function read `gateway.token` from config.json - the shared
    machine credential, which has authority over the whole account on every machine - and presented it
    straight to the Gateway. So every agent that ran one of these commands held the run of the account,
    which is precisely the hole Phase 1b was chartered to prevent, already open on this path. The
    session key closes it: bound to one session, one tenant, and the fleet's agent routes only.
    """
    return gateway.session_key()


class MissionClient:
    """Talks to one Gateway's mission surface (POST/GET /missions)."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or resolve_base_url()).rstrip("/")
        self._token = _auth_token()
        # A session key is REQUIRED, and _auth_token raises with the remedy when there is none.
        # The old exemption - "a loopback Gateway on this machine needs no token" - is deliberately
        # gone: the credential identifies WHICH SESSION is calling, and that is as necessary on this
        # machine as on any other. It was the address, never the caller, that made loopback special.

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

    def _ok_or_raise(self, resp: requests.Response) -> Any:
        if 200 <= resp.status_code < 300:
            # The shared guard, not a bare resp.json(): a request no endpoint matches falls
            # through to the Gateway's web app and answers HTTP 200 with text/html (issue #2486).
            return gateway.parse_json_body(resp, self.base_url)
        raise GatewayError(self._gateway_message(resp))

    def create(self, name: str) -> Dict[str, Any]:
        # Missions are FLAT. The parent link was removed on 2026-08-07 after never being used once;
        # see docs/new_architecture/fleet.html.
        return self._ok_or_raise(self._request("POST", "/missions", {"missionName": name}))

    def list_all(self, state: Optional[str] = None) -> List[Dict[str, Any]]:
        """Missions from the Gateway. ACTIVE ONLY unless `state` asks otherwise ("all" for every state).

        The default matches the Gateway's: "what am I working on" is the question nearly every caller
        is asking, and a list padded with finished work is the wrong answer to it.
        """
        path = "/missions" if not state else f"/missions?state={state}"
        data = self._ok_or_raise(self._request("GET", path))
        return list(data) if isinstance(data, list) else []

    def patch(self, mission_id: str, body: Dict[str, Any]) -> Dict[str, Any]:
        """Change a mission: its why, its name, or its state. Returns MissionPatchResultDto."""
        resp = self._ok_or_raise(self._request("PATCH", f"/missions/{mission_id}", body))
        return resp if isinstance(resp, dict) else {}


def _resolve_mission(query: str) -> Dict[str, Any]:
    """Resolve a Mission by full id, id prefix, or a case-insensitive name match.

    'mission list' prints SHORT ids, so requiring the full identifier would mean copying it out of a
    JSON dump every time. Only the typing is relaxed: the id that finally reaches the Gateway is the
    full one from the caller's OWN mission list, and the Gateway resolves it inside the caller's own
    tenant regardless of what was typed here.
    """
    # EVERY state, not just active. A mission that has been completed or removed still has to be
    # addressable - reopening one, or correcting its name, is exactly when you reach for it, and
    # resolving against the default active-only list would answer "no mission matches" for a record
    # that is plainly there.
    try:
        missions = MissionClient().list_all(state="all")
    except GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    wanted = query.strip()
    lowered = wanted.lower()
    exact = [m for m in missions if (_field(m, "missionId", "MissionId") or "").lower() == lowered]
    if exact:
        return exact[0]

    matches = [
        m for m in missions
        if (_field(m, "missionId", "MissionId") or "").lower().startswith(lowered)
        or lowered in (_field(m, "missionName", "MissionName") or "").lower()
    ]
    if not matches:
        console.print(
            f"[red]No mission matches '{wanted}'.[/red] "
            "Run cc-devthrottle mission list to see the missions on the Gateway."
        )
        raise typer.Exit(1)
    if len(matches) > 1:
        console.print(f"[yellow]'{wanted}' is ambiguous - {len(matches)} missions match:[/yellow]")
        for m in matches:
            mid = _field(m, "missionId", "MissionId") or "-"
            console.print(f"  {_short_id(mid)}  {_field(m, 'missionName', 'MissionName') or '-'}")
        console.print("Re-run with a longer id prefix or the exact name.")
        raise typer.Exit(1)
    return matches[0]


def _field(record: Dict[str, Any], *names: str) -> Optional[str]:
    """First present, non-empty value among the given key spellings (camel/Pascal case)."""
    for name in names:
        value = record.get(name)
        if value:
            return str(value)
    return None


def _short_id(value: Optional[str]) -> str:
    if not value:
        return "-"
    return value.split("-")[0] if "-" in value else value


def create_mission(name: str) -> None:
    """Create a Mission record on the Gateway and print its id."""
    try:
        resp = MissionClient().create(name)
    except GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    mid = _field(resp, "missionId", "MissionId")
    if not mid:
        console.print("[red]Error:[/red] the Gateway did not return a mission id.")
        raise typer.Exit(1)

    label = _field(resp, "missionName", "MissionName") or name
    console.print(f"[green]Created[/green] mission ({label}).")
    console.print(f"id: {mid}")
    console.print(
        f'Attach a session at spawn:  cc-devthrottle session spawn <repo> --mission {mid}'
    )


def list_missions(json_output: bool, state: Optional[str] = None) -> None:
    """List the Missions on the Gateway - active ones unless `state` asks for otherwise."""
    try:
        missions = MissionClient().list_all(state=state)
    except GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if json_output:
        print(json.dumps(missions, indent=2))
        return

    if not missions:
        if state and state != "active":
            # Say WHICH list is empty. "No missions" under a filter would read as "you have none at
            # all", which is a different and much more alarming statement.
            console.print(f"No missions with state '{state}'.")
        else:
            console.print(
                "No active missions on the Gateway. "
                "Create one with 'cc-devthrottle mission create <name>', "
                "or see finished ones with 'cc-devthrottle mission list --all'."
            )
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("Id")
    table.add_column("Name")
    table.add_column("State")
    table.add_column("Why")

    for mission in missions:
        mid = _field(mission, "missionId", "MissionId")
        name = _field(mission, "missionName", "MissionName") or "-"
        mstate = _field(mission, "state", "State") or "active"
        why = _field(mission, "why", "Why") or ""
        # A mission with no WHY is FLAGGED, not blank - the same rule the Cockpit card follows. A
        # mission whose reason nobody wrote down is the thing worth noticing in this list.
        why_cell = why if why else "[yellow]no why set[/yellow]"
        table.add_row(_short_id(mid) if mid else "-", name, mstate, why_cell)
    console.print(table)


def _patch_mission(mission_query: str, body: Dict[str, Any], command_name: str) -> Dict[str, Any]:
    """Resolve a mission, apply a patch, and surface anything the Gateway had to say about it."""
    mission = _resolve_mission(mission_query)
    mission_id = _field(mission, "missionId", "MissionId")
    if not mission_id:
        console.print("[red]Error:[/red] the Gateway returned a mission with no id.")
        raise typer.Exit(1)

    try:
        resp = MissionClient().patch(mission_id, body)
    except GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    resp["_before"] = mission
    return resp


def _patched_mission(resp: Dict[str, Any]) -> Dict[str, Any]:
    inner = resp.get("mission", resp.get("Mission"))
    return inner if isinstance(inner, dict) else {}


def _print_patch_note(resp: Dict[str, Any]) -> None:
    """Print the Gateway's sentence about anything that happened alongside the change.

    Passed through verbatim, exactly like the attach seat note: what happened to the workflow run is
    decided at the Gateway, and a client that re-worded it would be writing its own account of a
    decision it did not make.
    """
    note = _field(resp, "note", "Note")
    if note:
        console.print(f"[yellow]Note:[/yellow] {note}")


def rename_mission(mission_query: str, new_name: str) -> None:
    """Rename a Mission. Its id does not change, so nothing attached to it moves."""
    if not new_name.strip():
        console.print("[red]Error:[/red] a mission name cannot be blank.")
        raise typer.Exit(1)

    resp = _patch_mission(mission_query, {"missionName": new_name}, "rename")
    before = _field(resp.get("_before", {}), "missionName", "MissionName") or "(unnamed)"
    after = _field(_patched_mission(resp), "missionName", "MissionName") or new_name.strip()

    # NAME THE OLD NAME. A rename that reports only the new one hides which mission moved, which is
    # the same failure the attach command avoids by naming the mission a session LEFT.
    console.print(f'[green]Renamed[/green] "{before}" to "{after}".')
    console.print("Its id is unchanged, so every attached session stays attached.")
    _print_patch_note(resp)


def end_mission(mission_query: str, state: str) -> None:
    """Complete or remove a Mission (both are endings; both are reversible)."""
    resp = _patch_mission(mission_query, {"state": state}, state)
    mission = _patched_mission(resp)
    name = _field(mission, "missionName", "MissionName") or "(unnamed)"
    mid = _field(mission, "missionId", "MissionId") or ""

    verb = "Completed" if state == "complete" else "Removed"
    console.print(f"[green]{verb}[/green] mission {name} ({_short_id(mid)}).")
    _print_patch_note(resp)

    # SAY WHERE IT WENT, and how to get it back. An ending that reports only success leaves the owner
    # unable to find the record afterwards - and the Cockpit has no archive view yet, so the command
    # line is currently the ONLY way to see it again.
    console.print(
        f"It is out of the default list. See it with 'cc-devthrottle mission list --state {state}', "
        f"or bring it back with 'cc-devthrottle mission reopen {_short_id(mid)}'."
    )


def reopen_mission(mission_query: str) -> None:
    """Return an ended Mission to active - the way back from a mistaken ending."""
    resp = _patch_mission(mission_query, {"state": "active"}, "reopen")
    mission = _patched_mission(resp)
    name = _field(mission, "missionName", "MissionName") or "(unnamed)"
    console.print(f"[green]Reopened[/green] mission {name}. It is back in the default list.")
    _print_patch_note(resp)


# ===== Attach and detach (issue #2387) =====================================================
#
# THE RULES, settled here and written up in
# docs/new_architecture/fleet.html. Each one had to be decided because
# somebody will hit it, and an implied answer is one that gets re-litigated at the worst moment:
#
#  * Attaching is a MOVE, not a one-way door. A session that already carries a mission is
#    re-pointed by the same command, and the command says which mission it LEFT. The shape of a
#    mission is discovered as it runs, so the first classification is always a guess; a one-way
#    attach makes every wrong guess permanent until the session is killed.
#  * Detaching is supported. No mission is the ORDINARY state of a session, so returning to it must
#    not require inventing a mission to park the session in.
#  * Attaching a controlling session does NOT drag its children along by default. A controller
#    routinely commissions sessions for unrelated work - a reviewer for one pull request, an
#    investigation seat for something else - and a silent bulk re-parent cannot be undone in one
#    step. --with-children asks for it explicitly, walks the controlling relationship all the way
#    down, and NAMES every session it moves rather than reporting a count.


def _controlled_subtree(sessions: List[Dict[str, Any]], root_id: str) -> List[Dict[str, Any]]:
    """Every session controlled by root_id, transitively (children, their children, and so on).

    Transitive rather than one level: the shape this exists for is Architect -> Manager -> Workers,
    where stopping at the first level would attach the Manager and leave the Workers behind - which
    reads as "it worked" while producing exactly the split view the whole feature is meant to end.
    """
    by_controller: Dict[str, List[Dict[str, Any]]] = {}
    for s in sessions:
        controller = (_field(s, "controllerSessionId", "ControllerSessionId") or "").lower()
        if controller:
            by_controller.setdefault(controller, []).append(s)

    found: List[Dict[str, Any]] = []
    seen = {root_id.lower()}
    frontier = [root_id.lower()]
    while frontier:
        current = frontier.pop()
        for child in by_controller.get(current, []):
            child_id = (_field(child, "sessionId", "SessionId") or "").lower()
            # A cycle cannot happen through legitimate spawns, but a corrupted roster must not hang
            # the command line, so a session is only ever visited once.
            if not child_id or child_id in seen:
                continue
            seen.add(child_id)
            found.append(child)
            frontier.append(child_id)
    return found


def _apply_mission(session_id: str, mission_id: Optional[str]) -> Dict[str, Any]:
    """Attach (or detach, on a null mission id) one session through the Gateway.

    The answer is FLATTENED - the workflow seat's id and version arrive on the returned session row,
    and the display helpers below read them beside seatMoved and seatNote. This re-keys fields for
    display only; every judgement in the answer (whether the seat moved, and the sentence explaining
    it) is made at the Gateway and passed through untouched.
    """
    body: Dict[str, Any] = {}
    if mission_id:
        body["missionId"] = mission_id
    resp = gateway.post_json(f"sessions/{session_id}/mission", body)
    if not isinstance(resp, dict):
        return {}
    session = resp.get("session", resp.get("Session"))
    if isinstance(session, dict):
        for key in ("workflowId", "WorkflowId", "workflowVersion", "WorkflowVersion"):
            if key in session and key not in resp:
                resp[key] = session[key]
    return resp


def _previous_mission(
    resp: Dict[str, Any], roster_row: Dict[str, Any]
) -> tuple[Optional[str], Optional[str]]:
    """The mission a session was on BEFORE the call: (id, name), or (None, None).

    Two sources, in order of authority. A LOCAL target's Director read the attachment off the live
    session immediately before changing it, so its answer is exact and is used whenever it is there.
    A REMOTE target is relayed through the Gateway to a Director that this machine never talked to
    about that session, so nothing on the return path knows what it left - and the roster row the
    caller was just resolved against does. That row is a snapshot rather than a live read, which is
    the honest limit of what the second source can claim.

    This is a display line, not a decision: it names what the session left so a move is visible.
    Nothing branches on it, so the weaker source costs accuracy in the wording and nothing else.
    """
    exact_id = _field(resp, "previousMissionId", "PreviousMissionId")
    if exact_id:
        return exact_id, _field(resp, "previousMissionName", "PreviousMissionName")
    return (
        _field(roster_row, "missionId", "MissionId"),
        _field(roster_row, "missionName", "MissionName"),
    )


def _seat_moved(resp: Dict[str, Any]) -> bool:
    """True when the call also moved (or cleared) the session's workflow seat."""
    value = resp.get("seatMoved", resp.get("SeatMoved"))
    return bool(value)


def _conduct_command(resp: Dict[str, Any]) -> Optional[str]:
    """The exact command that re-reads the conduct the session is now seated under, or None.

    Named in full rather than left as placeholders. A instruction with blanks in it makes the human go
    and find two values somewhere else, at the one moment they have been told something is out of step -
    which is how a warning gets skipped.
    """
    workflow = _field(resp, "workflowId", "WorkflowId")
    version = resp.get("workflowVersion", resp.get("WorkflowVersion"))
    if not workflow or version is None:
        return None
    return f"cc-devthrottle workflow instructions {workflow} --version {version}"


def _print_seat_note(resp: Dict[str, Any]) -> None:
    """Print the Director's sentence about the seat, when it had one to add.

    Passed through verbatim rather than re-worded here. What happened to the seat is decided at the
    Gateway; a client that paraphrased it would be writing its own account of a decision it did not
    make, and that is how a surface starts saying something plausible instead of something true.
    """
    note = _field(resp, "seatNote", "SeatNote")
    if note:
        console.print(f"[yellow]Note:[/yellow] {note}")


def _session_label(session: Dict[str, Any]) -> str:
    sid = _field(session, "sessionId", "SessionId") or ""
    name = _field(session, "name", "Name") or "(unnamed)"
    return f"{name} ({gateway.short_id(sid)})"


def attach_session(target: str, mission_query: str, with_children: bool) -> None:
    """Attach an EXISTING session (and optionally everything it controls) to a Mission."""
    from . import session_ops

    mission = _resolve_mission(mission_query)
    mission_id = _field(mission, "missionId", "MissionId")
    mission_name = _field(mission, "missionName", "MissionName") or "(unnamed)"
    if not mission_id:
        console.print("[red]Error:[/red] the Gateway returned a mission with no id.")
        raise typer.Exit(1)

    chosen = session_ops.resolve_session(target, command_name="cc-devthrottle mission attach")
    session_id = _field(chosen, "sessionId", "SessionId")

    targets = [chosen]
    if with_children:
        sessions, _, _, _ = session_ops.fleet_or_exit()
        targets.extend(_controlled_subtree(sessions, session_id))
        console.print(
            f"Attaching {len(targets)} session(s) to mission [bold]{mission_name}[/bold] "
            f"({_short_id(mission_id)}):"
        )
        for s in targets:
            console.print(f"  {_session_label(s)}")

    failed = 0
    seat_moved_any = False
    # The command that re-reads the conduct the sessions are NOW under. Filled from the first move that
    # reports a workflow and version; the placeholder stands only when the destination seats nobody, which
    # is the case where there is no conduct to re-read anyway.
    seat_conduct = "cc-devthrottle workflow instructions <workflow> --version <version>"
    for s in targets:
        sid = _field(s, "sessionId", "SessionId")
        try:
            resp = _apply_mission(sid, mission_id)
        except gateway.GatewayError as err:
            # Keep going. A partial attach is honest and repeatable; abandoning the rest of the tree
            # because one session's Director is unreachable would leave the pod split with no record
            # of where it stopped.
            console.print(f"[red]Failed:[/red] {_session_label(s)} - {err}")
            failed += 1
            continue

        previous_id, previous = _previous_mission(resp, s)
        moved_from = ""
        if previous_id and previous_id.lower() != mission_id.lower():
            moved_from = f" (moved from {previous or _short_id(previous_id)})"
        console.print(
            f"[green]Attached[/green] {_session_label(s)} to {mission_name}{moved_from}."
        )
        if _seat_moved(resp):
            seat_moved_any = True
            seat_conduct = _conduct_command(resp) or seat_conduct
        _print_seat_note(resp)

    if seat_moved_any:
        # THE HONEST LIMIT, and it has to be said every time the seat moves. A mission is also a run of
        # the mission workflow, and the seat pins the conduct the agent follows - moving it corrects the
        # RECORD (what the fleet shows, what governs the session, who the run lists) but it cannot reach
        # back into a running agent's context and replace the conduct it was handed at birth. Only telling
        # the session does that. Saying nothing here would leave a human believing a move was complete
        # when the agent is still working to the old rules.
        console.print(
            "[yellow]Note:[/yellow] the workflow seat moved with the mission, but a session that is "
            "already running still holds the conduct it was given at birth. Tell it to fetch its "
            f"conduct again: {seat_conduct}"
        )

    if failed:
        raise typer.Exit(1)


def detach_session(target: str) -> None:
    """Detach a session from whatever Mission it is attached to."""
    from . import session_ops

    chosen = session_ops.resolve_session(target, command_name="cc-devthrottle mission detach")
    session_id = _field(chosen, "sessionId", "SessionId")

    try:
        resp = _apply_mission(session_id, None)
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    previous_id, previous = _previous_mission(resp, chosen)
    if not previous_id:
        # Say what is true rather than claiming a change: the session was already attached to nothing.
        console.print(f"{_session_label(chosen)} was not attached to a mission; nothing changed.")
        _print_seat_note(resp)
        return
    console.print(
        f"[green]Detached[/green] {_session_label(chosen)} from {previous or _short_id(previous_id)}."
    )
    if _seat_moved(resp):
        # Detach clears the mission's seat with it. A session that has LEFT a mission cannot still be
        # governed by that mission's workflow run, and cannot still sit in its participant list as active
        # - so the seat goes too, and the human is told, because the session it names is now running
        # under no workflow conduct at all.
        console.print(
            "Its workflow seat was cleared with the mission: it is no longer governed by that "
            "mission's workflow run."
        )
    _print_seat_note(resp)
