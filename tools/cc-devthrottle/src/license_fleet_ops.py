"""`cc-devthrottle license sessions`: what is using a Claude account, read from the Gateway.

The other `license` commands work on THIS machine's own sign-ins. This one asks the Gateway, which records every
session of every connected Director once a minute (the usage-by-session mandate of 2026-10-20), and answers in ONE
unit: percent of the account's weekly limit. So an agent - the Fleet Manager first - can say which sessions are
using the license, how fast, and how much so far, without the owner.

Every number and sentence is the Gateway's (CLAUDE.md rule 7); this command only lays them out. A percent the
Gateway could not measure yet is printed as "not measured", never as a zero.

Output follows docs/axi-standard.md: a few header lines, a count line, a compact list, help[] lines, and `--json`
for the Gateway's answer unchanged.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional
from urllib.parse import urlencode

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli, usage_errors  # noqa: E402

USAGE_PATH = "gateway/licenses/usage"

BY_CHOICES = ("session", "top", "mission")

SESSION_FIELDS = (
    "number", "name", "state", "rate-now", "last-4h", "so-far", "running", "average", "dollars", "model", "machine",
    "top-level", "mission", "id",
)
SESSION_DEFAULT_FIELDS = ("number", "name", "state", "rate-now", "last-4h", "so-far", "running", "average")

GROUP_FIELDS = ("label", "sessions", "live", "rate-now", "last-4h", "so-far", "dollars", "id")
GROUP_DEFAULT_FIELDS = ("label", "sessions", "live", "rate-now", "last-4h", "so-far")

_SESSIONS = "cc-devthrottle license sessions"
NOT_MEASURED = "not measured"


def _percent(value: Any) -> str:
    if value is None:
        return NOT_MEASURED
    number = float(value)
    return f"{number:.3f}%" if number < 0.1 else f"{number:.2f}%"


def _rate(value: Any) -> str:
    return NOT_MEASURED if value is None else f"{_percent(value)}/h"


def _dollars(value: Any) -> str:
    return "" if value is None else f"${float(value):.2f}"


def _hours(value: Any) -> str:
    hours = float(value or 0)
    return f"{hours * 60:.0f}m" if hours < 1 else f"{hours:.1f}h"


def _session_row(s: Dict[str, Any], top_labels: Dict[str, str]) -> Dict[str, Any]:
    number = s.get("number")
    return {
        "number": "" if number is None else str(number),
        "name": axi_output.escape_ascii(str(s.get("name") or "")),
        "state": str(s.get("stateText") or ""),
        "rate-now": _rate(s.get("rateNowPercentPerHour")),
        "last-4h": _percent(s.get("last4HoursPercent")),
        "so-far": _percent(s.get("soFarPercent")),
        "running": _hours(s.get("runningHours")),
        "average": _rate(s.get("averagePercentPerHour")),
        "dollars": _dollars(s.get("dollarsSoFar")),
        "model": axi_output.escape_ascii(str(s.get("model") or "")),
        "machine": axi_output.escape_ascii(str(s.get("machineName") or "")),
        "top-level": axi_output.escape_ascii(top_labels.get(str(s.get("topLevelSessionId") or ""), "")),
        "mission": axi_output.escape_ascii(str(s.get("missionName") or "")),
        "id": str(s.get("sessionId") or ""),
    }


def _group_row(g: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "label": axi_output.escape_ascii(str(g.get("label") or "")),
        "sessions": len(g.get("sessionIds") or []),
        "live": len(g.get("liveSessionIds") or []),
        "rate-now": _rate(g.get("rateNowPercentPerHour")),
        "last-4h": _percent(g.get("last4HoursPercent")),
        "so-far": _percent(g.get("soFarPercent")),
        "dollars": _dollars(g.get("dollarsSoFar")),
        "id": str(g.get("id") or ""),
    }


def read_usage(account: Optional[str], closed: bool) -> Dict[str, Any]:
    """The Gateway's answer for one account (default: the one in use). Fails loudly on anything else."""
    params: Dict[str, str] = {}
    if account:
        params["account"] = account
    if closed:
        params["closed"] = "true"
    path = USAGE_PATH + (f"?{urlencode(params)}" if params else "")
    try:
        answer = gateway.get_json(path)
    except gateway.GatewayError as err:
        axi_cli.fail(axi_output.escape_ascii(str(err)), [axi_cli.CHECK_GATEWAY, f"{_SESSIONS} --account <email>"])
    if not isinstance(answer, dict) or not isinstance(answer.get("sessions"), list):
        axi_cli.fail(
            "the Gateway's answer has no list of sessions. This tool will not read that as none. "
            "A Gateway older than this command does not have the route.",
            [axi_cli.CHECK_GATEWAY],
        )
    return answer


def sessions(account: Optional[str], by: str, closed: bool, fields: Optional[str], json_output: bool) -> None:
    """Which sessions use the account, how fast and how much so far, in percent of its weekly limit."""
    if json_output and fields is not None:
        axi_cli.usage_error(axi_cli.FIELDS_WITH_JSON)
    if by not in BY_CHOICES:
        axi_cli.usage_error(f"unknown --by '{axi_output.escape_ascii(by)}'. Valid: {', '.join(BY_CHOICES)}")
    grouped = by != "session"
    chosen = usage_errors.parse_fields(fields, GROUP_FIELDS if grouped else SESSION_FIELDS,
                                       GROUP_DEFAULT_FIELDS if grouped else SESSION_DEFAULT_FIELDS)

    answer = read_usage(account, closed)
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    email = answer.get("accountEmail")
    header = [f"account: {email or '(none)'}" + (" (in use)" if answer.get("accountInUse") else "")]
    week = answer.get("weekUsedPercent")
    if week is not None:
        header.append(f"week: {float(week):g}% used, resets {answer.get('weekResetsAtUtc')}")
    calibration = answer.get("calibration") or {}
    header.append("calibration: " + axi_output.escape_ascii(str(calibration.get("text") or "")))
    header.append("burn: " + axi_output.escape_ascii(str(answer.get("burnText") or "")))

    session_rows: List[Dict[str, Any]] = answer["sessions"]
    if grouped:
        groups: List[Dict[str, Any]] = answer.get("topLevel" if by == "top" else "missions") or []
        name = "top-level" if by == "top" else "missions"
        rows = [_group_row(g) for g in groups]
    else:
        top_labels = {str(g.get("id")): str(g.get("label") or "") for g in answer.get("topLevel") or []}
        name = "sessions"
        rows = [_session_row(s, top_labels) for s in session_rows]

    blocks = ["\n".join(header), axi_output.format_count(len(rows))]
    blocks.append(axi_output.render_list(name, chosen, [{f: r[f] for f in chosen} for r in rows]))
    if not rows and answer.get("emptyText"):
        blocks.append(axi_output.escape_ascii(str(answer["emptyText"])))

    help_lines = []
    if not grouped:
        help_lines.append(f"{_SESSIONS} --by top")
    help_lines.append(f"{_SESSIONS} --closed")
    others = [a.get("accountEmail") for a in answer.get("accounts") or [] if a.get("accountEmail") != email]
    if others:
        help_lines.append(f"{_SESSIONS} --account {others[0]}")
    help_lines.append(f"{_SESSIONS} --json")
    blocks.append(axi_output.format_help(help_lines))
    axi_output.write_blocks(sys.stdout, *blocks)
