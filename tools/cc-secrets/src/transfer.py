"""The two halves of moving one entry between machines, run by each machine's Director (the Secret Handoff mission).

`send_half` runs on the machine that HOLDS the entry: it checks the receiving machine's key against the one pinned
here, seals the whole entry to it and returns the envelope. `receive_half` runs on the machine that RECEIVES it: it
refuses an id it has already accepted, a transfer past its time, an envelope from a machine whose key changed, and a
name already taken unless the approval said it replaces it; then it opens the envelope and stores the entry through
the one write path every other command uses.

Each half reads its facts from the Director on standard input and answers with one JSON object. Neither ever prints,
logs or audits the secret, and a refusal says why in a sentence the owner can act on.
"""

from __future__ import annotations

import base64
import json
import re
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Dict, List, Optional

from . import filelog, known_machines, machine_key, paths, sealing
from .audit import AuditLog, OwnerApproval
from .errors import CcSecretsError
from .saving import save_entry
from .storefile import UserOnlyFile
from .store import Entry, SecretStore

ID_SHAPE = re.compile(r"^[0-9a-f]{32}$")
FINGERPRINT_SHAPE = re.compile(r"^[0-9a-f]{64}$")
ACCEPTED_FILE = "accepted-transfers.json"
ACCEPTED_KEEP = timedelta(days=30)


class TransferRefused(CcSecretsError):
    """This half will not go ahead, for the reason in the message. Nothing was sent or stored."""


@dataclass(frozen=True)
class Facts:
    transfer_id: str
    entry: str
    target_name: str
    from_machine: str
    to_machine: str
    replace: bool
    expires_utc: str
    approved_where: str
    approval_words: Optional[str]

    def bound(self, sender_fingerprint: str, receiver_fingerprint: str) -> Dict[str, object]:
        return {"transferId": self.transfer_id, "entry": self.entry, "targetName": self.target_name,
                "fromMachine": self.from_machine, "toMachine": self.to_machine, "replace": self.replace,
                "expiresAtUtc": self.expires_utc, "senderFingerprint": sender_fingerprint,
                "receiverFingerprint": receiver_fingerprint}

    def approval(self) -> Optional[OwnerApproval]:
        return OwnerApproval(text=self.approval_words, session_name="") if self.approval_words else None

    def note(self) -> str:
        return f"transfer {self.transfer_id}, approved in the {self.approved_where}"


def _text(payload: dict, key: str, required: bool = True) -> str:
    value = payload.get(key)
    if value is None or (isinstance(value, str) and not value.strip()):
        if required:
            raise TransferRefused(f"The transfer is missing '{key}'.")
        return ""
    if not isinstance(value, str):
        raise TransferRefused(f"'{key}' in the transfer is not text.")
    return value.strip()


def _fingerprint(payload: dict, key: str) -> str:
    value = _text(payload, key).lower()
    if not FINGERPRINT_SHAPE.match(value):
        raise TransferRefused(f"'{key}' in the transfer is not a key fingerprint.")
    return value


def _public_key(payload: dict, key: str, fingerprint: str) -> bytes:
    try:
        raw = machine_key.decode_public_key(_text(payload, key))
    except CcSecretsError as exc:
        raise TransferRefused(f"'{key}' in the transfer is not a machine key.") from exc
    if machine_key.fingerprint(raw) != fingerprint:
        raise TransferRefused(f"'{key}' in the transfer does not match its fingerprint.")
    return raw


def parse_facts(payload: object, transfer_id: str) -> Facts:
    if not isinstance(payload, dict):
        raise TransferRefused("The transfer the Director passed is not a JSON object.")
    facts = Facts(
        transfer_id=_text(payload, "transferId").lower(), entry=_text(payload, "entry"),
        target_name=_text(payload, "targetName"), from_machine=_text(payload, "fromMachine"),
        to_machine=_text(payload, "toMachine"), replace=payload.get("replace") is True,
        expires_utc=_text(payload, "expiresAtUtc"), approved_where=_text(payload, "approvedWhere"),
        approval_words=_text(payload, "approvalWords", required=False) or None)
    if not ID_SHAPE.match(facts.transfer_id):
        raise TransferRefused("The transfer id is not 32 hex characters.")
    if facts.transfer_id != transfer_id.strip().lower():
        raise TransferRefused("The transfer id on the command line does not match the one in the transfer.")
    return facts


def _not_past(facts: Facts, now: datetime) -> None:
    try:
        deadline = datetime.fromisoformat(facts.expires_utc.replace("Z", "+00:00"))
    except ValueError as exc:
        raise TransferRefused("The transfer's time limit is not a time.") from exc
    if deadline.tzinfo is None:
        deadline = deadline.replace(tzinfo=timezone.utc)
    if now > deadline:
        raise TransferRefused(f"The transfer had to arrive by {facts.expires_utc} and it is later than that. "
                              "Nothing was stored.")


def _entry_plaintext(entry: Entry) -> bytes:
    record = entry._to_record()
    record.pop("name", None)
    return json.dumps(record, separators=(",", ":")).encode("utf-8")


# --- The holding machine -----------------------------------------------------------------------------------------

