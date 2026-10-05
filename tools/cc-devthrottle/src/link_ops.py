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
