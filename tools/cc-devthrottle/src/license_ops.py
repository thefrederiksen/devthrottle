"""`cc-devthrottle license ...`: list, switch, add and remove the Claude accounts this machine holds.

The work is done by `license_store`; this module only resolves the folders, prints the answer in the AXI shape
(docs/axi-standard.md) and turns a store error into `Error:` plus next steps. Nothing printed here ever carries
a key: the store hands back emails, folders and key locations, never key values.

The folders are always resolvable without a running Director:
- the default config folder: `--default-config-dir`, else CC_LICENSE_DEFAULT_CONFIG_DIR, else `~/.claude`
  (which the store refuses unless a Director with license switching on runs the command);
- the store: `--store-dir`, else CC_LICENSE_STORE_DIR, else `<DevThrottle data folder>/licenses`.
"""

from __future__ import annotations

import datetime as _dt
import json
import os
import sys
from typing import Optional

from cc_shared import axi_output

from . import axi_cli, usage_errors
from .license_store import (
    ENV_ALLOW_REAL_DEFAULT,
    ENV_DEFAULT_CONFIG_DIR,
    ENV_STORE_DIR,
    HeldAccount,
    LicenseStore,
    LicenseStoreError,
    ROLE_ACTIVE,
    ROLE_SPARE,
    real_default_config_dir,
)

LIST = "cc-devthrottle license list"
FIELDS = ("email", "role", "organization", "configDir", "accountFile", "keyStore", "keyLocation")
DEFAULT_FIELDS = ("email", "role", "organization")


def resolve_default_config_dir(given: Optional[str]) -> str:
    return given or os.environ.get(ENV_DEFAULT_CONFIG_DIR) or str(real_default_config_dir())


def resolve_store_dir(given: Optional[str]) -> str:
    if given or os.environ.get(ENV_STORE_DIR):
        return given or os.environ[ENV_STORE_DIR]
    from .setup_ops import _install_root

    return str(_install_root() / "licenses")


def allow_real_default() -> bool:
    """True only when the Director that ran this command has license switching on (it sets the variable to "1")."""
    return os.environ.get(ENV_ALLOW_REAL_DEFAULT) == "1"


def _open(default_config_dir: Optional[str], store_dir: Optional[str]) -> LicenseStore:
    try:
        return LicenseStore(resolve_default_config_dir(default_config_dir), resolve_store_dir(store_dir),
                            allow_real_default=allow_real_default())
    except LicenseStoreError as exc:
        axi_cli.fail(str(exc), exc.next_steps)


def _report_recovery(store: LicenseStore) -> None:
    if store.last_recovery:
        axi_cli.warn(store.last_recovery)
    for heal in store.last_heals:
        axi_cli.warn(f"{heal.account} was signed in by hand into the default folder while it was also held as a "
                     f"spare; the stale spare was moved from {heal.moved_from} to {heal.moved_to}")


def healed_json(store: LicenseStore) -> list:
    """The `healed` field of a `--json` answer: every hand sign-in this run healed (see
    `LicenseStore._heal_hand_sign_in`). The Director reads it to tell the Gateway the machine was switched by hand."""
    return [heal.to_json() for heal in store.last_heals]


def _run(store: LicenseStore, action):
    try:
        return action()
    except LicenseStoreError as exc:
        _report_recovery(store)
        axi_cli.fail(str(exc), exc.next_steps)


def _arg(email: Optional[str]) -> str:
    return axi_cli.bare(email, "<email>")


def list_accounts(default_config_dir: Optional[str], store_dir: Optional[str], fields: Optional[str],
                  json_output: bool) -> None:
    if fields is not None and json_output:
        axi_cli.usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen = usage_errors.parse_fields(fields, FIELDS, DEFAULT_FIELDS)
    store = _open(default_config_dir, store_dir)
    accounts: list[HeldAccount] = _run(store, store.held_accounts)
    _report_recovery(store)
    if json_output:
        print(json.dumps({
            "count": len(accounts),
            "defaultConfigDir": store.default_config_dir,
            "storeDir": store.store_dir,
            "recovered": store.last_recovery,
            "healed": healed_json(store),
            "accounts": [a.to_json() for a in accounts],
        }, indent=2))
        return
    active = sum(1 for a in accounts if a.role == ROLE_ACTIVE)
    blocks = [axi_output.format_count(len(accounts), breakdown=[(ROLE_ACTIVE, active), (ROLE_SPARE, len(accounts) - active)])]
    if accounts:
        rows = [{f: a.to_json()[f] for f in chosen} for a in accounts]
        blocks.append(axi_output.render_list("accounts", chosen, rows))
    else:
        blocks.append(axi_cli.ascii_text(f"No accounts held: the default folder {store.default_config_dir} holds "
                                         "no sign-in key and there are no spares."))
    # The list block is already rendered ASCII; write_lines would escape its line breaks.
    axi_output.write_blocks(sys.stdout, *blocks)
    spare = next((a for a in accounts if a.role == ROLE_SPARE), None)
    next_steps = [f"cc-devthrottle license switch {_arg(spare.email)}"] if spare else []
    axi_cli.print_next(next_steps + ["cc-devthrottle license add <account-folder>", f"{LIST} --json"])


