"""The machines that can receive a secret, as this machine sees them (the Secret Handoff mission).

The Gateway lists the account's machines whose connected Directors offer a key (`GET /gateway/secrets/machines`).
Each row is checked here against what this machine knows: which one is THIS machine (its fingerprint is this
machine's own), and whether the key is the one pinned the last time (known_machines). The fingerprint is computed
here from the key itself, never taken from the Gateway's word, so a Gateway that sent a mismatched pair is caught.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import List, Optional

from . import gateway_link, known_machines, machine_key
from .errors import CcSecretsError

ROUTE = "gateway/secrets/machines"


@dataclass(frozen=True)
class Machine:
    name: str
    public_key: bytes
    fingerprint: str
    last_seen_utc: str
    directors: int
    this_machine: bool
    pin: known_machines.PinCheck
    conflict: str = ""

    @property
    def can_receive(self) -> bool:
        return not self.conflict


def _field(row: dict, name: str) -> object:
    return row.get(name, row.get(name[:1].upper() + name[1:], ""))


def listed(own: machine_key.MachineKey, link: Optional[gateway_link.Link] = None) -> List[Machine]:
    """Every machine the Gateway lists, checked."""
    answer = gateway_link.get(ROUTE, link) or {}
    rows = answer.get("machines", answer.get("Machines", [])) if isinstance(answer, dict) else None
    if not isinstance(rows, list):
        raise CcSecretsError("The Gateway's list of machines is not in the shape cc-secrets reads.")
    machines = []
    for row in rows:
        name = str(_field(row, "machine"))
        conflict = str(_field(row, "conflict") or "")
        if conflict:
            machines.append(Machine(name, b"", "", str(_field(row, "lastSeenUtc")), int(_field(row, "directors") or 0),
                                    False, known_machines.PinCheck(known_machines.NEW, None), conflict))
            continue
        public = machine_key.decode_public_key(str(_field(row, "publicKey")))
        print_ = machine_key.fingerprint(public)
        if str(_field(row, "fingerprint")) != print_:
            raise CcSecretsError(f"The Gateway's fingerprint for {name} does not match the key it sent. Nothing was done.")
        machines.append(Machine(name, public, print_, str(_field(row, "lastSeenUtc")), int(_field(row, "directors") or 0),
                                print_ == own.fingerprint, known_machines.check(name, print_)))
    return machines


def find(machines: List[Machine], name: str) -> Machine:
    """The machine called `name` (any case), or a CcSecretsError naming the machines that ARE there."""
    match = next((m for m in machines if m.name.lower() == name.strip().lower()), None)
    if match is None:
        there = ", ".join(m.name for m in machines) or "none"
        raise CcSecretsError(f"No machine called '{name}' can receive a secret right now (machines that can: {there}). "
                             "A machine is listed while a Director that can take part is running on it.")
    if match.conflict:
        raise CcSecretsError(match.conflict)
    return match


def this_machine(machines: List[Machine]) -> Machine:
    """This machine's own row: the one whose key is this machine's key."""
    match = next((m for m in machines if m.this_machine), None)
    if match is None:
        raise CcSecretsError("This machine is not in the Gateway's list of machines that can receive a secret. A "
                             "Director that can take part must be running here, signed in to your account.")
    return match
