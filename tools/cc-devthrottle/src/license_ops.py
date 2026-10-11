"""`cc-devthrottle license ...`: list, switch, add and remove the Claude accounts this machine holds.

The work is done by `license_store`; this module only resolves the folders, prints the answer in the AXI shape
(docs/axi-standard.md) and turns a store error into `Error:` plus next steps. Nothing printed here ever carries
a key: the store hands back emails, folders and key locations, never key values.

The folders are always resolvable without a running Director:
- the default config folder: `--default-config-dir`, else CC_LICENSE_DEFAULT_CONFIG_DIR, else `~/.claude`
  (which the store refuses in this release, so every run names an isolated folder);
- the store: `--store-dir`, else CC_LICENSE_STORE_DIR, else `<DevThrottle data folder>/licenses`.
"""

from __future__ import annotations

import json
import os
import sys
from typing import Optional

from cc_shared import axi_output

from . import axi_cli, usage_errors
from .license_store import (
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


def _open(default_config_dir: Optional[str], store_dir: Optional[str]) -> LicenseStore:
    try:
        return LicenseStore(resolve_default_config_dir(default_config_dir), resolve_store_dir(store_dir))
    except LicenseStoreError as exc:
        axi_cli.fail(str(exc), exc.next_steps)


def _report_recovery(store: LicenseStore) -> None:
    if store.last_recovery:
        axi_cli.warn(store.last_recovery)


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


def switch_account(email: str, default_config_dir: Optional[str], store_dir: Optional[str], json_output: bool) -> None:
    store = _open(default_config_dir, store_dir)
    result = _run(store, lambda: store.switch(email))
    _report_recovery(store)
    if json_output:
        print(json.dumps({
            "switched": result.changed,
            "active": result.active,
            "previous": result.previous,
            "previousKeptIn": result.previous_kept_in,
            "verified": True,
            "recovered": store.last_recovery,
        }, indent=2))
        return
    if not result.changed:
        axi_cli.write_lines(f"Unchanged: {result.active} is already the active account.")
    elif result.previous:
        axi_cli.write_lines(f"Switched: {result.active} is now the active account, read back and verified. "
                            f"{result.previous} is kept as a spare.")
    else:
        axi_cli.write_lines(f"Switched: {result.active} is now the active account, read back and verified. "
                            "The default folder held no sign-in before.")
    axi_cli.print_next([LIST])


def add_account(folder: str, default_config_dir: Optional[str], store_dir: Optional[str], json_output: bool) -> None:
    store = _open(default_config_dir, store_dir)
    account = _run(store, lambda: store.add(folder))
    _report_recovery(store)
    source = os.path.abspath(folder)
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
