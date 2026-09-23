"""`cc-devthrottle errors list`: the errors Directors, launchers and installers reported (issue #3311).

Before this, an error on a user's machine lived only in the log files on their disk. Now every Director
and launcher sends the errors it logs to its Gateway, and every installer sends a failed step; this
command reads them back so an agent can look first instead of asking the owner, or the user, for logs.

Two reads, chosen explicitly - never one standing in for the other:

  errors list                 - THIS account's Director and launcher errors, on this session's own key.
  errors list --all-accounts  - every account's errors and every installer failure, on the
                                administrator service token in ADMIN_SERVICE_TOKEN. Run it as
                                `cc-secrets run admin-service-token -- cc-devthrottle errors list --all-accounts`.
                                --account and --email imply it.

Output follows docs/axi-standard.md: a count line, a compact list, truncated messages with a size hint,
help[] lines, and `--json` for the Gateway's answer unchanged.
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional
from urllib.parse import urlencode

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli, usage_errors  # noqa: E402

ADMIN_TOKEN_VARIABLE = "ADMIN_SERVICE_TOKEN"
ADMIN_TOKEN_COMMAND = "cc-secrets run admin-service-token -- cc-devthrottle errors list --all-accounts"

ACCOUNT_PATH = "gateway/director-errors"
ADMIN_PATH = "gateway/admin/director-errors"

COMPONENTS = ("director", "launcher", "install")

ERROR_FIELDS = (
    "time",
    "component",
    "machine",
    "version",
    "os",
    "arch",
    "source",
    "kind",
    "exception",
    "repeats",
    "message",
    "account",
    "device",
    "step",
)
ERROR_DEFAULT_FIELDS = ("time", "component", "machine", "message")

MESSAGE_PREVIEW = 160


def _usage_error(message: str) -> None:
    usage_errors.usage_error(message)


def _row(record: Dict[str, Any], full: bool) -> Dict[str, Any]:
    message = str(record.get("message") or "")
    if not full and len(message) > MESSAGE_PREVIEW:
        message = f"{message[:MESSAGE_PREVIEW]}... (truncated, {len(message)} chars total - use --full)"
    return {
        "time": str(record.get("received_utc") or ""),
        "component": str(record.get("component") or ""),
        "machine": str(record.get("machine_id") or ""),
        "version": str(record.get("product_version") or ""),
        "os": str(record.get("os") or ""),
        "arch": str(record.get("arch") or ""),
        "source": str(record.get("source") or ""),
        "kind": str(record.get("kind") or ""),
        "exception": str(record.get("exception_type") or ""),
        "repeats": int(record.get("repeat_count") or 1),
        "message": axi_output.escape_ascii(message),
        "account": str(record.get("account") or ""),
        "device": str(record.get("device") or ""),
        "step": str(record.get("step") or ""),
    }


def list_errors(
    *,
    json_output: bool,
    all_accounts: bool,
    account: Optional[str],
    email: Optional[str],
    since: Optional[str],
    until: Optional[str],
    machine: Optional[str],
    version: Optional[str],
    component: Optional[str],
    limit: int,
    fields: Optional[str],
    full: bool,
    gateway_url: Optional[str],
) -> None:
    # Usage errors first: a bad flag is the caller's to fix whatever the Gateway holds.
    if json_output and fields is not None:
        _usage_error(axi_cli.FIELDS_WITH_JSON)
    if account and email:
        _usage_error("--account and --email both name the account. Give one of them.")
    if component is not None and component not in COMPONENTS:
        _usage_error(f"unknown --component '{axi_output.escape_ascii(component)}'. Valid components: {', '.join(COMPONENTS)}")
    if limit < 1 or limit > 500:
        _usage_error("--limit must be from 1 to 500.")
    admin = all_accounts or bool(account) or bool(email)
    if component == "install" and not admin:
        _usage_error("installer failures belong to no account yet, so they are read with --all-accounts.")
    if gateway_url and not admin:
        # This account's read goes out on THIS session's key, and that key belongs to CC_GATEWAY_URL alone.
        # Sending it to a host somebody typed would hand the key to that host, for a read the host would
        # refuse anyway. --gateway exists for the administrator read, which carries its own token.
        _usage_error("--gateway is only for --all-accounts, --account or --email: this account's read uses "
                     "this session's key, which is only ever sent to CC_GATEWAY_URL.")
    chosen = usage_errors.parse_fields(fields, ERROR_FIELDS, ERROR_DEFAULT_FIELDS)

    params: Dict[str, str] = {"limit": str(limit)}
    for key, value in (("since", since), ("until", until), ("machine", machine), ("version", version),
                       ("component", component), ("account", account), ("email", email)):
        if value:
            params[key] = value
    path = f"{ADMIN_PATH if admin else ACCOUNT_PATH}?{urlencode(params)}"

    bearer: Optional[str] = None
    if admin:
        bearer = os.environ.get(ADMIN_TOKEN_VARIABLE, "").strip()
        if not bearer:
            axi_cli.fail(
                f"reading every account's errors needs the administrator service token in {ADMIN_TOKEN_VARIABLE}, "
                "and it is not set.",
                [ADMIN_TOKEN_COMMAND, "cc-devthrottle errors list"],
            )

    try:
        answer = gateway.get_json(path, bearer=bearer, base_url=gateway_url)
    except gateway.GatewayError as err:
        axi_cli.fail(axi_output.escape_ascii(str(err)), [axi_cli.CHECK_GATEWAY])

    if not isinstance(answer, dict) or not isinstance(answer.get("errors"), list):
        axi_cli.fail(
            "the Gateway's answer has no list of errors. This tool will not read that as none. "
            "A Gateway older than this command does not have the route.",
            [axi_cli.CHECK_GATEWAY],
        )

    if json_output:
        print(json.dumps(answer, indent=2))
        return

    records: List[Dict[str, Any]] = answer["errors"]
    total = int(answer.get("total_matched") or 0)
    rows = [_row(r, full) for r in records]
    by_component = answer.get("by_component") or {}

    blocks = [
        f"scope: {'every account' if admin else 'this account'}, "
        f"{answer.get('since_utc')} to {answer.get('until_utc')} (kept {answer.get('retention_days')} days)",
        axi_output.format_count(len(rows), total=total),
    ]
    if by_component:
        blocks.append(
            "matched by component: " + ", ".join(f"{k} {by_component[k]}" for k in sorted(by_component))
        )
    blocks.append(axi_output.render_list("errors", chosen, [{f: row[f] for f in chosen} for row in rows]))
    if not rows:
        blocks.append("none match")
    axi_output.write_blocks(sys.stdout, *blocks)

    next_steps = ["cc-devthrottle errors list --json"]
    if total > len(rows):
        next_steps.append(f"cc-devthrottle errors list --limit {min(500, max(limit, total))}")
    next_steps.append("cc-devthrottle errors list --machine <machine-name> --since 7d")
    if not admin:
        next_steps.append(ADMIN_TOKEN_COMMAND)
    axi_cli.print_next(next_steps)
