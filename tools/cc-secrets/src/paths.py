"""Where the secret store lives on this machine.

The store is per operating-system USER, not per Director instance. That is why this module does not
use cc_storage: CcStorage honours CC_DIRECTOR_ROOT, which a Director sets for every session it runs
(for example ...\\cc-director\\instances\\default). The owner adds an entry from an ordinary terminal
that has no CC_DIRECTOR_ROOT, so a store resolved through CcStorage would put the owner's entries in
one folder and have every session read an empty store in another. One user, one machine, one store.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

from . import permissions

# Points the tool at a different store folder. Used by the tests and the leak check so they never
# touch the owner's real store. Pointing it elsewhere gains an agent nothing: the other folder holds
# only entries that were put there, and the owner's store is untouched.
HOME_OVERRIDE_ENV = "CC_SECRETS_HOME"

_checked_home: Path | None = None


def secrets_home() -> Path:
    """The folder holding the store, the audit log and the tool log."""
    override = os.environ.get(HOME_OVERRIDE_ENV)
    if override:
        return Path(override)
    if sys.platform == "win32":
        local = os.environ.get("LOCALAPPDATA")
        if not local:
            raise RuntimeError("LOCALAPPDATA is not set, so the per-user secret store cannot be located.")
        return Path(local) / "cc-director" / "secrets"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "cc-director" / "secrets"
    return Path.home() / ".cc-director" / "secrets"


def ensure_home() -> Path:
    """The secrets folder, created if needed and verified private to this user.

    Checked once per process per folder. When permissions had to be tightened the note is written to
    the tool log after the folder is private (the log lives inside it).
    """
    global _checked_home
    home = secrets_home()
    if _checked_home == home:
        return home
    note = permissions.ensure_private_folder(home)
    _checked_home = home
    if note:
        from . import filelog
        filelog.write(f"[paths] ensure_home: {note}")
    return home


def store_path() -> Path:
    """The store file: plain JSON, private to this user."""
    return secrets_home() / "secrets.json"


def audit_path() -> Path:
    """The audit log: one line per use, never a secret."""
    return secrets_home() / "secrets-audit.log"
