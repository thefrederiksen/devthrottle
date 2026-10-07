"""Message link operations for cc-devthrottle (issue #3548).

A message link lets two sessions that are not owner and worker message each other. The owner sets one up,
or a session the owner has RAISED does it for him - the Fleet Manager first. An ordinary session is refused
by the Gateway, not by this command, so editing this file cannot get round it.

A link names exactly two sessions and how much talking it allows:

  once             one message, no reply; then the link is used up
  once-with-reply  one message and its reply; then the link is used up
  ongoing          as much as they need, both ways, until removed or either session ends

`add` resolves both sessions the way every session verb does (number, id prefix, or exact name). The Gateway
tells the sending session, in its inbox, what it may now send.

A session that needs a link ASKS for one with `request`; the owner - or a raised session - lists the requests
with `requests` and answers one with `answer`. A request allows nothing by itself.
"""

from __future__ import annotations

import json
from typing import Any, Dict, List

import typer
from rich.console import Console

from cc_shared import gateway

from . import session_ops

console = Console()

PATH = "fleet/links"

REQUESTS_PATH = "fleet/link-requests"

AMOUNTS = ("once", "once-with-reply", "ongoing")


def _fail(message: str) -> None:
    console.print(f"[red]Error:[/red] {message}")
    raise typer.Exit(1)


def _resolve(target: str, command: str) -> Dict[str, Any]:
    return session_ops.resolve_session(target, command_name=command)


def _print_link(link: Dict[str, Any], indent: str = "  ") -> None:
    console.print(f"{indent}id: {link.get('linkId', '')}")
    console.print(f"{indent}from: {link.get('senderSessionId', '')}")
    console.print(f"{indent}to: {link.get('recipientSessionId', '')}")
    console.print(f"{indent}amount: {link.get('amount', '')}")
    console.print(f"{indent}status: {link.get('status', '')}")
    console.print(f"{indent}summary: {link.get('summary', '')}")
    console.print(f"{indent}set up by: {link.get('setUpBy', '')}")


def add(sender: str, recipient: str, amount: str, json_output: bool) -> None:
    """Set up a message link from SENDER to RECIPIENT."""
    amount = (amount or "").strip().lower()
    if amount not in AMOUNTS:
        _fail(f"amount must be one of {', '.join(AMOUNTS)}; '{amount}' was given.")
    command = "cc-devthrottle message link add"
    sender_id = gateway.field(_resolve(sender, command), "sessionId", "SessionId")
    recipient_id = gateway.field(_resolve(recipient, command), "sessionId", "SessionId")
    try:
        payload = gateway.post_json(PATH, {
            "senderSessionId": sender_id,
            "recipientSessionId": recipient_id,
            "amount": amount,
        }) or {}
    except gateway.GatewayError as err:
        _fail(str(err))
    if json_output:
        print(json.dumps(payload))
        return
    console.print("link set up:")
    _print_link(payload.get("link") or {})
    replaced: List[Dict[str, Any]] = payload.get("replaced") or []
    if replaced:
        console.print(f"replaced[{len(replaced)}]:")
        for old in replaced:
            console.print(f"  {old.get('linkId', '')}")
    console.print("help[2]:")
    console.print("  cc-devthrottle message link list")
    console.print(f"  cc-devthrottle message link remove {(payload.get('link') or {}).get('linkId', '<id>')}")


def list_links(json_output: bool) -> None:
    """Print every live link, and every link that stopped in the last 30 days."""
    try:
        payload = gateway.get_json(PATH) or {}
    except gateway.GatewayError as err:
        _fail(str(err))
    if json_output:
        print(json.dumps(payload))
        return
    links: List[Dict[str, Any]] = payload.get("links") or []
    days = payload.get("stoppedWithinDays", 30)
    console.print(f"links[{len(links)}] (live, and stopped in the last {days} days):")
    if not links:
        console.print("  count: 0")
    for link in links:
        console.print("  -")
        _print_link(link, indent="    ")
    console.print("help[1]:")
    console.print("  cc-devthrottle message link add <from> <to> --amount once|once-with-reply|ongoing")


