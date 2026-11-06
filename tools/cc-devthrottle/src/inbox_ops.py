"""Your inbox for cc-devthrottle (Factory Inbox mission, phase 3).

One verb, run with this session's own Gateway key:

- `inbox send --subject "..." --body "..." [--report <id>]`   tell the owner something, in his DevThrottle inbox

It replaces emailing the owner. The message lands in Your inbox on his phone and in the Cockpit, unread, with who sent
it and a link back to this session - and to its factory, and to a dev report when one is named. The Gateway reads who
sent it from its own record of this session; nothing this command sends can change that.

The owner may reply from his inbox. While this session runs, the reply arrives in its fleet inbox
(`cc-devthrottle message inbox`); after it ends, the owner is offered a new session instead.

A session never reads the owner's inbox: the list, the marks and the replies are his alone.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, Optional

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

from . import axi_cli  # noqa: E402

_TOOL = "cc-devthrottle inbox send"


def route(session_id: str) -> str:
    """The Gateway route a session sends to the owner's inbox through."""
    return f"sessions/{gateway.path_segment(session_id)}/inbox"


def send(subject: str, body: str, report: Optional[str], json_output: bool) -> None:
    """Send the owner a message in Your inbox, as this session."""
    if not subject or not subject.strip():
        axi_cli.usage_error("--subject is empty. Say in one line what the message is about.")
    if "\n" in subject or "\r" in subject:
        axi_cli.usage_error("--subject is more than one line. Put the detail in --body.")
    if not body or not body.strip():
        axi_cli.usage_error("--body is empty. Write what the owner needs to know.")

    sid = gateway.session_id()
    if not sid:
        axi_cli.fail(
            "CC_SESSION_ID is not set, so this is not running inside a DevThrottle session. Only a session sends to "
            "the owner's inbox, and the Gateway records which one did.",
            ["cc-devthrottle session list"],
        )

    payload: Dict[str, Any] = {"subject": subject.strip(), "body": body}
    if report and report.strip():
        payload["reportId"] = report.strip()

    try:
        answer = gateway.post_json(route(sid), payload)
    except gateway.GatewayError as exc:
        axi_cli.fail(str(exc), [axi_cli.CHECK_GATEWAY])
        return

    message = answer.get("message") if isinstance(answer, dict) else None
    if not isinstance(message, dict) or not message.get("id"):
        # An answer without the message must never read as "sent".
        axi_cli.fail(
            "the Gateway answered without the message it wrote, so this command cannot say it was sent.",
            [axi_cli.CHECK_GATEWAY],
        )
        return

    if json_output:
        print(json.dumps(answer))
        return

    sender = message.get("from") if isinstance(message.get("from"), dict) else {}
    links = message.get("links") if isinstance(message.get("links"), list) else []
    lines = [
        "sent: Your inbox",
        f"id: {message.get('id')}",
        f"subject: {axi_cli.ascii_text(str(message.get('subject', '')))}",
        f"from: {axi_cli.ascii_text(str(sender.get('label', '')))}",
        f"state: {axi_cli.ascii_text(str(message.get('stateLabel', '')))}",
        f"links: {len(links)}",
    ]
    for link in links:
        if isinstance(link, dict):
            lines.append(f"  {axi_cli.ascii_text(str(link.get('label', '')))}")
    lines.append("The owner reads it on his phone or in the Cockpit. A reply reaches this session's inbox while it runs.")
    axi_cli.write_lines(*lines)
    axi_cli.print_next(["cc-devthrottle message inbox"])
