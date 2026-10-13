"""`cc-devthrottle factory email` - the factory email (Factory Control, step 4).

- `factory email status` - is this account sent the factory email (the 08:00 report, and the 16:00 / midnight checks).
- `factory email on` / `factory email off` - switch it. It ships off.
- `factory email list [--factory ID] [--kind morning-report|check] [--count N] [--all] [--json]` - the kept emails,
  newest first (sent ones; every slot with --all); the factory page reads the same list.
- `factory email show <id> [--json]` - one kept email with its HTML exactly as sent.

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
EMAILS_ROUTE = "gateway/factory-emails"
KINDS = ("morning-report", "check")

#: What a 404 with no words of its own means here: a Gateway older than this command.
OLD_GATEWAY = "the Gateway does not know the factory email yet (it answered 404 with no reason) - it is older than this command."

_EMAIL = "cc-devthrottle factory email"
_STATUS = "cc-devthrottle factory email status"
_LIST = "cc-devthrottle factory email list"
_FIELDS = ["id", "slot", "state", "sent", "title", "factories", "problems", "detail"]


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
    next_commands = [f"{_EMAIL} off" if enabled else f"{_EMAIL} on", _LIST]
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


def list_emails(factory: Optional[str], kind: Optional[str], count: Optional[int], all_states: bool,
                json_output: bool) -> None:
    """The kept factory emails, newest first: sent ones, or every slot with --all."""
    query: Dict[str, str] = {}
    if factory is not None and factory.strip():
        query["factory"] = factory.strip()
    if kind is not None:
        if kind.strip().lower() not in KINDS:
            axi_cli.usage_error(f"'{kind}' is not a factory email kind. Use: {', '.join(KINDS)}.")
        query["kind"] = kind.strip().lower()
    if count is not None:
        if count < 1 or count > 100:
            axi_cli.usage_error("--count must be between 1 and 100.")
        query["limit"] = str(count)
    if all_states:
        query["state"] = "all"
    route = EMAILS_ROUTE + (f"?{urllib.parse.urlencode(query)}" if query else "")
    try:
        answer = gateway.get_json(route)
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), [axi_cli.CHECK_GATEWAY])

    rows = answer.get("emails") if isinstance(answer, dict) else None
    if not isinstance(rows, list) or not isinstance(answer.get("count"), int):
        # Absent is not empty: an answer with no list must never read as "nothing kept".
        axi_cli.fail("the Gateway answered with no list of kept emails; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    records = [
        {
            "id": r.get("id"),
            "slot": r.get("slotKey"),
            "state": r.get("state"),
            "sent": r.get("sentUtc") or "-",
            "title": r.get("title") or "-",
            "factories": ",".join(r.get("factories") or []) or "-",
            "problems": r.get("problemCount"),
            "detail": r.get("detail") or "-",
        }
        for r in rows
    ]
    blocks = [axi_output.format_count(len(records))]
    if records:
        blocks.append(axi_output.render_list("emails", _FIELDS, records))
    next_commands = [f"{_EMAIL} show <id>"] if records else [f"{_LIST} --all", _STATUS]
    blocks.append(axi_output.format_help(next_commands))
    axi_output.write_blocks(sys.stdout, *blocks)


def show(email_id: str, json_output: bool) -> None:
    """One kept factory email, with its HTML exactly as sent."""
    if not (email_id or "").strip():
        axi_cli.usage_error(f"name the email to show: {_EMAIL} show <id> (the ids are in {_LIST}).")
    try:
        answer = gateway.get_json(f"{EMAILS_ROUTE}/{urllib.parse.quote(email_id.strip(), safe='')}")
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), [_LIST])
    if not isinstance(answer, dict) or not isinstance(answer.get("html"), str):
        axi_cli.fail("the Gateway answered without the email's body, so there is nothing to show.", [_LIST])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    lines = [
        f"id: {_text(answer.get('id'))}",
        f"slot: {_text(answer.get('slotKey'))}",
        f"state: {_text(answer.get('state'))}",
        f"title: {_text(answer.get('title')) or '-'}",
        f"factories: {_text(','.join(answer.get('factories') or [])) or '-'}",
    ]
    axi_output.write_blocks(sys.stdout, "\n".join(lines), "html:\n" + answer["html"], axi_output.format_help([_LIST]))