def send_half(payload: object, transfer_id: str, store: SecretStore, audit: AuditLog) -> Dict[str, object]:
    """Seal the entry for the receiving machine. Returns {ok, envelope, senderPublicKey, senderFingerprint}."""
    facts = parse_facts(payload, transfer_id)
    assert isinstance(payload, dict)
    receiver_fingerprint = _fingerprint(payload, "receiverFingerprint")
    receiver_public = _public_key(payload, "receiverPublicKey", receiver_fingerprint)
    accepted = (payload.get("acceptedReceiverFingerprint") or "").strip().lower()

    pin = known_machines.check(facts.to_machine, receiver_fingerprint)
    if pin.state == known_machines.CHANGED:
        if accepted != receiver_fingerprint:
            raise TransferRefused(
                f"The key of {facts.to_machine} has changed since this machine pinned it on {pin.pinned.pinned_utc}, "
                f"so nothing was sent. If you know why (a reinstalled machine), accept the new key in the cc-secrets "
                f"window on this machine when you send again.")
        known_machines.pin(facts.to_machine, receiver_fingerprint)
    elif pin.state == known_machines.NEW:
        known_machines.pin(facts.to_machine, receiver_fingerprint)

    entry = store.get(facts.entry)
    if entry is None:
        raise TransferRefused(f"No entry named '{facts.entry}' is on {facts.from_machine}. Nothing was sent.")

    own, _ = machine_key.load_or_create()
    envelope = sealing.seal(_entry_plaintext(entry), own.private, receiver_public,
                            facts.bound(own.fingerprint, receiver_fingerprint))
    audit.record(facts.entry, "transfer sent", "ok",
                 f"to {facts.to_machine} as {facts.target_name}; {facts.note()}", facts.approval())
    filelog.write(f"[transfer] send_half: {facts.transfer_id} sealed for {facts.to_machine}")
    return {"ok": True, "envelope": envelope, "senderPublicKey": own.public_b64, "senderFingerprint": own.fingerprint}


# --- The receiving machine ---------------------------------------------------------------------------------------

def _accepted_file() -> UserOnlyFile:
    return UserOnlyFile(paths.secrets_home() / ACCEPTED_FILE)


def _accepted(now: datetime) -> Dict[str, str]:
    file = _accepted_file()
    if not file.exists():
        return {}
    try:
        document = json.loads(file.read().decode("utf-8"))
        rows = {str(k): str(v) for k, v in document["accepted"].items()}
    except (ValueError, KeyError, TypeError, AttributeError) as exc:
        # Fail closed: without the list a replayed envelope could not be told from a new one.
        raise TransferRefused(f"{file.location} cannot be read ({type(exc).__name__}), so this machine cannot tell "
                              "whether it has received this transfer before. Nothing was stored.") from exc
    cutoff = now - ACCEPTED_KEEP
    return {k: v for k, v in rows.items() if datetime.fromisoformat(v) >= cutoff}


def _remember(transfer_id: str, now: datetime) -> None:
    rows = _accepted(now)
    rows[transfer_id] = now.isoformat(timespec="seconds")
    _accepted_file().write((json.dumps({"version": 1, "accepted": rows}, indent=2, sort_keys=True) + "\n")
                           .encode("utf-8"))


def receive_half(payload: object, transfer_id: str, store: SecretStore, audit: AuditLog,
                 now: Optional[datetime] = None) -> Dict[str, object]:
    """Open the envelope and store the entry. Returns {ok, stored}."""
    now = now or datetime.now(timezone.utc)
    facts = parse_facts(payload, transfer_id)
    assert isinstance(payload, dict)
    sender_fingerprint = _fingerprint(payload, "senderFingerprint")
    sender_public = _public_key(payload, "senderPublicKey", sender_fingerprint)
    receiver_fingerprint = _fingerprint(payload, "receiverFingerprint")
    envelope = _text(payload, "envelope")

    try:
        if facts.transfer_id in _accepted(now):
            raise TransferRefused(f"Transfer {facts.transfer_id} was already received on this machine; an envelope "
                                  "is accepted once. Nothing was stored.")
        _not_past(facts, now)
        own, _ = machine_key.load_or_create()
        if own.fingerprint != receiver_fingerprint:
            raise TransferRefused(f"The entry was sealed for a different key than this machine's, so it cannot be "
                                  f"opened here. Nothing was stored.")
        pin = known_machines.check(facts.from_machine, sender_fingerprint)
        if pin.state == known_machines.CHANGED:
            raise TransferRefused(
                f"The key of {facts.from_machine} has changed since this machine pinned it on "
                f"{pin.pinned.pinned_utc}, so nothing was stored. If you know why (a reinstalled machine), run "
                f"'cc-secrets trust-machine {facts.from_machine}' on this machine and send it again.")
        if store.get(facts.target_name) is not None and not facts.replace:
            raise TransferRefused(f"An entry named '{facts.target_name}' is already on {facts.to_machine}, and the "
                                  f"approval did not say it replaces it. Nothing was stored.")

        plaintext = sealing.open_envelope(envelope, own.private, sender_public,
                                          facts.bound(sender_fingerprint, receiver_fingerprint))
        # Accepted once the envelope proves genuine, before anything is stored: a second delivery of the same
        # envelope is refused even if storing this one fails part way.
        _remember(facts.transfer_id, now)
        if pin.state == known_machines.NEW:
            known_machines.pin(facts.from_machine, sender_fingerprint)
        record = json.loads(plaintext.decode("utf-8"))
        save_entry(store, audit, facts.target_name, str(record.get("username", "")), str(record["secret"]),
                   [str(d) for d in record.get("allowedDomains", [])], str(record.get("notes", "")),
                   [str(u) for u in record.get("uses", [])], str(record.get("envName", "")),
                   record.get("kind") == "setting", "transfer received",
                   f"from {facts.from_machine}; {facts.note()}", facts.approval())
    except (TransferRefused, sealing.EnvelopeError) as exc:
        audit.record(facts.target_name, "transfer received", "refused", f"from {facts.from_machine}; {exc}",
                     facts.approval())
        raise
    filelog.write(f"[transfer] receive_half: {facts.transfer_id} stored as {facts.target_name}")
    return {"ok": True, "stored": facts.target_name}
