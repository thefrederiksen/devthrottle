"""`cc-devthrottle license usage` and `cc-devthrottle license refresh`: the command-line layer over
`license_usage`, in the AXI shape (docs/axi-standard.md).

`license usage --json` is what the Director reads: `{count, total, defaultConfigDir, storeDir, recovered,
healed, accounts}`, the same envelope as `license list --json` (`healed` lists the hand sign-ins this run healed), where `accounts` are the contract's `LicenseHolding` rows
(missions/license-switching/contract.md section 2.2), active first. `--active` reads only the
active holding (the Director reads it every 2 minutes, the spares at most every 10).

WHY RENEWING A SPARE IS ASKED FOR, NOT AUTOMATIC. A renewal rewrites a stored sign-in and spends its
refresh token. A command that looks like a read never does that on its own, run as often as an agent
likes. The Director asks for it - `license usage --refresh` renews every spare that is due and then
reads it, so one call keeps spares fresh and reports them, and a renewal that fails is that row's
`refresh-failed`. `license refresh` renews without reading, for a person or a test.

Both commands open the key store exactly as `license list` does (`license_ops._open`): the same
`--default-config-dir` and `--store-dir`, the same environment variables, and the same refusal of the
machine's real Claude login.
"""

from __future__ import annotations

import json
import sys
from typing import Any, Dict, List, Optional

from cc_shared import axi_output

from . import axi_cli, license_ops, license_usage, usage_errors
from .license_store import LicenseStore
from .license_usage import RefreshError

USAGE_FIELDS = [
    "account", "state", "five-hour", "week", "five-hour-resets", "week-resets", "extra-usage",
    "active", "plan", "tier", "detail", "read-at", "sign-in-expires", "folder",
]
USAGE_DEFAULT_FIELDS = ["account", "state", "five-hour", "week", "five-hour-resets", "week-resets", "extra-usage"]

REFRESH_FIELDS = ["account", "result", "expires-at", "refresh-token-rotated", "sign-in-expires", "detail", "folder"]
REFRESH_DEFAULT_FIELDS = ["account", "result", "expires-at", "refresh-token-rotated"]

_USAGE = "cc-devthrottle license usage"
_REFRESH = "cc-devthrottle license refresh"


def _open(default_config_dir: Optional[str], store_dir: Optional[str]) -> LicenseStore:
    """The key store, resolved exactly as `license list` resolves it."""
    return license_ops._open(default_config_dir, store_dir)


def _held(store: LicenseStore) -> List[license_usage.KeyHandle]:
    keys = license_ops._run(store, lambda: license_usage.held_keys(store))
    license_ops._report_recovery(store)
    return keys


def _percent(window: Optional[license_usage.Window]) -> Optional[str]:
    return None if window is None else f"{window.utilization:g}%"


def _extra_state(extra: Optional[license_usage.ExtraUsage]) -> Optional[str]:
    if extra is None or extra.is_enabled is None:
        return None
    return "on" if extra.is_enabled else "off"


def _usage_record(r: license_usage.UsageReading, chosen: List[str]) -> Dict[str, Any]:
    values = {
        "account": r.account_email,
        "state": r.read_error or "ok",
        "five-hour": _percent(r.five_hour),
        "week": _percent(r.seven_day),
        "five-hour-resets": r.five_hour.resets_at_utc if r.five_hour else None,
        "week-resets": r.seven_day.resets_at_utc if r.seven_day else None,
        "extra-usage": _extra_state(r.extra_usage),
        "active": "yes" if r.is_active else "no",
        "plan": r.plan,
        "tier": r.rate_limit_tier,
        "detail": r.read_error_detail,
        "read-at": r.read_at_utc,
        "sign-in-expires": r.sign_in_expires_at_utc,
        "folder": r.folder,
    }
    return {name: values[name] for name in chosen}


