"""Your inbox for cc-devthrottle (Factory Inbox mission).

The account's inbox, used like email (the owner's decision of 7 November 2026: "An agent should be able to do what it
can do with regular email. Read, delete, and also send, to factory mailboxes or its own."). Every verb runs with this
session's own Gateway key, in the account its Director is signed in as; nothing here can name another account.

- `inbox send --subject "..." --body "..." [--report <id>]`   tell the owner something, in his DevThrottle inbox
- `inbox list [--view unread|inbox|archived] [--count N]`     the inbox, newest first
- `inbox read <id>`                                           one message, its replies, and what agents did to it
- `inbox delete <id>`                                         ARCHIVE it - nothing is deleted; undo with restore
- `inbox restore <id>`                                        move an archived message back to the inbox
- `inbox mark <id> --read | --unread`                         mark it read or unread
- `inbox reply <id> "<text>" [--to-factory]`                  reply, routed where the message came from

READING DOES NOT MARK A MESSAGE READ: the owner has still not read it. Mark it read only when you are told to.
Every act is recorded on the message with this session's number and name, and the owner sees it: a reply this
session writes, and a message it leaves in a factory, is shown as from this session, never from the owner.

The owner may reply to a message this session sent. While this session runs, the reply arrives in its fleet inbox
(`cc-devthrottle message inbox`); after it ends, the owner is offered a new session instead.

Every word printed is the Gateway's (CLAUDE.md rule 7). `--json` prints the Gateway's answer unchanged.
"""

from __future__ import annotations

import json
import sys
import urllib.parse
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli, usage_errors  # noqa: E402

_TOOL = "cc-devthrottle inbox send"
_INBOX = "cc-devthrottle inbox"

#: The views the Gateway lists, spelled as it takes them.
VIEWS = ("unread", "inbox", "archived")

#: The most messages one list read may return. Must match YourInboxStore.MaxList on the Gateway.
LIST_MAX = 500

#: A subject is cut at this many characters in a list unless --full is passed.
SUBJECT_PREVIEW = 80

LIST_FIELDS = ["id", "state", "from", "sent", "subject", "agent"]
LIST_DEFAULT = ["id", "state", "from", "subject"]

#: What a 404 with no words of its own means here: a Gateway older than this command.
OLD_GATEWAY = ("the Gateway does not let a session use the inbox yet (it answered 404 with no reason) - "
               "it is older than this command.")


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


# ---------------------------------------------------------------------------------------------------
# Using the inbox like email (the owner's decision of 7 November 2026).
# ---------------------------------------------------------------------------------------------------

def _me() -> str:
    sid = gateway.session_id()
    if not sid:
        axi_cli.fail(
            "CC_SESSION_ID is not set, so this is not running inside a DevThrottle session. A session uses the inbox "
            "as itself, and the Gateway records which one did.",
            ["cc-devthrottle session list"],
        )
    return sid


def _message_route(message_id: str, tail: str = "") -> str:
    mid = (message_id or "").strip()
    if not mid:
        axi_cli.usage_error(f"name the message by its id. The ids are in {_INBOX} list.")
    path = f"{route(_me())}/{gateway.path_segment(mid)}"
    return f"{path}/{tail}" if tail else path


def _reason(exc: gateway.GatewayError) -> str:
    return OLD_GATEWAY if exc.status == 404 and exc.body is None else str(exc)


def _call(method: str, path: str, body: Optional[Dict[str, Any]], next_commands: Sequence[str]) -> Any:
    try:
        return gateway.get_json(path) if method == "GET" else gateway.post_json(path, body or {})
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), next_commands)


def _message_of(answer: Any) -> Dict[str, Any]:
    message = answer.get("message") if isinstance(answer, dict) else None
    if not isinstance(message, dict) or not message.get("id"):
        # An answer without the message must never read as done.
        axi_cli.fail("the Gateway answered without the message, so this command cannot say what happened to it.",
                     [axi_cli.CHECK_GATEWAY])
    return message


def _text(value: Any) -> str:
    return axi_cli.ascii_text(str(value)) if value is not None else ""


def _label(value: Any, key: str = "label") -> str:
    return _text(value.get(key)) if isinstance(value, dict) else ""


def _agent_lines(message: Dict[str, Any]) -> List[str]:
    acts = message.get("agentActions")
    if not isinstance(acts, list):
        return []
    return [_text(a.get("line")) for a in acts if isinstance(a, dict) and a.get("line")]


