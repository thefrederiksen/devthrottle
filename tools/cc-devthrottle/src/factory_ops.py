"""The factory activity record for cc-devthrottle (Website Business Factory, product track).

Two verbs:

- `factory record` appends one row to the Gateway's append-only factory activity record and prints the
  new row's id. A business tool calls it BEFORE it acts ("write first, then act"), so the one defect
  that matters most here is a silent success: whenever the row was NOT written - the factory agents
  switch is off, the Gateway refused the row, the Gateway could not be reached - this exits non-zero
  and says why. There is no path through this module that exits 0 without an id the Gateway returned.
- `factory activity` reads rows back, newest first unless asked otherwise.
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

#: The route the Gateway maps only while factory agents are switched on.
ROUTE = "gateway/factory/activity"

#: The outcome words the Gateway accepts. The Gateway is the authority and refuses anything else with the
#: list; this copy exists only so `--help` can show it.
OUTCOMES = (
    "started", "allowed", "asked", "blocked", "escalated", "done", "sent-back", "nothing-to-do", "paused",
    "skipped", "failed",
)

#: What a 404 from this route means. The route is simply not mapped while the switch is off, so the
#: Gateway cannot say it in its own words - this sentence says it for it.
FEATURE_OFF = (
    "factory agents are switched off on this Gateway, so it does not serve the factory activity record "
    "(the Gateway answered 404). The owner turns it on with \"factoryAgents\": {\"enabled\": true} in the "
    "Gateway's config.json. A Gateway older than this command answers the same way."
)

_ACTIVITY = "cc-devthrottle factory activity"


def _not_written(reason: str) -> None:
    """Exit 1: the row was not written. Every failure of `factory record` ends here."""
    axi_cli.fail(f"Not recorded: {reason}", [axi_cli.CHECK_GATEWAY], label="Error:")


def _gateway_reason(exc: gateway.GatewayError) -> str:
    if exc.status == 404:
        return FEATURE_OFF
    return str(exc)


def record(
    factory: str,
    agent: str,
    outcome: str,
    what: str,
    subject: Optional[str],
    link: Optional[str],
    version: Optional[str],
    corrects: Optional[str],
    json_output: bool,
) -> None:
    """Append one row and print its id. Exits non-zero whenever the row was not written."""
    body: Dict[str, Any] = {
        "factory": factory,
        "factoryAgent": agent,
        "outcome": outcome,
        "what": what,
    }
    if subject is not None:
        body["subject"] = subject
    if link is not None:
        body["link"] = link
    if version is not None:
        body["factoryAgentVersion"] = version
    if corrects is not None:
        body["correctsId"] = corrects

    try:
        answer = gateway.post_json(ROUTE, body)
    except gateway.GatewayError as exc:
        _not_written(_gateway_reason(exc))
        return

    row_id = answer.get("id") if isinstance(answer, dict) else None
    if not isinstance(row_id, str) or not row_id.strip():
        # A 2xx with no id is not proof of a write, and this command never reports one it cannot show.
        _not_written(
            "the Gateway answered without the id of a recorded row, so this command cannot show the row "
            "was written. Treat it as not recorded."
        )
        return

    if json_output:
        print(json.dumps(answer, indent=2))
        return
    axi_cli.write_lines(row_id)
    axi_cli.print_next([f"{_ACTIVITY} --factory {axi_cli.bare(factory, '<factory>')}"])


def activity(
    factory: Optional[str],
    agent: Optional[str],
    outcome: Optional[str],
    since: Optional[str],
    until: Optional[str],
    oldest_first: bool,
    offset: int,
    limit: int,
    json_output: bool,
) -> None:
    """Read rows back from the record."""
    params: List[tuple] = []
    for key, value in (("factory", factory), ("agent", agent), ("outcome", outcome), ("from", since), ("to", until)):
        if value:
            params.append((key, value))
    params.append(("order", "oldest" if oldest_first else "newest"))
    params.append(("offset", str(offset)))
    params.append(("limit", str(limit)))
    path = f"{ROUTE}?{urllib.parse.urlencode(params)}"

    try:
        page = gateway.get_json(path)
    except gateway.GatewayError as exc:
        axi_cli.fail(_gateway_reason(exc), [axi_cli.CHECK_GATEWAY])
        return

    rows = page.get("rows") if isinstance(page, dict) else None
    if not isinstance(rows, list):
        # Absent is not empty: an answer with no list of rows must never read as "nothing happened".
        axi_cli.fail(
            "the Gateway answered with no list of rows; this command will not report that as no activity.",
            [axi_cli.CHECK_GATEWAY],
        )
        return

    if json_output:
        print(json.dumps(page, indent=2))
        return

    if not rows:
        axi_cli.write_lines("No factory activity matches.")
        return
    lines = []
    for row in rows:
        subject = row.get("subject")
        corrects = row.get("correctsId")
        parts = [
            axi_output.format_value(row.get("occurredUtc")),
            axi_output.format_value(row.get("factory")),
            axi_output.format_value(row.get("factoryAgent")),
            axi_output.format_value(row.get("outcome")),
            axi_cli.ascii_text(str(row.get("what") or "")),
        ]
        if subject:
            parts.append(f"subject: {axi_cli.ascii_text(str(subject))}")
        if corrects:
            parts.append(f"corrects {corrects}")
        parts.append(f"id {row.get('id')}")
        lines.append("  ".join(parts))
    axi_cli.write_lines(*lines)
    if page.get("hasMore"):
        axi_cli.write_lines(f"More rows match: add --offset {offset + len(rows)} to see the next page.")