def usage(json_output: bool, fields: Optional[str], default_config_dir: Optional[str], store_dir: Optional[str],
          *, active_only: bool = False, refresh_due: bool = False,
          http_get: Optional[license_usage.HttpGet] = None,
          http_post_json: Optional[license_usage.HttpPostJson] = None) -> None:
    """One row per held account: its 5-hour and weekly use, both reset times, extra-usage state."""
    if json_output and fields is not None:
        axi_cli.usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen = usage_errors.parse_fields(fields, USAGE_FIELDS, USAGE_DEFAULT_FIELDS)
    store = _open(default_config_dir, store_dir)
    held = _held(store)
    keys = [k for k in held if k.role == license_usage.ROLE_ACTIVE] if active_only else held
    get = http_get or license_usage._requests_get
    if refresh_due:
        readings = [license_usage.read_with_refresh(k, get, http_post_json) for k in keys]
    else:
        readings = [license_usage.read_usage(k, get) for k in keys]

    if json_output:
        # The same envelope as `license list --json`; the Director reads `accounts`. `total` is every
        # account held, so a filtered read says what it left out; `recovered` is what the store finished
        # or undid of an interrupted switch before reading, as in `license list`.
        print(json.dumps({
            "count": len(readings),
            "total": len(held),
            "defaultConfigDir": store.default_config_dir,
            "storeDir": store.store_dir,
            "recovered": store.last_recovery,
            "healed": license_ops.healed_json(store),
            "accounts": [r.to_json() for r in readings],
        }, indent=2))
        return

    states = ("ok",) + license_usage.READ_ERRORS
    breakdown = [(s, n) for s in states if (n := sum(1 for r in readings if (r.read_error or "ok") == s))] or None
    blocks = [
        axi_output.format_count(len(readings), total=len(held) if active_only else None, breakdown=breakdown),
        axi_output.render_list("accounts", chosen, [_usage_record(r, chosen) for r in readings]),
    ]
    help_lines = []
    if any(r.read_error and not r.is_active for r in readings):
        help_lines.append(f"{_REFRESH} <account>")
    if any(r.read_error for r in readings):
        help_lines.append(f"{_USAGE} --fields account,state,detail")
    help_lines.append(f"{_USAGE} --json")
    blocks.append(axi_output.format_help(help_lines))
    axi_output.write_blocks(sys.stdout, *blocks)


def refresh(account: Optional[str], force: bool, json_output: bool, fields: Optional[str],
            default_config_dir: Optional[str], store_dir: Optional[str],
            http_post_json: Optional[license_usage.HttpPostJson] = None) -> None:
    """Renew a spare's key (or every spare's) in its own folder, when it is due."""
    if json_output and fields is not None:
        axi_cli.usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen = usage_errors.parse_fields(fields, REFRESH_FIELDS, REFRESH_DEFAULT_FIELDS)
    held = _held(_open(default_config_dir, store_dir))
    if account is not None:
        targets = [k for k in held if k.account.lower() == account.strip().lower()]
        if not targets:
            axi_cli.fail(
                f"this machine holds no account {account!r}. Held: " + ", ".join(k.account.lower() for k in held),
                [license_ops.LIST, _REFRESH],
            )
    else:
        targets = [k for k in held if k.role == license_usage.ROLE_SPARE]

    rows: List[Dict[str, Any]] = []
    for key in targets:
        try:
            result = license_usage.refresh_spare(
                key, force=force, http_post_json=http_post_json or license_usage._requests_post_json)
            rows.append({**result.to_json(), "result": "refreshed" if result.refreshed else result.reason,
                         "detail": None})
        except RefreshError as ex:
            rows.append({"accountEmail": key.account.lower(), "folder": str(key.folder), "refreshed": False,
                         "reason": ex.kind, "expiresAtUtc": None, "refreshTokenRotated": None,
                         "signInExpiresAtUtc": None, "result": f"failed-{ex.kind}", "detail": str(ex)})
    failed = [row for row in rows if row["result"].startswith("failed-")]

    if json_output:
        print(json.dumps(rows, indent=2))
    else:
        refreshed = sum(1 for row in rows if row["result"] == "refreshed")
        breakdown = [(label, n) for label, n in (("refreshed", refreshed),
                                                  ("not-needed", len(rows) - refreshed - len(failed)),
                                                  ("failed", len(failed))) if n] or None
        records = [{
            "account": row["accountEmail"], "result": row["result"], "expires-at": row["expiresAtUtc"],
            "refresh-token-rotated": row["refreshTokenRotated"], "sign-in-expires": row["signInExpiresAtUtc"],
            "detail": row["detail"], "folder": row["folder"],
        } for row in rows]
        blocks = [
            axi_output.format_count(len(rows), breakdown=breakdown),
            axi_output.render_list("spares", chosen, [{f: r[f] for f in chosen} for r in records]),
        ]
        if not rows:
            blocks.append("This machine holds no spare account to refresh.")
        blocks.append(axi_output.format_help([_USAGE, f"{_REFRESH} <account> --force"]))
        axi_output.write_blocks(sys.stdout, *blocks)
    if failed:
        axi_cli.fail(
            f"{len(failed)} of {len(rows)} refreshes failed: "
            + " ".join(f"{row['accountEmail']}: {row['detail']}" for row in failed),
            [f"{_REFRESH} --fields account,result,detail", _USAGE],
        )
