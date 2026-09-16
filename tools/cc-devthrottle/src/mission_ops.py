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
from rich.console import Console

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

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
        # Absent is not empty: an answer that is not a list of missions must never read as "no missions".
        if not isinstance(data, list):
            raise GatewayError(
                f"the Gateway at {self.base_url} answered {path} with no list of missions; "
                "this tool will not report that as no missions."
            )
        return data

    def patch(self, mission_id: str, body: Dict[str, Any]) -> Dict[str, Any]:
        """Change a mission: its why, its name, or its state. Returns MissionPatchResultDto."""
        resp = self._ok_or_raise(self._request("PATCH", f"/missions/{mission_id}", body))
        return resp if isinstance(resp, dict) else {}


def _resolve_mission(query: str) -> Dict[str, Any]:
    """Resolve a Mission by full id, id prefix, or a case-insensitive name match.

    Typing a whole id is slow, so a prefix or part of the name is enough. Only the typing is relaxed: the id that finally reaches the Gateway is the
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


# Every state a mission can be in, in the order the count line names them.
MISSION_STATES = ("active", "complete", "removed")

# What `mission list --state` accepts: one state, or "all" (the same values the Gateway accepts).
MISSION_STATE_FILTERS = MISSION_STATES + ("all",)

# Every field `mission list --fields` accepts, and the three shown when it is not given. The why is
# free text that runs to hundreds of characters, so it is asked for, never shown by default.
MISSION_LIST_FIELDS = ("id", "name", "state", "why", "why-updated", "state-changed", "run")
MISSION_LIST_DEFAULT_FIELDS = ("id", "name", "state")


def _usage_error(message: str) -> None:
    print(f"Error: {message}", file=sys.stderr)
    raise typer.Exit(axi_output.USAGE_ERROR_EXIT_CODE)


def _mission_state(mission: Dict[str, Any]) -> str:
    """The mission's state as the Gateway sent it. A missing or unknown state is a broken answer and
    fails loudly: listing it under a guessed state would put finished work in the active list."""
    raw = mission.get("state", mission.get("State"))
    if raw not in MISSION_STATES:
        mid = _shown_id(mission.get("missionId", mission.get("MissionId")))
        shown = "missing" if raw is None else axi_output.format_value(raw) if isinstance(raw, str) else repr(raw)
        print(
            f"Error: the Gateway returned mission {mid} with state {shown}; "
            f"this tool knows only {', '.join(MISSION_STATES)}. --json shows the raw rows.",
            file=sys.stderr,
        )
        raise typer.Exit(1)
    return raw


def _shown_id(mid: Any) -> str:
    """A mission id from the Gateway, as one line of ASCII for an error sentence."""
    return axi_output.escape_ascii(str(mid))


def _bad_mission(mid: Any, what: str, row: Optional[int] = None) -> None:
    """Refuse a mission row the Gateway would never send, naming the mission and what is wrong with it."""
    where = f" (row {row})" if row is not None else ""
    print(
        f"Error: the Gateway returned mission {_shown_id(mid)} with {what}{where}. "
        "This tool will not list it as if it were sound; --json shows the raw rows.",
        file=sys.stderr,
    )
    raise typer.Exit(1)


def _describe(value: Any) -> str:
    return "null" if value is None else f"a {type(value).__name__}"


def _require_mission_rows(missions: List[Any]) -> None:
    """Every row must be a mission this tool can name: an object with a mission id and a name.

    A row that is not an object, a mission with no id or no name, a mission whose name is blank (the
    Gateway refuses a blank name on create and on rename), or a mission with any other field missing or
    of the wrong kind is a broken answer and fails loudly. Filtering
    it would silently drop a Gateway row, and rendering it would crash or show a mission nobody can
    address. Two rows with one id are refused too: the id is what every other verb addresses.
    """
    seen = set()
    for index, mission in enumerate(missions):
        row = index + 1
        if not isinstance(mission, dict):
            print(
                f"Error: the Gateway returned a mission row that is not an object (row {row}, "
                f"{type(mission).__name__}). --json shows the raw rows.",
                file=sys.stderr,
            )
            raise typer.Exit(1)
        mid = mission.get("missionId", mission.get("MissionId"))
        if not isinstance(mid, str) or not mid.strip():
            print(
                f"Error: the Gateway returned a mission with no mission id (row {row}). "
                "This tool will not list a mission it cannot name; --json shows the raw rows.",
                file=sys.stderr,
            )
            raise typer.Exit(1)
        if mid.lower() in seen:
            _bad_mission(mid, "an id that an earlier row already has", row)
        seen.add(mid.lower())
        name = mission.get("missionName", mission.get("MissionName"))
        if not isinstance(name, str):
            print(
                f"Error: the Gateway returned mission {_shown_id(mid)} with no mission name (row {row}). "
                "This tool will not list or filter a mission without its name; --json shows the raw rows.",
                file=sys.stderr,
            )
            raise typer.Exit(1)
        if not name.strip():
            _bad_mission(mid, "a blank mission name; the Gateway never stores one", row)
        # Every other field is checked here too, before any filter runs, so a broken row fails on every
        # path that reads the rows - never only when its field happens to be on screen.
        _mission_state(mission)
        for field in _CHECKED_MISSION_FIELDS:
            _MISSION_READERS[field](mission)


def _mission_text(mission: Dict[str, Any], names: tuple, *, nullable: bool, blank_ok: bool) -> Optional[str]:
    """The value of one mission field, checked where it is read.

    The Gateway always sends every field of MissionDto, so a missing key is a broken answer, never an
    empty value. Null is accepted only where the DTO is nullable, and blank text only where it means
    something (an empty why is the owner's "unset").
    """
    mid = mission.get("missionId", mission.get("MissionId"))
    key = next((name for name in names if name in mission), None)
    if key is None:
        _bad_mission(mid, f"no {names[0]}")
    value = mission[key]
    if value is None:
        if nullable:
            return None
        _bad_mission(mid, f"{names[0]} null; it is always text")
    if not isinstance(value, str):
        expected = "text or null" if nullable else "text"
        _bad_mission(mid, f"{names[0]} {_describe(value)}; it must be {expected}")
    if not blank_ok and not value.strip():
        _bad_mission(mid, f"a blank {names[0]}; the Gateway never sends one")
    return value


# How each `mission list` field is read from a MissionDto (origin/main, CcDirector.Gateway.Contracts).
# MissionName, Why and State are non-nullable strings; the three others are nullable. The id and name are
# checked by _require_mission_rows itself and the state by _mission_state, so they are read as checked.
_MISSION_READERS = {
    "id": lambda m: m.get("missionId", m.get("MissionId")),
    "name": lambda m: m.get("missionName", m.get("MissionName")),
    "why": lambda m: _mission_text(m, ("why", "Why"), nullable=False, blank_ok=True),
    "why-updated": lambda m: _mission_text(m, ("whyUpdatedAt", "WhyUpdatedAt"), nullable=True, blank_ok=False),
    "state-changed": lambda m: _mission_text(
        m, ("stateChangedAt", "StateChangedAt"), nullable=True, blank_ok=False
    ),
    "run": lambda m: _mission_text(m, ("workflowRunId", "WorkflowRunId"), nullable=True, blank_ok=False),
}


_CHECKED_MISSION_FIELDS = ("why", "why-updated", "state-changed", "run")


def _mission_record(mission: Dict[str, Any], state: str, chosen_fields: List[str]) -> Dict[str, object]:
    """The fields `mission list` shows, for one mission, each checked as it is read. Ids and names are
    never shortened."""
    return {f: state if f == "state" else _MISSION_READERS[f](mission) for f in chosen_fields}


def _matches_name(mission: Dict[str, Any], name: str) -> bool:
    """--name matches any part of the mission name, ignoring case."""
    return name.strip().lower() in mission.get("missionName", mission.get("MissionName")).lower()


def list_missions(
    json_output: bool,
    state: Optional[str] = None,
    *,
    name: Optional[str] = None,
    fields: Optional[str] = None,
) -> None:
    """List the Missions on the Gateway - active ones unless `state` asks for otherwise.

    `state` is one of active, complete, removed, or all; None means the Gateway's default, active only.
    `name` keeps only missions whose name contains it, ignoring case.
    """
    # Usage errors come before the fetch: a bad flag is the caller's to fix, whatever the Gateway holds.
    if json_output and fields is not None:
        _usage_error("--fields does not apply to --json, which always carries every field. Drop one of them.")
    chosen_fields = axi_output.parse_fields_or_exit(fields, MISSION_LIST_FIELDS, MISSION_LIST_DEFAULT_FIELDS)
    if state is not None and state not in MISSION_STATE_FILTERS:
        _usage_error(
            f"unknown --state value {axi_output.escape_ascii(state)!r}. "
            f"Valid states: {', '.join(MISSION_STATE_FILTERS)}"
        )
    if name is not None and not name.strip():
        _usage_error("--name needs a value.")

    if json_output:
        # The Gateway is asked exactly what it was always asked, so the unfiltered answer is byte for
        # byte what it was. --name narrows that same bare array; it never changes its shape.
        try:
            missions = MissionClient().list_all(state=state)
        except GatewayError as err:
            print(f"Error: {axi_output.escape_ascii(str(err))}", file=sys.stderr)
            raise typer.Exit(1)
        if name is not None:
            # Filtering reads the rows, so every row is checked in full first; the unfiltered answer is not.
            _require_mission_rows(missions)
            missions = [m for m in missions if _matches_name(m, name)]
        # Plain print, not console.print: Rich wraps long values when stdout is not a terminal.
        print(json.dumps(missions, indent=2))
        return

    # The plain list asks for every mission once, so it can say how many the filter left out.
    try:
        everything = MissionClient().list_all(state="all")
    except GatewayError as err:
        print(f"Error: {axi_output.escape_ascii(str(err))}", file=sys.stderr)
        raise typer.Exit(1)
    _require_mission_rows(everything)
    states = [_mission_state(m) for m in everything]

    # No --state means the Gateway's default view, active only - itself a filter, so the count line
    # says how many missions it left out.
    wanted = None if state == "all" else (state or "active")
    filtered = wanted is not None or name is not None
    rows = [
        (m, st)
        for m, st in zip(everything, states)
        if (wanted is None or st == wanted) and (name is None or _matches_name(m, name))
    ]

    records = [_mission_record(m, st, chosen_fields) for m, st in rows]
    # No rows means no breakdown at all: the helper refuses an empty one, and "count: 0" says it all.
    breakdown = [(s, n) for s in MISSION_STATES if (n := sum(1 for _, st in rows if st == s))] or None
    blocks = [
        axi_output.format_count(len(rows), total=len(everything) if filtered else None, breakdown=breakdown),
        axi_output.render_list("missions", chosen_fields, records),
    ]

    if not rows:
        if not everything:
            blocks.append("No missions on the Gateway.")
        else:
            # Say WHICH list is empty. "No missions" under a filter would read as "you have none at
            # all", which is a different and much more alarming statement.
            conditions = []
            if wanted is not None:
                conditions.append(f"state '{wanted}'")
            if name is not None:
                conditions.append(f"a name containing '{axi_output.escape_ascii(name.strip())}'")
            blocks.append(f"No missions with {' and '.join(conditions)}.")
    elif "why" not in chosen_fields:
        # A mission with no WHY is FLAGGED, not hidden - the same rule the Cockpit card follows. A
        # mission whose reason nobody wrote down is the thing worth noticing in this list.
        unset = sum(1 for m, _ in rows if not _MISSION_READERS["why"](m).strip())
        if unset:
            noun = "mission has" if unset == 1 else "missions have"
            blocks.append(f"{unset} of these {noun} no why set.")

    blocks.append(axi_output.format_help(_mission_list_help(rows, bool(everything), wanted, name, chosen_fields)))
    axi_output.write_blocks(sys.stdout, *blocks)


def _mission_list_help(
    rows: List[Any], any_missions: bool, wanted: Optional[str], name: Optional[str], chosen_fields: List[str]
) -> List[str]:
    """Concrete next commands. Runtime values are placeholders, never guessed."""
    if not rows:
        if not any_missions:
            return ["cc-devthrottle mission create <name>"]
        return ["cc-devthrottle mission list --all", "cc-devthrottle mission list --help"]
    commands = []
    if wanted == "active":
        commands.append("cc-devthrottle mission list --all")
    if list(chosen_fields) == list(MISSION_LIST_DEFAULT_FIELDS):
        commands.append("cc-devthrottle mission list --fields " + ",".join(MISSION_LIST_FIELDS))
    commands.append("cc-devthrottle mission list --json")
    commands.append("cc-devthrottle mission attach <session> <id>")
    commands.append("cc-devthrottle session spawn <repo> --controlled-by self --mission <id>")
    return commands


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
