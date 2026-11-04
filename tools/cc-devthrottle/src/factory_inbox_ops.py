"""`cc-devthrottle factory inbox` - the Factory inbox (Factory Inbox mission, phase 1).

The owner leaves a factory a message from the phone or the Cockpit. The factory's Secretary files it and hands one
bundle to the Boss; the Boss decides each item. There is NO `send` here: the owner's send is refused to a session
key on the Gateway, because a session never leaves a message as the owner.

Reads anyone in the account may run (the Gateway allows them to a session key, so a Boss reads its own summary):

- `factory inbox list`                       every factory's inbox: how many messages wait, the last message
- `factory inbox thread <factory>`           one factory's messages, each with its trail (Sent - Received - decision)
- `factory inbox summary <factory> [--since]` what came in since a time and the decision on each, built from the log
- `factory inbox todo <factory>`             the factory's to-do list, written by the Boss's `todo` decisions

Seat commands, run inside a factory session. The Gateway reads the caller's factory from the session's own record;
the command never names it:

- `factory inbox new`                        the messages not yet received (the Secretary's work)
- `factory inbox receive <id>...`            claim and mark Received, with merges and links
- `factory inbox bundle`                     the Boss reads its open bundles
- `factory inbox bundle --hand <id>...`      the Secretary hands one bundle to the Boss
- `factory inbox decide <id> <kind>`         the Boss's decision: delegated --session, todo, noted,
                                             declined --why, cannot --why, pushback --why

Nothing here decides anything. Every state word and every trail line is the Gateway's (CLAUDE.md rule 7); this
module prints them as they came. `--json` prints the Gateway's answer unchanged.
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

#: The owner's routes, by factory.
OWNER_ROUTE = "gateway/factory"
#: The session's own routes, under its session id.
SESSION_ROUTE = "factory-inbox"

_TOOL = "cc-devthrottle factory inbox"

#: The decisions a Boss may take, and what each needs. Spelled exactly as the Gateway takes them.
NEEDS_SESSION = ("delegated",)
NEEDS_WHY = ("declined", "cannot", "pushback")
DECISIONS = ("delegated", "todo", "noted", "declined", "cannot", "pushback")

#: A message's text is cut at this many characters in a list unless --full is passed.
TEXT_PREVIEW = 80

#: What a 404 with no words of its own means here: a Gateway older than this command.
OLD_GATEWAY = ("the Gateway does not know the Factory inbox yet (it answered 404 with no reason) - "
               "it is older than this command.")

LIST_FIELDS = ["factory", "title", "waiting", "lastState", "lastSent", "lastText"]
LIST_DEFAULT = ["factory", "waiting", "lastState", "lastText"]
MESSAGE_FIELDS = ["id", "state", "sent", "note", "source", "surface", "decision", "why", "bundle", "trail", "text"]
MESSAGE_DEFAULT = ["id", "state", "trail", "text"]


# ---------------------------------------------------------------------------------------------------
# Shared pieces.
# ---------------------------------------------------------------------------------------------------

def _reason(exc: gateway.GatewayError) -> str:
    # A 404 that carried no answer of its own is a Gateway with no such route; one with an answer says why.
    return OLD_GATEWAY if exc.status == 404 and exc.body is None else str(exc)


def _get(route: str, next_commands: Sequence[str]) -> Any:
    try:
        return gateway.get_json(route)
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), next_commands)


def _post(route: str, body: Dict[str, Any], next_commands: Sequence[str]) -> Any:
    try:
        return gateway.post_json(route, body)
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), next_commands)


def _factory_route(factory: str, tail: str = "inbox") -> str:
    name = (factory or "").strip()
    if not name:
        axi_cli.usage_error(f"name the factory, for example: {_TOOL} thread <factory>. The ids are in {_TOOL} list.")
    return f"{OWNER_ROUTE}/{gateway.path_segment(name)}/{tail}"


def _session_route(tail: str = "") -> str:
    """This session's own inbox route. A seat command outside a session has nobody to act as."""
    sid = gateway.session_id()
    if sid is None:
        axi_cli.fail(
            "CC_SESSION_ID is not set, so this is not a DevThrottle session. The seat commands act as the factory "
            "session that runs them - the Secretary or the Boss - and the Gateway reads the factory from that "
            "session's record.",
            [f"{_TOOL} list", f"{_TOOL} thread <factory>"],
        )
    route = f"sessions/{gateway.path_segment(sid)}/{SESSION_ROUTE}"
    return f"{route}/{tail}" if tail else route


