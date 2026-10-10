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

The grouped read and the linked issue (the Error Logging mission, issue #3675):

  errors groups               - the same errors, one row per PROBLEM: the Gateway's fingerprint, how many
                                reports, how many the user saw, first and last seen, a sample message. The same
                                two scopes and the same filters as `errors list`.
  errors link <fingerprint> <issue>
                              - record the work item filed for a problem on its permanent summary (or --clear
                                it). Administrator only.
  errors website-token        - mint (or rotate) the token the website files its errors with. The Gateway keeps
                                only its hash and shows the value once. Administrator only.

Output follows docs/axi-standard.md: a count line, a compact list, truncated messages with a size hint,
help[] lines, and `--json` for the Gateway's answer unchanged.
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple
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
GROUPS_PATH = "gateway/director-errors/groups"
ADMIN_GROUPS_PATH = "gateway/admin/director-errors/groups"
ADMIN_LINKED_ISSUE_PATH = "gateway/admin/director-errors/linked-issue"
ADMIN_LINK_COMMAND = "cc-secrets run admin-service-token -- cc-devthrottle errors link <fingerprint> <issue>"
ADMIN_WEBSITE_TOKEN_PATH = "gateway/admin/website-error-token"
ADMIN_WEBSITE_TOKEN_COMMAND = (
    "cc-secrets run admin-service-token -- cc-devthrottle errors website-token --gateway https://gateway.devthrottle.com"
)

# The same list, in the same order, as ErrorReportLimits.Components in
# src/CcDirector.Core/ErrorReports/ErrorReportContract.cs - the one place the components are defined.
# test_errors_groups.py reads that file and fails when the two differ.
COMPONENTS = ("director", "launcher", "install", "tool", "cockpit", "mobile", "gateway", "gateway-app", "website")

GROUP_FIELDS = (
    "fingerprint",
    "count",
    "occurrences",
    "visible",
    "machines",
    "versions",
    "component",
    "source",
    "exception",
    "first_seen",
    "last_seen",
    "issue",
    "message",
)
GROUP_DEFAULT_FIELDS = ("fingerprint", "count", "visible", "last_seen", "component", "message")

FINGERPRINT_LENGTH = 16

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
# Across every account the first question is WHOSE error it is, so the account leads.
ADMIN_DEFAULT_FIELDS = ("time", "account", "component", "machine", "message")

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


def _prepare_read(
    verb: str,
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
    gateway_url: Optional[str],
) -> Tuple[bool, str, Optional[str]]:
    """The checks and the query string `errors list` and `errors groups` share, so the two reads can never scope or
    filter differently. Returns (administrator read?, query string, bearer token or None for this session's key)."""
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

    params: Dict[str, str] = {"limit": str(limit)}
    for key, value in (("since", since), ("until", until), ("machine", machine), ("version", version),
                       ("component", component), ("account", account), ("email", email)):
        if value:
            params[key] = value

    bearer: Optional[str] = None
    if admin:
        bearer = _admin_token(
            "reading every account's errors",
            f"cc-secrets run admin-service-token -- cc-devthrottle errors {verb} --all-accounts",
            f"cc-devthrottle errors {verb}",
        )
    return admin, urlencode(params), bearer


def _admin_token(what: str, *next_commands: str) -> str:
    bearer = os.environ.get(ADMIN_TOKEN_VARIABLE, "").strip()
    if not bearer:
        axi_cli.fail(
            f"{what} needs the administrator service token in {ADMIN_TOKEN_VARIABLE}, and it is not set.",
            list(next_commands),
        )
    return bearer


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
    admin, query, bearer = _prepare_read(
        "list", json_output=json_output, all_accounts=all_accounts, account=account, email=email, since=since,
        until=until, machine=machine, version=version, component=component, limit=limit, fields=fields,
        gateway_url=gateway_url,
    )
    chosen = usage_errors.parse_fields(fields, ERROR_FIELDS, ADMIN_DEFAULT_FIELDS if admin else ERROR_DEFAULT_FIELDS)
    path = f"{ADMIN_PATH if admin else ACCOUNT_PATH}?{query}"

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
    next_steps.append("cc-devthrottle errors groups --since 7d")
    if not admin:
        next_steps.append(ADMIN_TOKEN_COMMAND)
    axi_cli.print_next(next_steps)


def _group_row(group: Dict[str, Any], full: bool) -> Dict[str, Any]:
    message = str(group.get("sample_message") or "")
    if not full and len(message) > MESSAGE_PREVIEW:
        message = f"{message[:MESSAGE_PREVIEW]}... (truncated, {len(message)} chars total - use --full)"
    return {
        "fingerprint": str(group.get("fingerprint") or ""),
        "count": int(group.get("count") or 0),
        "occurrences": int(group.get("occurrences") or 0),
        "visible": int(group.get("user_visible") or 0),
        "machines": int(group.get("machines") or 0),
        "versions": ",".join(str(v) for v in (group.get("versions") or [])),
        "component": str(group.get("component") or ""),
        "source": str(group.get("source") or ""),
        "exception": str(group.get("exception_type") or ""),
        "first_seen": str(group.get("first_seen_utc") or ""),
        "last_seen": str(group.get("last_seen_utc") or ""),
        "issue": str(group.get("linked_issue") or ""),
        "message": axi_output.escape_ascii(message),
    }


def group_errors(
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
    """`errors groups`: the reported errors one row per problem, the most reported first (issue #3675). The
    fingerprint that decides which reports are one problem is the Gateway's; this command only shows it."""
    admin, query, bearer = _prepare_read(
        "groups", json_output=json_output, all_accounts=all_accounts, account=account, email=email, since=since,
        until=until, machine=machine, version=version, component=component, limit=limit, fields=fields,
        gateway_url=gateway_url,
    )
    chosen = usage_errors.parse_fields(fields, GROUP_FIELDS, GROUP_DEFAULT_FIELDS)
    path = f"{ADMIN_GROUPS_PATH if admin else GROUPS_PATH}?{query}"

    try:
        answer = gateway.get_json(path, bearer=bearer, base_url=gateway_url)
    except gateway.GatewayError as err:
        axi_cli.fail(axi_output.escape_ascii(str(err)), [axi_cli.CHECK_GATEWAY])

    if not isinstance(answer, dict) or not isinstance(answer.get("groups"), list):
        axi_cli.fail(
            "the Gateway's answer has no list of groups. This tool will not read that as none. "
            "A Gateway older than this command does not have the route.",
            [axi_cli.CHECK_GATEWAY],
        )

    if json_output:
        print(json.dumps(answer, indent=2))
        return

    groups: List[Dict[str, Any]] = answer["groups"]
    total = int(answer.get("total_groups") or 0)
    rows = [_group_row(g, full) for g in groups]

    blocks = [
        f"scope: {'every account' if admin else 'this account'}, "
        f"{answer.get('since_utc')} to {answer.get('until_utc')} (kept {answer.get('retention_days')} days)",
        axi_output.format_count(len(rows), total=total),
        f"reports in these problems: {int(answer.get('total_reports') or 0)}",
        axi_output.render_list("groups", chosen, [{f: row[f] for f in chosen} for row in rows]),
    ]
    if not rows:
        blocks.append("none match")
    axi_output.write_blocks(sys.stdout, *blocks)

    next_steps = ["cc-devthrottle errors groups --json"]
    if total > len(rows):
        next_steps.append(f"cc-devthrottle errors groups --limit {min(500, max(limit, total))}")
    next_steps.append("cc-devthrottle errors groups --since 30d --component <component>")
    next_steps.append("cc-devthrottle errors list --since 7d")
    next_steps.append(ADMIN_LINK_COMMAND)
    axi_cli.print_next(next_steps)


def link_issue(
    *,
    fingerprint: str,
    issue: Optional[str],
    clear: bool,
    json_output: bool,
    gateway_url: Optional[str],
) -> None:
    """`errors link`: record on a problem's permanent summary the work item filed for it, or clear it (issue
    #3675). Administrator only: the summaries span every account."""
    fingerprint = (fingerprint or "").strip()
    if len(fingerprint) != FINGERPRINT_LENGTH or any(c not in "0123456789abcdef" for c in fingerprint):
        _usage_error("the fingerprint is the 16 lower-case hexadecimal characters `cc-devthrottle errors groups` shows.")
    if clear and issue:
        _usage_error("give an issue to link, or --clear to remove the link - not both.")
    if not clear and not issue:
        _usage_error("give the issue to link (#3675, owner/repo#3675 or a GitHub issue address), or --clear.")

    bearer = _admin_token("linking a problem to its issue", ADMIN_LINK_COMMAND)
    body = {"fingerprint": fingerprint, "linked_issue": None if clear else issue.strip()}
    try:
        answer = gateway.put_json(ADMIN_LINKED_ISSUE_PATH, body, bearer=bearer, base_url=gateway_url)
    except gateway.GatewayError as err:
        axi_cli.fail(axi_output.escape_ascii(str(err)), ["cc-devthrottle errors groups --all-accounts"])

    if not isinstance(answer, dict) or answer.get("fingerprint") != fingerprint:
        axi_cli.fail(
            "the Gateway's answer is not the updated summary, so this tool cannot say the link was recorded.",
            [axi_cli.CHECK_GATEWAY],
        )

    if json_output:
        print(json.dumps(answer, indent=2))
        return

    linked = answer.get("linked_issue")
    axi_output.write_blocks(
        sys.stdout,
        f"fingerprint: {fingerprint}",
        f"linked_issue: {axi_output.escape_ascii(str(linked)) if linked else '(none)'}",
        f"count: {int(answer.get('count') or 0)} reports, first seen {answer.get('first_seen_utc')}, "
        f"last seen {answer.get('last_seen_utc')}",
    )
    axi_cli.print_next(["cc-devthrottle errors groups --all-accounts"])


def mint_website_token(*, json_output: bool, gateway_url: Optional[str]) -> None:
    """`errors website-token`: ask the Gateway to mint the website's error-report token (issue #3675), replacing
    any before it. The hosted Gateway takes no secret through its settings, so it mints this one itself and keeps
    only its hash; the value is shown here once, for the owner to put into the website's environment as
    WEBSITE_ERROR_SERVICE_TOKEN. Administrator only."""
    bearer = _admin_token("minting the website error token", ADMIN_WEBSITE_TOKEN_COMMAND)
    try:
        answer = gateway.post_json(ADMIN_WEBSITE_TOKEN_PATH, {}, bearer=bearer, base_url=gateway_url)
    except gateway.GatewayError as err:
        axi_cli.fail(axi_output.escape_ascii(str(err)), [axi_cli.CHECK_GATEWAY])

    token = answer.get("token") if isinstance(answer, dict) else None
    if not isinstance(token, str) or not token:
        axi_cli.fail(
            "the Gateway's answer holds no token, so this tool cannot say one was minted.",
            [axi_cli.CHECK_GATEWAY],
        )

    if json_output:
        print(json.dumps(answer, indent=2))
        return

    axi_output.write_blocks(
        sys.stdout,
        f"token: {token}",
        f"minted_utc: {answer.get('minted_utc')}",
        "shown once: any earlier website error token stopped working when this one was minted.",
    )
    axi_cli.print_next([
        "put the token into the website's environment as WEBSITE_ERROR_SERVICE_TOKEN (production and preview)",
        "cc-devthrottle errors list --all-accounts --component website",
    ])
