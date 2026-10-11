"""`cc-devthrottle factory email` - the factory email (Factory Control, step 4).

- `factory email status` - is this account sent the factory email (the 08:00 report, and the 16:00 / midnight checks).
- `factory email on` / `factory email off` - switch it. It ships off.
- `factory email reports [--kind morning-report|check] [--count N] [--html] [--json]` - the kept emails, newest
  first, each as it was composed: the factory page reads the same list to show yesterday's report again.

Nothing here decides anything. When an email is sent, what it says and whether a slot had anything to send are
the Gateway's.
"""

from __future__ import annotations

import json
import sys
import urllib.parse
from pathlib import Path
from typing import Any, Dict, Optional

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli  # noqa: E402

SWITCH_ROUTE = "gateway/factory-email"
REPORTS_ROUTE = "gateway/factory-email/reports"
KINDS = ("morning-report", "check")

#: What a 404 with no words of its own means here: a Gateway older than this command.
OLD_GATEWAY = "the Gateway does not know the factory email yet (it answered 404 with no reason) - it is older than this command."

_EMAIL = "cc-devthrottle factory email"
_STATUS = "cc-devthrottle factory email status"
_REPORTS = "cc-devthrottle factory email reports"
_FIELDS = ["slot", "kind", "state", "subject", "problems", "sent", "detail"]


def _text(value: Any) -> str:
    return axi_cli.ascii_text(str(value)) if value is not None else ""


def _reason(exc: gateway.GatewayError) -> str:
    # A 404 that carried no answer of its own is a Gateway that has no such route; one with an answer says why.
    return OLD_GATEWAY if exc.status == 404 and exc.body is None else str(exc)


def _enabled_of(answer: Any) -> bool:
    """The switch's state from the Gateway's answer; an answer without a boolean is a failure, never a guess."""
    value = answer.get("enabled") if isinstance(answer, dict) else None
    if not isinstance(value, bool):
        axi_cli.fail("the Gateway answered without saying whether the factory email is on, so this command will "
                     "not guess.", [axi_cli.CHECK_GATEWAY])
    return value


def _print_switch(enabled: bool, changed: Optional[str], json_output: bool) -> None:
    if json_output:
        print(json.dumps({"enabled": enabled}, indent=2))
        return
    lines = []
    if changed:
        lines.append(changed)
    lines.append(f"factory email: {'on' if enabled else 'off'}")
    lines.append("sent at 08:00 every day, and at 16:00 and midnight only when something went wrong, to the account "
                 "that set up the factories" if enabled else "nothing is sent to this account")
    next_commands = [f"{_EMAIL} off" if enabled else f"{_EMAIL} on", _REPORTS]
    axi_output.write_blocks(sys.stdout, "\n".join(lines), axi_output.format_help(next_commands))


def switch(state: Optional[str], json_output: bool) -> None:
    """Show the switch, or set it with `on` / `off`."""
    if state is None:
        try:
            answer = gateway.get_json(SWITCH_ROUTE)
        except gateway.GatewayError as exc:
            axi_cli.fail(_reason(exc), [axi_cli.CHECK_GATEWAY])
        _print_switch(_enabled_of(answer), None, json_output)
        return

    wanted = state.strip().lower()
    if wanted not in ("on", "off"):
        axi_cli.usage_error(f"'{state}' is not a switch position. Use: {_EMAIL} on, or {_EMAIL} off.")
    try:
        answer = gateway.put_json(SWITCH_ROUTE, {"enabled": wanted == "on"})
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), [_STATUS])
    enabled = _enabled_of(answer)
    if enabled != (wanted == "on"):
        axi_cli.fail(f"the Gateway's answer says the factory email is {'on' if enabled else 'off'}, not {wanted}, "
                     "so it was not switched.", [_STATUS])
    _print_switch(enabled, f"Switched the factory email {wanted}.", json_output)


def reports(kind: Optional[str], count: Optional[int], html: bool, json_output: bool) -> None:
    """The account's kept factory emails, newest first."""
    query: Dict[str, str] = {}
    if kind is not None:
        if kind.strip().lower() not in KINDS:
            axi_cli.usage_error(f"'{kind}' is not a factory email kind. Use: {', '.join(KINDS)}.")
        query["kind"] = kind.strip().lower()
    if count is not None:
        if count < 1 or count > 100:
            axi_cli.usage_error("--count must be between 1 and 100.")
        query["limit"] = str(count)
    if html:
        query["html"] = "true"
    route = REPORTS_ROUTE + (f"?{urllib.parse.urlencode(query)}" if query else "")
    try:
        answer = gateway.get_json(route)
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), [axi_cli.CHECK_GATEWAY])

    rows = answer.get("reports") if isinstance(answer, dict) else None
    if not isinstance(rows, list) or not isinstance(answer.get("count"), int):
        # Absent is not empty: an answer with no list must never read as "nothing kept".
        axi_cli.fail("the Gateway answered with no list of kept emails; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    records = [
        {
            "slot": r.get("slotKey"),
            "kind": r.get("kind"),
            "state": r.get("state"),
            "subject": r.get("subject") or "-",
            "problems": r.get("problemCount"),
            "sent": r.get("sentAtUtc") or "-",
            "detail": r.get("detail") or "-",
        }
        for r in rows
    ]
    blocks = [axi_output.format_count(len(records))]
    if records:
        blocks.append(axi_output.render_list("reports", _FIELDS, records))
    if html:
        for r in rows:
            if r.get("html"):
                blocks.append(f"html of {_text(r.get('slotKey'))}:\n{r.get('html')}")
    blocks.append(axi_output.format_help([f"{_REPORTS} --html --count 1", _STATUS]))
    axi_output.write_blocks(sys.stdout, *blocks)