def _rows(answer: Any, key: str, what: str) -> List[Dict[str, Any]]:
    """The list under `key`, or exit 1. Absent is not empty: an answer without the list must never read as none."""
    rows = answer.get(key) if isinstance(answer, dict) else None
    if not isinstance(rows, list) or not all(isinstance(r, dict) for r in rows):
        axi_cli.fail(f"the Gateway answered with no list of {what}; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
    return rows


def _check_fields(fields: Optional[str], json_output: bool, valid: Sequence[str],
                  default: Sequence[str]) -> List[str]:
    if fields is not None and json_output:
        usage_errors.usage_error(axi_cli.FIELDS_WITH_JSON)
    return usage_errors.parse_fields(fields, valid, default)


def _cut(text: Any, full: bool) -> tuple:
    """`text` as one line of ASCII, cut at TEXT_PREVIEW characters unless `full`. Returns (shown, was_cut)."""
    line = axi_cli.ascii_text(str(text)) if text is not None else ""
    if full or len(line) <= TEXT_PREVIEW:
        return line, False
    return f"{line[:TEXT_PREVIEW - 3]}... ({len(line)} chars)", True


def _one_line(value: Any) -> Optional[str]:
    return axi_cli.ascii_text(str(value)) if value is not None and str(value) != "" else None


def _trail(message: Dict[str, Any]) -> str:
    """The trail as the Gateway wrote it, one label per step, joined in the order given."""
    steps = message.get("trail")
    if not isinstance(steps, list):
        return ""
    return " - ".join(str(step.get("label")) for step in steps if isinstance(step, dict) and step.get("label"))


def _decision(message: Dict[str, Any]) -> Dict[str, Any]:
    decision = message.get("decision")
    return decision if isinstance(decision, dict) else {}


def _message_record(message: Dict[str, Any], full: bool) -> tuple:
    text, cut = _cut(message.get("text"), full)
    decision = _decision(message)
    return {
        "id": message.get("id"),
        "state": message.get("stateLabel"),
        "sent": message.get("sentAtUtc"),
        "note": message.get("noteLabel"),
        "source": message.get("source"),
        "surface": message.get("surface"),
        "decision": decision.get("label"),
        "why": _one_line(decision.get("why")),
        "bundle": message.get("bundleId"),
        "trail": _trail(message),
        "text": text,
    }, cut


def _message_blocks(messages: List[Dict[str, Any]], fields: List[str], full: bool, name: str = "messages") -> List[str]:
    """The count line, the list, and a line saying how many rows had their text cut."""
    records, cut_rows = [], 0
    for message in messages:
        record, cut = _message_record(message, full)
        records.append(record)
        cut_rows += 1 if cut and "text" in fields else 0
    blocks = [axi_output.format_count(len(records))]
    if records:
        blocks.append(axi_output.render_list(name, fields, records))
    if cut_rows:
        blocks.append(f"text cut at {TEXT_PREVIEW} characters in {cut_rows} rows - use --full")
    return blocks


def _render_messages(header: List[str], messages: List[Dict[str, Any]], fields: List[str], full: bool,
                     next_commands: Sequence[str]) -> None:
    blocks = [*(axi_cli.ascii_text(line) for line in header), *_message_blocks(messages, fields, full)]
    blocks.append(axi_output.format_help(list(next_commands)))
    axi_output.write_blocks(sys.stdout, *blocks)


def _factory_of(answer: Any, given: str) -> str:
    value = answer.get("factory") if isinstance(answer, dict) else None
    return str(value) if value else given.strip()


# ---------------------------------------------------------------------------------------------------
# Reads.
# ---------------------------------------------------------------------------------------------------

def list_inboxes(fields: Optional[str], full: bool, json_output: bool) -> None:
    """Every factory's inbox: how many messages wait, and the last message."""
    chosen = _check_fields(fields, json_output, LIST_FIELDS, LIST_DEFAULT)
    answer = _get(f"{OWNER_ROUTE}/inbox", [axi_cli.CHECK_GATEWAY])
    rows = _rows(answer, "factories", "factory inboxes")
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    records, cut_rows, waiting = [], 0, 0
    for row in rows:
        last = row.get("lastMessage") if isinstance(row.get("lastMessage"), dict) else {}
        text, cut = _cut(last.get("text"), full)
        cut_rows += 1 if cut and "lastText" in chosen else 0
        waiting += row.get("waiting") if isinstance(row.get("waiting"), int) else 0
        records.append({
            "factory": row.get("factory"),
            "title": _one_line(row.get("title")),
            "waiting": row.get("waiting"),
            "lastState": last.get("stateLabel"),
            "lastSent": last.get("sentAtUtc"),
            "lastText": text or None,
        })
    blocks = [axi_output.format_count(len(records)), f"waiting: {waiting}"]
    if records:
        blocks.append(axi_output.render_list("factories", chosen, records))
    if cut_rows:
        blocks.append(f"lastText cut at {TEXT_PREVIEW} characters in {cut_rows} rows - use --full")
    blocks.append(axi_output.format_help([f"{_TOOL} thread <factory>", f"{_TOOL} summary <factory>"]))
    axi_output.write_blocks(sys.stdout, *blocks)


def thread(factory: str, fields: Optional[str], full: bool, json_output: bool) -> None:
    """One factory's messages, each with its trail."""
    chosen = _check_fields(fields, json_output, MESSAGE_FIELDS, MESSAGE_DEFAULT)
    answer = _get(_factory_route(factory), [f"{_TOOL} list"])
    messages = _rows(answer, "messages", "messages")
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    name = _factory_of(answer, factory)
    shown = axi_cli.bare(name, "<factory>")
    _render_messages([f"factory: {name}"], messages, chosen, full,
                     [f"{_TOOL} summary {shown}", f"{_TOOL} todo {shown}"])


def summary(factory: str, since: Optional[str], json_output: bool) -> None:
    """What came in since a time and the decision on each, as the Gateway built it from the log."""
    route = _factory_route(factory, "inbox/summary")
    if since is not None:
        if not since.strip():
            axi_cli.usage_error("--since needs a time, for example 2026-11-04T08:00:00Z.")
        route += "?" + urllib.parse.urlencode({"since": since.strip()})
    answer = _get(route, [f"{_TOOL} list"])
    items = _rows(answer, "items", "summary items")
    by_decision = _rows(answer, "byDecision", "decision counts")
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    name = _factory_of(answer, factory)
    blocks = [f"factory: {axi_cli.ascii_text(name)}",
              f"from: {axi_cli.ascii_text(str(answer.get('sinceUtc') or '-'))} "
              f"to: {axi_cli.ascii_text(str(answer.get('untilUtc') or '-'))}"]
    if isinstance(answer.get("line"), str) and answer["line"].strip():
        blocks.append(axi_cli.ascii_text(answer["line"].strip()))
    if by_decision:
        blocks.append("by decision: " + ", ".join(
            f"{axi_cli.ascii_text(str(d.get('label')))} {d.get('count')}" for d in by_decision))
    blocks.extend(_message_blocks(items, ["id", "state", "decision", "why", "text"], False, "items"))
    blocks.append(axi_output.format_help([f"{_TOOL} thread {axi_cli.bare(name, '<factory>')}"]))
    axi_output.write_blocks(sys.stdout, *blocks)


def todo(factory: str, json_output: bool) -> None:
    """The factory's to-do list."""
    answer = _get(_factory_route(factory, "todo"), [f"{_TOOL} list"])
    items = _rows(answer, "items", "to-do items")
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    records = [{
        "id": item.get("id"),
        "added": item.get("addedAtUtc"),
        "fromMessage": item.get("fromMessageId"),
        "text": _cut(item.get("text"), True)[0],
    } for item in items]
    name = _factory_of(answer, factory)
    blocks = [f"factory: {axi_cli.ascii_text(name)}", axi_output.format_count(len(records))]
    if records:
        blocks.append(axi_output.render_list("todo", ["id", "added", "fromMessage", "text"], records))
    blocks.append(axi_output.format_help([f"{_TOOL} thread {axi_cli.bare(name, '<factory>')}"]))
    axi_output.write_blocks(sys.stdout, *blocks)


# ---------------------------------------------------------------------------------------------------
# Seat commands.
# ---------------------------------------------------------------------------------------------------

def new_messages(fields: Optional[str], full: bool, json_output: bool) -> None:
    """The messages this session's factory has not yet received."""
    chosen = _check_fields(fields, json_output, MESSAGE_FIELDS, MESSAGE_DEFAULT)
    answer = _get(_session_route(), [f"{_TOOL} list"])
    messages = _rows(answer, "messages", "new messages")
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    header = [f"factory: {answer.get('factory') or '-'}"]
    next_commands = ([f"{_TOOL} receive <id> [<id>...]", f"{_TOOL} bundle --hand <id> [<id>...]"] if messages
                     else ["cc-devthrottle session done"])
    _render_messages(header, messages, chosen, full, next_commands)


def _pairs(values: Sequence[str], parts: int, flag: str, shape: str) -> List[List[str]]:
    out = []
    for value in values:
        pieces = value.split(":", parts - 1)
        if len(pieces) != parts or not all(p.strip() for p in pieces):
            axi_cli.usage_error(f"{flag} '{value}' is not {shape}.")
        out.append([p.strip() for p in pieces])
    return out


def _ids(message_ids: Sequence[str], verb: str) -> List[str]:
    ids = [m.strip() for m in message_ids if m.strip()]
    if not ids:
        axi_cli.usage_error(f"name at least one message to {verb}; the ids are in {_TOOL} new.")
    if len(set(ids)) != len(ids):
        axi_cli.usage_error(f"a message id is named more than once: {', '.join(sorted({i for i in ids if ids.count(i) > 1}))}.")
    return ids


def receive(message_ids: Sequence[str], merges: Sequence[str], links: Sequence[str], json_output: bool) -> None:
    """Claim messages and mark them Received, with merges and links."""
    ids = _ids(message_ids, "receive")
    merge_pairs = _pairs(merges, 2, "--merge", "<message-id>:<into-message-id>")
    link_triples = _pairs(links, 3, "--link", "<message-id>:<kind>:<ref>")
    named = set(ids)
    for message_id, into in merge_pairs:
        if message_id not in named or into not in named:
            axi_cli.usage_error(f"--merge {message_id}:{into} names a message not being received; "
                                "both must be among the ids passed.")
        if message_id == into:
            axi_cli.usage_error(f"--merge {message_id}:{into} merges a message into itself.")
    for message_id, _, _ in link_triples:
        if message_id not in named:
            axi_cli.usage_error(f"--link names message {message_id}, which is not being received.")
    body = {
        "ids": ids,
        "merges": [{"id": m, "into": i} for m, i in merge_pairs],
        "links": [{"id": m, "kind": k, "ref": r} for m, k, r in link_triples],
    }
    answer = _post(_session_route("receive"), body, [f"{_TOOL} new"])
    received = _rows(answer, "received", "received messages")
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    _render_messages(["Received."], received, MESSAGE_DEFAULT, False,
                     [f"{_TOOL} bundle --hand <id> [<id>...]"])


_DECIDE_NEXT = [f"{_TOOL} decide <id> delegated --session <session-id>",
                f"{_TOOL} decide <id> todo",
                f"{_TOOL} decide <id> declined --why \"<why>\""]


def bundle(message_ids: Sequence[str], hand: bool, json_output: bool) -> None:
    """Without --hand: the Boss reads its open bundles. With --hand: the Secretary hands one bundle to the Boss."""
    if not hand:
        if message_ids:
            axi_cli.usage_error("message ids are given only with --hand, which hands them to the Boss as one "
                                f"bundle. To read your open bundles run {_TOOL} bundle with no ids.")
        answer = _get(_session_route("bundle"), [f"{_TOOL} new"])
        bundles = _rows(answer, "bundles", "open bundles")
        if json_output:
            print(json.dumps(answer, indent=2))
            return
        blocks = [f"factory: {axi_cli.ascii_text(str(answer.get('factory') or '-'))}",
                  f"open bundles: {len(bundles)}"]
        for one in bundles:
            items = _rows(one, "items", f"messages in bundle {one.get('id')}")
            blocks.append(axi_cli.ascii_text(f"bundle: {one.get('id')}  handed {one.get('handedAtUtc') or '-'}"))
            blocks.extend(_message_blocks(items, MESSAGE_DEFAULT, True))
        blocks.append(axi_output.format_help(_DECIDE_NEXT if bundles
                                             else [f"{_TOOL} thread <factory>", "cc-devthrottle session done"]))
        axi_output.write_blocks(sys.stdout, *blocks)
        return
    ids = _ids(message_ids, "hand to the Boss")
    answer = _post(_session_route("bundle"), {"ids": ids}, [f"{_TOOL} new"])
    handed = answer.get("bundle") if isinstance(answer, dict) else None
    bundle_id = axi_cli.confirmed(handed, ["id"], "the hand-over of the bundle", [f"{_TOOL} new"])
    items = _rows(handed, "items", "messages in the bundle")
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    _render_messages([f"Handed to the Boss: bundle {bundle_id}."], items, MESSAGE_DEFAULT, False,
                     [f"{_TOOL} new", "cc-devthrottle session done"])


def decide(message_id: str, kind: str, session: Optional[str], why: Optional[str], json_output: bool) -> None:
    """The Boss's decision on one message."""
    decision = (kind or "").strip().lower()
    if decision not in DECISIONS:
        axi_cli.usage_error(f"'{kind}' is not a decision. Valid decisions: {', '.join(DECISIONS)}.")
    if not (message_id or "").strip():
        axi_cli.usage_error(f"name the message to decide; the ids are in {_TOOL} bundle.")
    has_session = session is not None and session.strip() != ""
    has_why = why is not None and why.strip() != ""
    if decision in NEEDS_SESSION and not has_session:
        axi_cli.usage_error("delegated needs --session <id>: the id of the session you started for it. "
                            "The Gateway accepts only a session this Boss started.")
    if decision not in NEEDS_SESSION and session is not None:
        axi_cli.usage_error(f"--session applies only to delegated, not to {decision}.")
    if decision in NEEDS_WHY and not has_why:
        axi_cli.usage_error(f"{decision} needs --why \"<why>\": the owner reads it in Your inbox.")
    if decision not in NEEDS_WHY and why is not None:
        axi_cli.usage_error(f"--why applies only to {', '.join(NEEDS_WHY)}, not to {decision}.")
    body: Dict[str, Any] = {"kind": decision}
    if has_session:
        body["session"] = session.strip()
    if has_why:
        body["why"] = why.strip()
    answer = _post(_session_route(f"{gateway.path_segment(message_id.strip())}/decide"), body,
                   [f"{_TOOL} bundle"])
    message = answer.get("message") if isinstance(answer, dict) else None
    decided = message.get("decision") if isinstance(message, dict) else None
    label = axi_cli.confirmed(decided, ["label"], f"the decision on message {message_id.strip()}",
                              [f"{_TOOL} bundle"])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    lines = [f"Decided: message {message_id.strip()} is {label}."]
    added = answer.get("todo")
    if decision == "todo":
        todo_id = axi_cli.confirmed(added, ["id"], f"the to-do item for message {message_id.strip()}",
                                    [f"{_TOOL} bundle"])
        lines.append(f"To-do item {todo_id} added.")
    axi_cli.write_lines(*lines)
    axi_cli.print_next([f"{_TOOL} bundle"])