def _utc(moment: _dt.datetime) -> str:
    return moment.astimezone(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _switch_json(outcome: str, reason_code: Optional[str], message: str, previous: Optional[str],
                 after: Optional[str], started: _dt.datetime, finished: _dt.datetime, exchange_ms: int,
                 previous_kept_in: Optional[str], recovered: Optional[str]) -> str:
    """The `--json` answer, in the field names of LicenseSwitchResult (missions/license-switching/contract.md,
    section 3.4). The Director adds what only it knows: the switch id, the usage read, replay."""
    return json.dumps({
        "outcome": outcome,
        "reasonCode": reason_code,
        "message": message,
        "previousActiveAccountEmail": previous or "",
        "activeAccountEmailAfter": after or "",
        "startedAtUtc": _utc(started),
        "finishedAtUtc": _utc(finished),
        "exchangeMilliseconds": exchange_ms,
        "previousKeptIn": previous_kept_in,
        "recovered": recovered,
    }, indent=2)


def switch_account(email: str, expect_active: Optional[str], default_config_dir: Optional[str],
                   store_dir: Optional[str], json_output: bool) -> None:
    started = _dt.datetime.now(_dt.timezone.utc)
    store = _open(default_config_dir, store_dir)
    try:
        result = store.switch(email, expect_active=expect_active)
    except LicenseStoreError as exc:
        _report_recovery(store)
        # Read what is there now, so a refusal reports the real state, as the contract asks.
        try:
            after = store.active_account_email()
        except LicenseStoreError:
            after = None
        previous = exc.previous_active if exc.previous_active is not None else after
        message = str(exc)
        if json_output:
            print(_switch_json("failed", exc.reason_code, message, previous, after, started,
                               _dt.datetime.now(_dt.timezone.utc), 0, None, store.last_recovery))
        reason = f" (reason: {exc.reason_code})" if exc.reason_code else ""
        axi_cli.fail(f"{message}{reason}", exc.next_steps)
    _report_recovery(store)
    if not result.changed:
        message = f"{result.active} is already the active account."
    elif result.previous:
        message = f"Switched from {result.previous} to {result.active}."
    else:
        message = f"Switched to {result.active}; the default folder held no sign-in before."
    if json_output:
        print(_switch_json("switched" if result.changed else "already-active", None, message, result.previous,
                           result.active, result.started_at, result.finished_at, result.exchange_milliseconds,
                           result.previous_kept_in, store.last_recovery))
        return
    if not result.changed:
        axi_cli.write_lines(f"Unchanged: {message}")
    else:
        kept = f" {result.previous} is kept as a spare." if result.previous else ""
        axi_cli.write_lines(f"{message} Read back and verified: {result.active} is now the active account.{kept}")
    axi_cli.print_next([LIST])


def add_account(folder: str, default_config_dir: Optional[str], store_dir: Optional[str], json_output: bool) -> None:
    store = _open(default_config_dir, store_dir)
    account = _run(store, lambda: store.add(folder))
    _report_recovery(store)
    source = folder
    if json_output:
        print(json.dumps({
            "added": account.email,
            "movedFrom": source,
            "recovered": store.last_recovery,
            "account": account.to_json(),
        }, indent=2))
        return
    axi_cli.write_lines(f"Added: {account.email} is held as a spare in {account.config_dir}. "
                        f"Its sign-in was moved, so {source} no longer holds it.")
    axi_cli.print_next([f"cc-devthrottle license switch {_arg(account.email)}", LIST])


def remove_account(email: str, yes: bool, default_config_dir: Optional[str], store_dir: Optional[str],
                   json_output: bool) -> None:
    if not axi_cli.confirm_or_fail(f"Delete the spare sign-in for {email}? It cannot be undone.", yes, "--yes"):
        axi_cli.fail("not removed: the confirmation was declined.", [LIST])
    store = _open(default_config_dir, store_dir)
    account = _run(store, lambda: store.remove(email))
    _report_recovery(store)
    if json_output:
        print(json.dumps({"removed": account.email, "configDir": account.config_dir,
                          "recovered": store.last_recovery}, indent=2))
        return
    axi_cli.write_lines(f"Removed: the spare sign-in for {account.email} was deleted from this machine.")
    axi_cli.print_next([LIST, "cc-devthrottle license add <account-folder>"])
