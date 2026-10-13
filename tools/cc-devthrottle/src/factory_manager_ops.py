"""`cc-devthrottle factory manager` and `cc-devthrottle factory name-check` (Factory Control, step 5).

An account has at most one Factory Manager: the one standing session over all of its factories. The ACCOUNT
says which session it is - one session id held on the Gateway (GET and PUT /gateway/factory-manager), the
same kind of mark as the Fleet Manager's. The bound Factory Manager may resolve any problem of the account
(`run resolve`); the Gateway reads the mark and records the resolver as the Factory Manager.

- `factory manager show` prints the mark, and the computer and folder the Factory Manager starts on.
- `factory manager set [<session>]` marks a session (number, id prefix or name), or this session.
- `factory manager clear` removes the mark.
- `factory name-check` prints the Gateway's name check: every seat that says CEO, or whose name is not a
  role word of its factory. The rule and every sentence are the Gateway's; nothing is decided here.

Every route is mapped only while factory agents are switched on, so a 404 is said in words.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli  # noqa: E402
from . import session_ops  # noqa: E402

#: The account's Factory Manager mark.
BINDING_ROUTE = "gateway/factory-manager"

#: The name check, folded on the Gateway.
NAME_CHECK_ROUTE = "gateway/factory/name-check"

#: What a 404 means: the switch is off, so the Gateway cannot say it in its own words.
FEATURE_OFF = (
    "factory agents are switched off for this account, so the Gateway does not serve the Factory Manager "
    "(it answered 404). A Gateway older than this command answers the same way."
)

_SET = "cc-devthrottle factory manager set <session>"
_CLEAR = "cc-devthrottle factory manager clear"


def _reason(exc: gateway.GatewayError) -> str:
    return FEATURE_OFF if exc.status == 404 else str(exc)


def _fail(message: str) -> None:
    axi_cli.fail(message, [axi_cli.CHECK_GATEWAY])


def _name_in(sessions: List[Dict[str, Any]], session_id: str) -> Optional[str]:
    for s in sessions:
        if gateway.field(s, "sessionId", "SessionId").lower() == session_id.lower():
            return gateway.field(s, "name", "Name") or "(unnamed)"
    return None


def _print_binding(payload: Dict[str, Any], name: Optional[str], verb: str) -> None:
    session_id = payload.get("sessionId")
    computer = payload.get("computer")
    folder = payload.get("folder")
    lines = []
    if not session_id:
        lines.append(f"factory-manager: none{verb}")
    else:
        lines.append(f"factory-manager{verb}:")
        lines.append(f"  id: {session_id}")
        lines.append(f"  name: {axi_cli.ascii_text(name) if name is not None else '(not in the current fleet list)'}")
    lines.append(f"  computer: {axi_cli.ascii_text(computer) if computer else '(not chosen yet)'}")
    lines.append(f"  folder: {axi_cli.ascii_text(folder) if folder else '(not chosen yet)'}")
    axi_cli.write_lines(*lines)
    axi_cli.print_next([_CLEAR, f"cc-devthrottle session buffer {session_id}"] if session_id else [_SET])


def show(json_output: bool) -> None:
    """Print which session is this account's Factory Manager, and where it starts."""
    try:
        payload = gateway.get_json(BINDING_ROUTE) or {}
    except gateway.GatewayError as exc:
        _fail(_reason(exc))
    if json_output:
        print(json.dumps(payload))
        return
    name = None
    if payload.get("sessionId"):
        sessions, _, _, _ = session_ops.fleet_or_exit()
        name = _name_in(sessions, payload["sessionId"])
    _print_binding(payload, name, "")


def set_mark(target: Optional[str], json_output: bool) -> None:
    """Mark one session as this account's Factory Manager - the named one, or this session."""
    if target:
        session = session_ops.resolve_session(target, command_name="cc-devthrottle factory manager set")
        session_id = gateway.field(session, "sessionId", "SessionId")
        name: Optional[str] = gateway.field(session, "name", "Name") or "(unnamed)"
    else:
        session_id = gateway.session_id() or ""
        if not session_id:
            axi_cli.fail("no session given and CC_SESSION_ID is not set. Name the session.", [_SET])
        name = None
    try:
        payload = gateway.put_json(BINDING_ROUTE, {"sessionId": session_id}) or {}
    except gateway.GatewayError as exc:
        _fail(_reason(exc))
    if json_output:
        print(json.dumps(payload))
        return
    if name is None and payload.get("sessionId"):
        sessions, _, _, _ = session_ops.fleet_or_exit()
        name = _name_in(sessions, payload["sessionId"])
    _print_binding(payload, name, " set")


def clear(json_output: bool) -> None:
    """Remove this account's Factory Manager mark."""
    try:
        payload = gateway.put_json(BINDING_ROUTE, {"sessionId": None}) or {}
    except gateway.GatewayError as exc:
        _fail(_reason(exc))
    if json_output:
        print(json.dumps(payload))
        return
    _print_binding(payload, None, " (cleared)")


def name_check(json_output: bool) -> None:
    """Print the Gateway's name check over every factory of the account."""
    try:
        answer = gateway.get_json(NAME_CHECK_ROUTE)
    except gateway.GatewayError as exc:
        _fail(_reason(exc))
    findings = answer.get("findings") if isinstance(answer, dict) else None
    if not isinstance(findings, list):
        # Absent is not empty: an answer with no list must never read as "every factory passes".
        axi_cli.fail("the Gateway answered with no list of findings; this command will not report that as a pass.",
                     [axi_cli.CHECK_GATEWAY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    records = [
        {"factory": f.get("factory"), "seat": f.get("seatId"), "kind": f.get("kind"), "text": f.get("text")}
        for f in findings
    ]
    blocks = [
        axi_output.format_count(len(records)),
        f"factories-checked: {answer.get('factoriesChecked', 0)}",
        axi_cli.ascii_text(f"rule: {answer.get('ruleText') or ''}"),
    ]
    if records:
        blocks.append(axi_output.render_list("findings", ["factory", "seat", "kind", "text"], records))
    elif answer.get("passText"):
        blocks.append(axi_cli.ascii_text(str(answer["passText"])))
    blocks.append(axi_output.format_help(["cc-devthrottle factory status --factory <id>"]))
    axi_output.write_blocks(sys.stdout, *blocks)
