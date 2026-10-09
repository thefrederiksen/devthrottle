"""`cc-devthrottle factory status` - the Factories screen, as the owner sees it (issue #3685).

A factory's boss could not see its own factory marked FAILING: the screen's routes were the owner's, so the
boss rebuilt the rule by hand from the activity record and asked the owner to refresh. This command reads
the SAME two routes the screen reads, folded once on the Gateway (FactoriesScreenFold), and prints what
they say - the status word, why, what is waiting on the owner, and every failing and waiting item with
its row id - so a boss can act and then mark a resolved item handled with
`factory record --corrects <row id>`.

- `factory status` prints one line per factory from GET /gateway/factories.
- `factory status --factory <id>` prints one factory's page from GET /gateway/factories/<id>.
- `--json` prints the Gateway's answer verbatim: the screen's own data, nothing re-derived here.

Nothing here decides a word. Every word, order and item is the Gateway's; this module only lays it out.
"""

from __future__ import annotations

import json
import sys
import urllib.parse
from pathlib import Path
from typing import Any, Dict, List, Optional

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli  # noqa: E402

#: The Factories screen's routes, mapped only while factory agents are switched on.
LIST_ROUTE = "gateway/factories"

#: What a 404 from the list route means: the switch is off, so the Gateway cannot say it in its own words.
FEATURE_OFF = (
    "factory agents are switched off for this account, so the Gateway does not serve the Factories screen "
    "(it answered 404). A Gateway older than this command answers the same way."
)

_STATUS = "cc-devthrottle factory status --factory <id>"
_RECORD = ("cc-devthrottle factory record --factory <id> --agent <the item's seat> --outcome done "
           "--corrects <row id> --what \"Handled: <the evidence>\"")


def _reason(exc: gateway.GatewayError) -> str:
    return FEATURE_OFF if exc.status == 404 else str(exc)


def _page_route(factory: str) -> str:
    return f"{LIST_ROUTE}/{urllib.parse.quote(factory, safe='')}"


def _text(value: Any) -> str:
    return axi_cli.ascii_text(str(value)) if value is not None else ""


# ---------- the list ----------


def _list(json_output: bool) -> None:
    try:
        answer = gateway.get_json(LIST_ROUTE)
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), [axi_cli.CHECK_GATEWAY])

    rows = answer.get("rows") if isinstance(answer, dict) else None
    if not isinstance(rows, list):
        # Absent is not empty: an answer with no list of rows must never read as "every factory is running".
        axi_cli.fail("the Gateway answered with no list of factories; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    records = [
        {
            "id": r.get("id"),
            "status": r.get("statusWord"),
            "waiting": r.get("waitingText"),
            "line": r.get("statusLine"),
        }
        for r in rows
    ]
    blocks = [
        axi_output.format_count(len(records)),
        axi_output.render_list("factories", ["id", "status", "waiting", "line"], records),
    ]
    if answer.get("truncatedText"):
        blocks.append(axi_cli.ascii_text(f"Note: {answer['truncatedText']}"))
    blocks.append(axi_output.format_help([_STATUS]))
    axi_output.write_blocks(sys.stdout, *blocks)


# ---------- one factory's page ----------


def _item_lines(items: List[Dict[str, Any]], kind: str) -> List[str]:
    """One failing or waiting item per line, the row id first so it can be copied into --corrects."""
    lines: List[str] = []
    for item in items:
        row_id = item.get("id")
        head = _text(row_id) if row_id else "(no row)"
        word = item.get("word")
        parts = [head]
        if kind == "waiting" and word:
            parts.append(_text(word))
        parts.append(_text(item.get("by")))
        if item.get("subject"):
            parts.append(_text(item["subject"]))
        parts.append(_text(item.get("what")))
        line = "  " + " | ".join(p for p in parts if p)
        if item.get("link"):
            line += f" | {_text(item['link'])}"
        if not row_id and item.get("note"):
            # A schedule that could not start its run is not a row: it clears by itself, and the Gateway says how.
            line += f" | {_text(item['note'])}"
        lines.append(line)
    return lines


def _page(factory: str, json_output: bool) -> None:
    try:
        answer = gateway.get_json(_page_route(factory))
    except gateway.GatewayError as exc:
        if exc.status == 404 and "registered" in str(exc):
            axi_cli.fail(str(exc), ["cc-devthrottle factory list"])
        axi_cli.fail(_reason(exc), [axi_cli.CHECK_GATEWAY])

    if not isinstance(answer, dict) or not answer.get("statusWord"):
        # The word IS the answer: a page without one is not a status, and must never read as RUNNING.
        axi_cli.fail("the Gateway answered with no status word for this factory; this command will not report that as running.",
                     [axi_cli.CHECK_GATEWAY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    failures = answer.get("failures") or {}
    failing_items = failures.get("items") or [] if isinstance(failures, dict) else []
    waiting = answer.get("waiting") or {}
    waiting_items = waiting.get("items") or [] if isinstance(waiting, dict) else []

    head = [
        f"factory: {_text(answer.get('id'))}",
        f"title: {_text(answer.get('title'))}",
        f"status: {_text(answer.get('statusWord'))}",
        f"reason: {_text(answer.get('statusReason'))}",
        f"waiting on the owner: {len(waiting_items)}",
    ]
    blocks: List[str] = ["\n".join(head)]

    if failing_items:
        blocks.append("\n".join([f"failing[{len(failing_items)}] (row id | seat, when | subject | what):"] + _item_lines(failing_items, "failing")))
    if waiting_items:
        blocks.append("\n".join([f"waiting[{len(waiting_items)}] (row id | asked or escalated | seat, when | subject | what):"] + _item_lines(waiting_items, "waiting")))
    if not failing_items and not waiting_items:
        blocks.append("failing[0]\nwaiting[0]")
    if answer.get("truncatedText"):
        blocks.append(axi_cli.ascii_text(f"Note: {answer['truncatedText']}"))

    next_commands = [_RECORD, "cc-devthrottle factory status --factory " + axi_cli.bare(answer.get("id"), "<id>")] \
        if (failing_items or waiting_items) else ["cc-devthrottle factory status"]
    blocks.append(axi_output.format_help(next_commands))
    axi_output.write_blocks(sys.stdout, *blocks)


def status(factory: Optional[str], json_output: bool) -> None:
    """The Factories screen: every factory's status word, or one factory's page with its items."""
    if factory is None:
        _list(json_output)
    else:
        _page(factory.strip(), json_output)