def remove(link_id: str, json_output: bool) -> None:
    """Remove a message link."""
    try:
        payload = gateway.delete(f"{PATH}/{gateway.path_segment(link_id)}") or {}
    except gateway.GatewayError as err:
        _fail(str(err))
    if json_output:
        print(json.dumps(payload))
        return
    console.print("link:")
    _print_link(payload)


def _print_request(req: Dict[str, Any], indent: str = "  ") -> None:
    console.print(f"{indent}id: {req.get('requestId', '')}")
    console.print(f"{indent}from: {req.get('requesterSessionId', '')}")
    console.print(f"{indent}to: {req.get('targetSessionId', '')}")
    console.print(f"{indent}reason: {req.get('reason', '')}")
    if req.get("requestedAmount"):
        console.print(f"{indent}asks for: {req.get('requestedAmount')}")
    console.print(f"{indent}status: {req.get('status', '')}")
    if req.get("amount"):
        console.print(f"{indent}amount: {req.get('amount')}")
    if req.get("linkId"):
        console.print(f"{indent}link: {req.get('linkId')}")


def request(target: str, reason: str, amount: str, json_output: bool) -> None:
    """Ask the owner for a message link from THIS session to TARGET, for AMOUNT of talking (issue #3631).

    The owner approves or denies exactly what is asked for, so ask for what the work needs.
    """
    if not (reason or "").strip():
        _fail("say why you need to talk to that session, in one sentence the user can decide on.")
    amount = (amount or "").strip().lower()
    if amount not in AMOUNTS:
        _fail(f"amount must be one of {', '.join(AMOUNTS)}; '{amount}' was given.")
    target_id = gateway.field(_resolve(target, "cc-devthrottle message request"), "sessionId", "SessionId")
    try:
        payload = gateway.post_json(REQUESTS_PATH, {"targetSessionId": target_id, "reason": reason, "amount": amount}) or {}
    except gateway.GatewayError as err:
        _fail(str(err))
    if json_output:
        print(json.dumps(payload))
        return
    console.print("request:")
    _print_request(payload.get("request") or {})
    console.print(f"note: {payload.get('note', '')}")


def list_requests(json_output: bool) -> None:
    """Print every waiting request, and every request answered in the last 7 days."""
    try:
        payload = gateway.get_json(REQUESTS_PATH) or {}
    except gateway.GatewayError as err:
        _fail(str(err))
    if json_output:
        print(json.dumps(payload))
        return
    reqs: List[Dict[str, Any]] = payload.get("requests") or []
    days = payload.get("answeredWithinDays", 7)
    console.print(f"requests[{len(reqs)}] (waiting, and answered in the last {days} days):")
    if not reqs:
        console.print("  count: 0")
    for req in reqs:
        console.print("  -")
        _print_request(req, indent="    ")
    console.print("help[1]:")
    console.print("  cc-devthrottle message link answer <id> --approve  (or --amount once|once-with-reply|ongoing, or --decline)")


def answer(request_id: str, amount: str, decline: bool, json_output: bool, approve: bool = False) -> None:
    """Allow what a request asked for, allow it with another amount, or decline it."""
    amount = (amount or "").strip().lower()
    if int(decline) + int(approve) + int(bool(amount)) != 1:
        _fail("answer with --approve to allow what was asked, --amount to allow that instead, or --decline to say no "
              "- exactly one of the three.")
    if amount and amount not in AMOUNTS:
        _fail(f"amount must be one of {', '.join(AMOUNTS)}; '{amount}' was given.")
    body: Dict[str, Any]
    if decline:
        body = {"decline": True}
    elif approve:
        body = {"approve": True}
    else:
        body = {"amount": amount}
    try:
        payload = gateway.post_json(f"{REQUESTS_PATH}/{gateway.path_segment(request_id)}/answer", body) or {}
    except gateway.GatewayError as err:
        _fail(str(err))
    if json_output:
        print(json.dumps(payload))
        return
    console.print("request:")
    _print_request(payload.get("request") or {})
    if payload.get("link"):
        console.print("link set up:")
        _print_link(payload["link"])
