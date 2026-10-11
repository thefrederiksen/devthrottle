"""The sealed envelope a secret travels in between two machines (the Secret Handoff mission).

Only the receiving machine can open it, and only for the transfer the owner approved:

- the key is agreed twice with X25519 and the two results go through HKDF-SHA256 together: once from a fresh
  ephemeral key made for this envelope alone (so one stolen machine key never opens envelopes recorded earlier), and
  once from the SENDING machine's own static key (so the receiver knows which machine sealed it, not merely that
  somebody did);
- the entry is encrypted with AES-256-GCM, and the facts of the approval - the transfer id, the entry and the name it
  is stored under, both machines, the replace flag, the time it must arrive by and both machines' key fingerprints -
  are the additional authenticated data. Change any one of them, or hand the envelope to another machine or another
  transfer, and it does not open.

The Gateway and the Directors carry the envelope but hold no key that opens it.
"""

from __future__ import annotations

import base64
import json
import os
from typing import Dict

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives import serialization

from .errors import CcSecretsError

VERSION = 1
INFO = b"cc-secrets secret transfer v1"

# The facts bound into every envelope, in the order they are named here. All of them must be present.
FACTS = ("transferId", "entry", "targetName", "fromMachine", "toMachine", "replace", "expiresAtUtc",
         "senderFingerprint", "receiverFingerprint")


class EnvelopeError(CcSecretsError):
    """The envelope did not open: it was changed, sealed for another transfer or another machine, or is not an
    envelope at all. The message never says which, and never carries any of its bytes."""


def associated_data(facts: Dict[str, object]) -> bytes:
    """The approval's facts as canonical JSON: sorted keys, no spaces, exactly the FACTS and nothing else."""
    missing = [name for name in FACTS if name not in facts]
    if missing:
        raise CcSecretsError(f"The transfer is missing {', '.join(missing)}.")
    return json.dumps({name: facts[name] for name in FACTS}, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True).encode("ascii")


def _key(shared_ephemeral: bytes, shared_static: bytes, transfer_id: str) -> bytes:
    return HKDF(algorithm=hashes.SHA256(), length=32, salt=transfer_id.encode("ascii"),
                info=INFO).derive(shared_ephemeral + shared_static)


def _raw(public: X25519PublicKey) -> bytes:
    return public.public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)


def seal(plaintext: bytes, sender: X25519PrivateKey, receiver_public: bytes, facts: Dict[str, object]) -> str:
    """Seal `plaintext` to the receiving machine's public key. Returns the envelope as base64 text."""
    aad = associated_data(facts)
    receiver = X25519PublicKey.from_public_bytes(receiver_public)
    ephemeral = X25519PrivateKey.generate()
    key = _key(ephemeral.exchange(receiver), sender.exchange(receiver), str(facts["transferId"]))
    nonce = os.urandom(12)
    sealed = AESGCM(key).encrypt(nonce, plaintext, aad)
    document = {"v": VERSION, "eph": base64.b64encode(_raw(ephemeral.public_key())).decode("ascii"),
                "nonce": base64.b64encode(nonce).decode("ascii"), "ct": base64.b64encode(sealed).decode("ascii")}
    return base64.b64encode(json.dumps(document, separators=(",", ":")).encode("ascii")).decode("ascii")


def open_envelope(envelope: str, receiver: X25519PrivateKey, sender_public: bytes, facts: Dict[str, object]) -> bytes:
    """Open an envelope sealed by the machine whose public key is `sender_public`, for exactly these facts."""
    aad = associated_data(facts)
    try:
        document = json.loads(base64.b64decode(envelope, validate=True).decode("ascii"))
        if not isinstance(document, dict) or document.get("v") != VERSION:
            raise ValueError("not a version 1 envelope")
        ephemeral = X25519PublicKey.from_public_bytes(base64.b64decode(document["eph"], validate=True))
        nonce = base64.b64decode(document["nonce"], validate=True)
        sealed = base64.b64decode(document["ct"], validate=True)
        sender = X25519PublicKey.from_public_bytes(sender_public)
        key = _key(receiver.exchange(ephemeral), receiver.exchange(sender), str(facts["transferId"]))
        return AESGCM(key).decrypt(nonce, sealed, aad)
    except (InvalidTag, ValueError, KeyError, TypeError, UnicodeDecodeError) as exc:
        raise EnvelopeError("The sealed entry could not be opened: it was changed on the way, or it was sealed for "
                            "another transfer or another machine. Nothing was stored.") from exc
