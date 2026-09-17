"""Fleet Manager mark operations for cc-devthrottle.

An account has exactly one Fleet Manager, and the ACCOUNT says which session it is: one session id,
held on the Gateway (GET and PUT /gateway/fleet-manager). The Wingman judges the sessions that session
directly owns. The workflow a session is seated on never makes it the Fleet Manager - only this mark does.

`set` resolves the session the same way every session verb does (number, id prefix, or name), and with
no target marks the session running the command. `clear` removes the mark. `show` prints it.

`session hand-over` (the Fleet Manager mission, step 8) changes who owns a running session: to the Fleet
Manager, or back to the owner. It is the OWNER'S change - the Gateway allows it only from the owner's own
phone or browser, and refuses every session key, the Fleet Manager's included - so from a session this
command prints the Gateway's refusal. It exists so the route has a documented door and so its refusal is
read in words, not a status number.
"""

from __future__ import annotations

import json
from typing import Any, Dict, List, Optional

import typer
from rich.console import Console

from cc_shared import gateway

from . import axi_cli
from . import session_ops

console = Console()

PATH = "gateway/fleet-manager"


def _fail(message: str) -> None:
    console.print(f"[red]Error:[/red] {message}")
    raise typer.Exit(1)


def _name_in(sessions: List[Dict[str, Any]], session_id: str) -> Optional[str]:
    for s in sessions:
        if gateway.field(s, "sessionId", "SessionId").lower() == session_id.lower():
            return gateway.field(s, "name", "Name") or "(unnamed)"
    return None


def _print_mark(session_id: Optional[str], name: Optional[str], verb: str) -> None:
    if not session_id:
        console.print(f"fleet-manager: none{verb}")
        console.print("help[1]:")
        console.print("  cc-devthrottle fleet-manager set <session>")
        return
    console.print(f"fleet-manager{verb}:")
    console.print(f"  id: {session_id}")
    console.print(f"  name: {name if name is not None else '(not in the current fleet list)'}")
    console.print("help[2]:")
    console.print("  cc-devthrottle fleet-manager clear")
    console.print(f"  cc-devthrottle session buffer {session_id}")


def show(json_output: bool) -> None:
    """Print which session this account has marked as its Fleet Manager."""
    try:
        payload = gateway.get_json(PATH) or {}
    except gateway.GatewayError as err:
        _fail(str(err))
    session_id = payload.get("sessionId")
    if json_output:
        print(json.dumps({"sessionId": session_id}))
        return
    name = None
    if session_id:
        sessions, _, _, _ = session_ops.fleet_or_exit()
        name = _name_in(sessions, session_id)
    _print_mark(session_id, name, "")


def set_mark(target: Optional[str], json_output: bool) -> None:
    """Mark one session as this account's Fleet Manager - the named one, or this session."""
    if target:
        session = session_ops.resolve_session(target, command_name="cc-devthrottle fleet-manager set")
        session_id = gateway.field(session, "sessionId", "SessionId")
        name: Optional[str] = gateway.field(session, "name", "Name") or "(unnamed)"
    else:
        session_id = gateway.session_id() or ""
        if not session_id:
            _fail(
                "no session given and CC_SESSION_ID is not set. "
                "Name the session: cc-devthrottle fleet-manager set <session>"
            )
        name = None
    try:
        payload = gateway.put_json(PATH, {"sessionId": session_id}) or {}
    except gateway.GatewayError as err:
        _fail(str(err))
    stored = payload.get("sessionId")
    if json_output:
        print(json.dumps({"sessionId": stored}))
        return
    if name is None:
        sessions, _, _, _ = session_ops.fleet_or_exit()
        name = _name_in(sessions, stored or session_id)
    _print_mark(stored, name, " set")


def clear(json_output: bool) -> None:
    """Remove this account's Fleet Manager mark."""
    try:
        payload = gateway.put_json(PATH, {"sessionId": None}) or {}
    except gateway.GatewayError as err:
        _fail(str(err))
    if json_output:
        print(json.dumps({"sessionId": payload.get("sessionId")}))
        return
    _print_mark(None, None, " (cleared)")


HAND_OVER_PATH = "gateway/fleet-manager/hand-over"
HAND_OVER_DIRECTIONS = ("fleet-manager", "owner")


def hand_over(target: str, to: Optional[str], json_output: bool) -> None:
    """Hand a running session to the Fleet Manager, or back to the owner, through the Gateway."""
    direction = (to or "").strip().lower()
    if direction not in HAND_OVER_DIRECTIONS:
        axi_cli.usage_error(
            f"--to must be one of: {', '.join(HAND_OVER_DIRECTIONS)}"
            + (f" (got '{to}')." if to else " - it was not given.")
        )
    session = session_ops.resolve_session(target, command_name="cc-devthrottle session hand-over")
    session_id = gateway.field(session, "sessionId", "SessionId")
    other = "owner" if direction == "fleet-manager" else "fleet-manager"
    try:
        payload = gateway.post_json(HAND_OVER_PATH, {"session": session_id, "to": direction}) or {}
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"session {session_id} was not handed over: {err}",
            ["cc-devthrottle session list --fields id,name,state", "cc-devthrottle fleet-manager show"],
        )
    # REPORTED FROM THE ANSWER, never from the request: the answer must name this session, and an owner that
    # matches the direction - a session owner when handed to the Fleet Manager, none when handed back.
    check = ["cc-devthrottle session list --fields id,name,state", "cc-devthrottle fleet-manager show"]
    what = f"the hand over of session {session_id}"
    answered = axi_cli.confirmed(
        payload, ["sessionId"], what, check,
        accept=lambda v: isinstance(v, str) and v.lower() == session_id.lower(),
    )
    if direction == "fleet-manager":
        owner = axi_cli.confirmed(
            payload, ["ownerSessionId"], what, check,
            accept=lambda v: isinstance(v, str) and v.strip() != "" and v.lower() != session_id.lower(),
        )
    else:
        owner = axi_cli.confirmed(payload, ["ownerSessionId"], what, check, accept=axi_cli.is_cleared)
    if json_output:
        print(json.dumps(payload))
        return
    session_id = answered
    axi_cli.write_lines(
        payload.get("sentence") or "The Gateway answered without a sentence.",
        f"session: {session_id}",
        f"owner: {owner if owner else 'you'}",
    )
    axi_cli.print_next([
        "cc-devthrottle session list --fields id,name,state",
        f"cc-devthrottle session hand-over {axi_cli.bare(session_id, '<session>')} --to {other}",
    ])
