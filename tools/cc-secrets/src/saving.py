"""The ONE way an entry the owner typed is written: `add`, the `ask` pop-up and the cc-secrets window all come
through `save_entry`, so there is no second path by which a secret reaches the store."""

from __future__ import annotations

from typing import List, Optional, Tuple

from .audit import AuditLog, OwnerApproval
from .redact import SCRUBBER
from .store import KIND_SECRET, KIND_SETTING, Entry, SecretStore, make_entry


def save_entry(store: SecretStore, audit: AuditLog, name: str, username: str, secret: str, domains: List[str],
               notes: str, agents: bool, uses: List[str], env_name: str, setting: bool, command: str, detail: str,
               approval: Optional[OwnerApproval]) -> Tuple[Entry, bool]:
    """Validate and store one entry, and write its audit line. Returns (entry, replaced)."""
    entry = make_entry(name, username, secret, domains, notes, agents, uses,
                       env_name=env_name, kind=KIND_SETTING if setting else KIND_SECRET)
    # Registered only once make_entry has accepted it: the windows let the owner try again in the same process,
    # and a refused attempt (a typo, three letters) left in the scrubber would then block every audit line whose
    # entry or machine name happened to contain it. make_entry's messages never carry the secret.
    if entry.kind == KIND_SECRET:
        SCRUBBER.add(secret, username)
    # The audit line is built and checked before the store changes, so an approval text that carries the secret
    # is refused with nothing saved, rather than after the entry is already in.
    # Both possible lines are prepared, and the one matching what put reports is written, so a store that
    # changed underneath still gets a true line.
    suffix = f"; {detail}" if detail else ""
    prepared = {was_there: audit.prepare(name, command, "ok", ("replaced" if was_there else "added") + suffix,
                                         approval)
                for was_there in (False, True)}
    replaced = store.put(entry)
    audit.write_prepared([prepared[replaced]])
    return entry, replaced
