"""`cc-devthrottle schedule admin-runs`: every account's scheduled runs, for the administrator (issue #4403).

Before this the only reader of a schedule's runs was `schedule runs <id>`, which answers only the account that owns
the schedule - so "my schedule is erroring out" from a customer could not be answered without that customer's own
credentials. This reads `GET /gateway/admin/cron-runs` on the administrator service token in ADMIN_SERVICE_TOKEN:

    cc-secrets run admin-service-token -- cc-devthrottle schedule admin-runs --email <address> --failed

Each run shows its account, schedule, machine, when it fired, how it ended and the reason the Gateway recorded. The
Gateway's own side of the same trouble - the failed runs, refused session keys and refused registrations it reported
under the account - is read with `cc-devthrottle errors list --email <address> --component gateway`.

Output follows docs/axi-standard.md: a count line, a compact list, long reasons truncated with a size hint, help[]
lines, and `--json` for the Gateway's answer unchanged.
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

from . import axi_cli, errors_ops, usage_errors  # noqa: E402

ADMIN_RUNS_PATH = "gateway/admin/cron-runs"
ADMIN_RUNS_COMMAND = "cc-secrets run admin-service-token -- cc-devthrottle schedule admin-runs --email <address> --failed"

RUN_FIELDS = (
    "fired",
    "account",
    "job",
    "name",
    "machine",
    "status",
    "result",
    "problem",
    "failed",
    "reason",
    "session",
    "run",
    "scheduled",
    "resolved",
)
RUN_DEFAULT_FIELDS = ("fired", "account", "name", "machine", "result", "problem", "reason")

REASON_PREVIEW = 160


def _row(run: Dict[str, Any], full: bool) -> Dict[str, Any]:
    reason = str(run.get("reason") or "")
    if not full and len(reason) > REASON_PREVIEW:
        reason = f"{reason[:REASON_PREVIEW]}... (truncated, {len(reason)} chars total - use --full)"
    return {
        "fired": str(run.get("fired_utc") or ""),
        "account": str(run.get("account") or ""),
        "job": str(run.get("job_id") or ""),
        "name": axi_output.escape_ascii(str(run.get("job_name") or "")),
        "machine": axi_output.escape_ascii(str(run.get("machine") or "")),
        "status": str(run.get("status") or ""),
        "result": str(run.get("result") or ""),
        "problem": str(run.get("problem") or ""),
        "failed": "yes" if run.get("failed") else "no",
        "reason": axi_output.escape_ascii(reason),
        "session": str(run.get("session_id") or ""),
        "run": str(run.get("run_id") or ""),
        "scheduled": str(run.get("scheduled_utc") or ""),
        "resolved": str(run.get("resolved_utc") or ""),
    }


def list_admin_runs(
    *,
    json_output: bool,
    account: Optional[str],
    email: Optional[str],
    job: Optional[str],
    since: Optional[str],
    until: Optional[str],
    failed: bool,
    limit: int,
    fields: Optional[str],
    full: bool,
    gateway_url: Optional[str],
) -> None:
    # Usage errors first: a bad flag is the caller's to fix whatever the Gateway holds.
    if json_output and fields is not None:
        usage_errors.usage_error(axi_cli.FIELDS_WITH_JSON)
    if account and email:
        usage_errors.usage_error("--account and --email both name the account. Give one of them.")
    if limit < 1 or limit > 500:
        usage_errors.usage_error("--limit must be from 1 to 500.")
    chosen = usage_errors.parse_fields(fields, RUN_FIELDS, RUN_DEFAULT_FIELDS)

    params: Dict[str, str] = {"limit": str(limit), "outcome": "failed" if failed else "all"}
    for key, value in (("account", account), ("email", email), ("job", job), ("since", since), ("until", until)):
        if value:
            params[key] = value

    bearer = errors_ops._admin_token(
        "reading every account's scheduled runs",
        ADMIN_RUNS_COMMAND,
        "cc-devthrottle schedule runs <schedule-id>",
    )
    try:
        answer = gateway.get_json(f"{ADMIN_RUNS_PATH}?{urlencode(params)}", bearer=bearer, base_url=gateway_url)
    except gateway.GatewayError as err:
        axi_cli.fail(axi_output.escape_ascii(str(err)), [axi_cli.CHECK_GATEWAY])

    if not isinstance(answer, dict) or not isinstance(answer.get("runs"), list):
        axi_cli.fail(
            "the Gateway's answer has no list of runs. This tool will not read that as none. "
            "A Gateway older than this command does not have the route.",
            [axi_cli.CHECK_GATEWAY],
        )

    if json_output:
        print(json.dumps(answer, indent=2))
        return

    runs: List[Dict[str, Any]] = answer["runs"]
    rows = [_row(r, full) for r in runs]
    scope = f"account {answer.get('account')}" if answer.get("account") else "every account"
    blocks = [
        f"scope: {scope}, {'failed runs' if failed else 'every run'}, fired {answer.get('since_utc')} to {answer.get('until_utc')}",
        axi_output.format_count(len(rows)),
        axi_output.render_list("runs", chosen, [{f: row[f] for f in chosen} for row in rows]),
    ]
    if not rows:
        blocks.append("none match")
    axi_output.write_blocks(sys.stdout, *blocks)

    who = f"--email {email}" if email else f"--account {account}" if account else "--all-accounts"
    next_steps = ["cc-devthrottle schedule admin-runs --json"]
    if len(rows) >= limit:
        next_steps.append(f"cc-devthrottle schedule admin-runs --limit {min(500, limit * 2)}")
    if not failed:
        next_steps.append("cc-devthrottle schedule admin-runs --failed")
    next_steps.append(f"cc-devthrottle errors list {who} --component gateway --since 7d")
    axi_cli.print_next(next_steps)
