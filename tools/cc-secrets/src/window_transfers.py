"""What the cc-secrets window does to move an entry between machines, without any of the window itself (the Secret
Handoff mission, phase 5): the machines it can send to, sending and getting an entry with the owner's approval given
by the click in the window, and the transfers waiting on this machine for the owner's answer.

The window answers with THIS MACHINE's own Gateway credential, never a session's key, even when an agent opened it:
the window is the owner's surface. The Gateway records that credential and its machine as the one that answered.
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Callable, List, Optional

from . import filelog, gateway_link, known_machines, machine_key, machines, transfers
from .store import SecretStore

WHERE = "window"
FINAL_STATES = ("delivered", "failed", "denied", "expired")


@dataclass(frozen=True)
class Destination:
    """A machine the window can send to or get from, and what the owner needs to know about its key."""
    name: str
    key_note: str
    key_changed: bool


class WindowTransfers:
    def __init__(self, store: SecretStore, link_factory: Callable[[], gateway_link.Link] = None,
                 sleep: Callable[[float], None] = time.sleep, poll_seconds: float = 2.0) -> None:
        self._store = store
        self._link_factory = link_factory or gateway_link.resolve_machine
        self._sleep = sleep
        self._poll_seconds = poll_seconds

    def _link(self) -> gateway_link.Link:
        return self._link_factory()

    def _listed(self, link: gateway_link.Link):
        own, _ = machine_key.load_or_create()
        found = machines.listed(own, link)
        return found, machines.this_machine(found)

    def this_machine(self) -> str:
        _, here = self._listed(self._link())
        return here.name

    def others(self) -> List[Destination]:
        """The other machines that can take part now, in the Gateway's order."""
        found, _ = self._listed(self._link())
        rows = []
        for m in found:
            if m.this_machine:
                continue
            if m.conflict:
                rows.append(Destination(m.name, m.conflict, False))
                continue
            note = {known_machines.NEW: "Not sent to from here before.",
                    known_machines.SAME: "Its key is the one pinned when this machine first sent to it.",
                    known_machines.CHANGED: "Its KEY HAS CHANGED since this machine pinned it."}[m.pin.state]
            rows.append(Destination(m.name, note, m.pin.state == known_machines.CHANGED))
        return rows

    def send(self, entry: str, to: str, target: str, replace: bool, accept_new_key: bool) -> transfers.Transfer:
        """Send an entry from this machine, approved by the owner's click on Send in this window."""
        link = self._link()
        found, here = self._listed(link)
        destination = machines.find(found, to)
        if self._store.get(entry) is None:
            raise gateway_link.GatewayRefusal(0, "no_such_entry", f"No entry named '{entry}' is on this machine.")
        body = {"entry": entry, "targetName": target or entry, "fromMachine": here.name, "toMachine": destination.name,
                "replace": replace, "reason": "", "approvedHere": WHERE, "askedOn": here.name}
        if accept_new_key:
            body["acceptReceiverFingerprint"] = destination.fingerprint
        filelog.write(f"[window_transfers] send: {entry} to {destination.name}")
        return transfers.create(body, link)

    def request(self, entry: str, source: str, target: str, replace: bool) -> transfers.Transfer:
        """Get an entry from another machine onto this one, approved by the owner's click on Get in this window."""
        link = self._link()
        found, here = self._listed(link)
        holder = machines.find(found, source)
        body = {"entry": entry, "targetName": target or entry, "fromMachine": holder.name, "toMachine": here.name,
                "replace": replace, "reason": "", "approvedHere": WHERE, "askedOn": here.name}
        filelog.write(f"[window_transfers] request: {entry} from {holder.name}")
        return transfers.create(body, link)

    def wait(self, t: transfers.Transfer, limit_seconds: float = transfers.WAIT_LIMIT_SECONDS) -> transfers.Transfer:
        """Read the transfer until it ends (or the limit passes). Run off the window's own thread."""
        link = self._link()
        waited = 0.0
        while t.state not in FINAL_STATES and waited < limit_seconds:
            self._sleep(self._poll_seconds)
            waited += self._poll_seconds
            t = transfers.find(t.transfer_id, link)
        return t

    def waiting_here(self) -> List[transfers.Transfer]:
        """Transfers that can still be answered and would send an entry FROM this machine: the ones its pop-up asks
        the owner about."""
        link = self._link()
        _, here = self._listed(link)
        return [t for t in transfers.listed(link)
                if t.can_answer and t.from_machine.lower() == here.name.lower()]

    def answer(self, transfer_id: str, approve: bool) -> transfers.Transfer:
        t, _ = transfers.answer(transfer_id, approve, WHERE, None, self._link())
        filelog.write(f"[window_transfers] answer: {transfer_id} approve={approve}")
        return t


def ended_sentence(t: transfers.Transfer) -> str:
    """What the window says when a transfer it started has ended."""
    if t.state == "delivered":
        return f"Stored {t.target_name} on {t.to_machine}."
    if t.state not in FINAL_STATES:
        return f"Still not finished: {t.status_text}"
    return t.outcome or t.status_text
