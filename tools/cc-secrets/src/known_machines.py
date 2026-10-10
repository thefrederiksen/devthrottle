"""The other machines' keys this machine has seen, pinned the first time (the Secret Handoff mission).

The Gateway hands out the public keys, so a Gateway that swapped one could redirect a secret to a key of its own
choosing. Pinning closes that: the first time this machine seals a secret to another machine (or opens one from it),
the key's fingerprint is written here; afterwards a different fingerprint for that machine is a CHANGED key. An agent's
transfer is then refused, and the owner is asked in the window. Kept in `known-machines.json` in the private secrets
folder. Machine names are compared without regard to case. Only public fingerprints are here - nothing secret.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Dict, Optional

from . import filelog, paths
from .storefile import UserOnlyFile

FILE = "known-machines.json"
VERSION = 1

NEW = "new"
SAME = "same"
CHANGED = "changed"


@dataclass(frozen=True)
class Pin:
    machine: str
    fingerprint: str
    pinned_utc: str


@dataclass(frozen=True)
class PinCheck:
    """`state` is NEW (never seen), SAME (the pinned key) or CHANGED (a different key than the one pinned, which is in
    `pinned`)."""
    state: str
    pinned: Optional[Pin]


def _file() -> UserOnlyFile:
    return UserOnlyFile(paths.secrets_home() / FILE)


def _read() -> Dict[str, Pin]:
    file = _file()
    if not file.exists():
        return {}
    document = json.loads(file.read().decode("utf-8"))
    return {key: Pin(str(value["machine"]), str(value["fingerprint"]), str(value["pinnedUtc"]))
            for key, value in document.get("machines", {}).items()}


def check(machine: str, fingerprint: str) -> PinCheck:
    pinned = _read().get(machine.lower())
    if pinned is None:
        return PinCheck(NEW, None)
    return PinCheck(SAME if pinned.fingerprint == fingerprint else CHANGED, pinned)


def pin(machine: str, fingerprint: str) -> None:
    """Pin (or re-pin, after the owner accepted a changed key) a machine's fingerprint."""
    pins = _read()
    pins[machine.lower()] = Pin(machine, fingerprint, datetime.now(timezone.utc).isoformat(timespec="seconds"))
    document = {"version": VERSION, "machines": {key: {"machine": p.machine, "fingerprint": p.fingerprint,
                                                       "pinnedUtc": p.pinned_utc} for key, p in sorted(pins.items())}}
    _file().write((json.dumps(document, indent=2) + "\n").encode("utf-8"))
    filelog.write(f"[known_machines] pinned {machine} at {fingerprint[:16]}")