def list_messages(view: Optional[str], count: Optional[int], fields: Optional[str], full: bool, json_output: bool) -> None:
    """The account's inbox, one view, newest first. The view and the count go to the Gateway, so they apply to --json."""
    if fields is not None and json_output:
        usage_errors.usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen = usage_errors.parse_fields(fields, LIST_FIELDS, LIST_DEFAULT)
    query: Dict[str, str] = {}
    if view is not None:
        v = view.strip().lower()
        if v not in VIEWS:
            axi_cli.usage_error(f"--view must be one of {', '.join(VIEWS)}, not '{view}'.")
        query["view"] = v
    if count is not None:
        if count < 1 or count > LIST_MAX:
            axi_cli.usage_error(f"--count must be between 1 and {LIST_MAX}.")
        query["limit"] = str(count)
    path = route(_me()) + ("?" + urllib.parse.urlencode(query) if query else "")
    answer = _call("GET", path, None, [axi_cli.CHECK_GATEWAY])
    messages = answer.get("messages") if isinstance(answer, dict) else None
    total = answer.get("count") if isinstance(answer, dict) else None
    if not isinstance(messages, list) or not all(isinstance(m, dict) for m in messages):
        axi_cli.fail("the Gateway answered with no list of messages; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
    if not isinstance(total, int) or isinstance(total, bool) or total < len(messages):
        axi_cli.fail(f"the Gateway answered {len(messages)} messages with count {total!r}, so whether more exist is unknown.",
                     [f"{_INBOX} list --json"])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    records, cut_rows = [], 0
    for m in messages:
        subject = _text(m.get("subject"))
        if not full and len(subject) > SUBJECT_PREVIEW:
            subject = f"{subject[:SUBJECT_PREVIEW - 3]}... ({len(subject)} chars)"
            cut_rows += 1 if "subject" in chosen else 0
        records.append({
            "id": m.get("id"),
            "state": _text(m.get("stateLabel")),
            "from": _label(m.get("from")),
            "sent": m.get("sentAtUtc"),
            "subject": subject,
            "agent": _text(m.get("agentLine")) or None,
        })
    shown_view = _text(answer.get("view") or "inbox")
    blocks = [f"view: {shown_view}", axi_output.format_count(len(records), total),
              f"unread: {answer.get('unread') if isinstance(answer.get('unread'), int) else '?'}"]
    if records:
        blocks.append(axi_output.render_list("messages", chosen, records))
    else:
        blocks.append(_text(answer.get("empty")) or "none")
    if cut_rows:
        blocks.append(f"subject cut at {SUBJECT_PREVIEW} characters in {cut_rows} rows - use --full")
    if total > len(records):
        blocks.append(f"newest {len(records)} of {total} shown - use --count up to {LIST_MAX}")
    blocks.append(axi_output.format_help([f"{_INBOX} read <id>", f"{_INBOX} list --view archived"]))
    axi_output.write_blocks(sys.stdout, *blocks)


def read(message_id: str, json_output: bool) -> None:
    """One message, whole: who sent it, its state, its links, what agents did to it, its replies, and its body. Reading
    is recorded as this session's, and does NOT mark the message read."""
    answer = _call("GET", _message_route(message_id), None, [f"{_INBOX} list"])
    message = _message_of(answer)
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    mid = str(message.get("id"))
    lines = [
        f"id: {mid}",
        f"subject: {_text(message.get('subject'))}",
        f"from: {_label(message.get('from'))}",
        f"state: {_text(message.get('stateLabel'))}",
        f"sent: {_text(message.get('sentAtUtc'))}",
    ]
    reply_to = message.get("replyTo")
    lines.append(f"reply goes to: {_label(reply_to) if isinstance(reply_to, dict) else 'nobody - a reply to this message is not read'}")
    links = message.get("links") if isinstance(message.get("links"), list) else []
    lines.append(f"links: {len(links)}")
    lines.extend(f"  {_label(link)}" for link in links if isinstance(link, dict))
    acts = _agent_lines(message)
    lines.append(f"agent acts: {len(acts)}")
    lines.extend(f"  {a}" for a in acts)
    replies = message.get("replies") if isinstance(message.get("replies"), list) else []
    lines.append(f"replies: {len(replies)}")
    for r in replies:
        if isinstance(r, dict):
            lines.append(f"  {_text(r.get('sentAtUtc'))} {_text(r.get('label'))}: {_text(r.get('text'))}")
    lines.append("body:")
    lines.extend(f"  {_text(line)}" for line in str(message.get("body") or "").splitlines() or [""])
    lines.append("Reading did not mark it read. Mark it read only when you are told to.")
    axi_cli.write_lines(*lines)
    commands = [f"{_INBOX} reply {mid} \"<text>\"", f"{_INBOX} mark {mid} --read", f"{_INBOX} delete {mid}"]
    axi_cli.print_next(commands)


def _set_state(message_id: str, state: str, json_output: bool, done: str, next_commands: Sequence[str]) -> None:
    answer = _call("POST", _message_route(message_id, "state"), {"state": state}, [f"{_INBOX} list"])
    message = _message_of(answer)
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    lines = [done, f"id: {message.get('id')}", f"state: {_text(message.get('stateLabel'))}"]
    if message.get("agentLine"):
        lines.append(f"recorded: {_text(message.get('agentLine'))}")
    axi_cli.write_lines(*lines)
    axi_cli.print_next(next_commands)


def delete(message_id: str, json_output: bool) -> None:
    """ARCHIVE a message - an agent's delete (the owner, 7 November 2026: "Delete means Archive")."""
    mid = (message_id or "").strip()
    _set_state(mid, "archived", json_output,
               "archived: delete is archive - the message left the inbox and is under Archived; nothing was deleted",
               [f"{_INBOX} restore {axi_cli.bare(mid, '<id>')}", f"{_INBOX} list --view archived"])


def restore(message_id: str, json_output: bool) -> None:
    """Move an archived message back to the inbox: unread if nobody ever marked it read, read if somebody did."""
    mid = (message_id or "").strip()
    _set_state(mid, "restore", json_output, "restored: back in the inbox",
               [f"{_INBOX} read {axi_cli.bare(mid, '<id>')}", f"{_INBOX} list"])


def mark(message_id: str, read_flag: bool, unread_flag: bool, json_output: bool) -> None:
    """Mark a message read or unread - only when you are told to."""
    if read_flag == unread_flag:
        axi_cli.usage_error("pass exactly one of --read and --unread.")
    mid = (message_id or "").strip()
    state = "read" if read_flag else "unread"
    _set_state(mid, state, json_output, f"marked: {state}", [f"{_INBOX} list"])


def reply(message_id: str, text: str, to_factory: bool, json_output: bool) -> None:
    """Reply to a message, routed as the owner's reply is: into the sending session's inbox while it runs, or into a
    factory's Factory inbox. The reply is shown as from this session, never from the owner."""
    if not text or not text.strip():
        axi_cli.usage_error("the reply is empty. Write what you want to say.")
    mid = (message_id or "").strip()
    body: Dict[str, Any] = {"text": text}
    if to_factory:
        body["to"] = "factory"
    try:
        answer = gateway.post_json(_message_route(mid, "reply"), body)
    except gateway.GatewayError as exc:
        offers = exc.body.get("offers") if isinstance(exc.body, dict) else None
        if isinstance(offers, list) and offers:
            # The session that sent it has ended: nothing was sent, and the Gateway says where the reply can go.
            words = [_reason(exc)] + [f"offer: {_label(o)}" for o in offers if isinstance(o, dict)]
            commands = [f"{_INBOX} reply {axi_cli.bare(mid, '<id>')} \"<text>\" --to-factory"
                        for o in offers if isinstance(o, dict) and o.get("kind") == "factory"]
            axi_cli.fail(" ".join(words), commands or [f"{_INBOX} read {axi_cli.bare(mid, '<id>')}"])
        axi_cli.fail(_reason(exc), [f"{_INBOX} read {axi_cli.bare(mid, '<id>')}"])
        return
    if not isinstance(answer, dict) or not isinstance(answer.get("reply"), dict):
        axi_cli.fail("the Gateway answered without the reply it sent, so this command cannot say it was sent.",
                     [axi_cli.CHECK_GATEWAY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    sent = answer["reply"]
    axi_cli.write_lines(
        "replied",
        f"reply: {sent.get('id')}",
        f"went: {_text(sent.get('label'))}",
        "The message's state is unchanged: replying as an agent does not mark it read for the owner.",
    )
    axi_cli.print_next([f"{_INBOX} read {axi_cli.bare(mid, '<id>')}"])
