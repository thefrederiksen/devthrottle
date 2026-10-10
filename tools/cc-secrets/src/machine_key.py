"""This machine's key pair for receiving a secret from another of the owner's machines (the Secret Handoff mission).

X25519. The pair is made the first time it is needed and kept in `machine-key.json` in the private secrets folder,
written exactly the way the store is (a file private to this user from the moment it exists, replaced atomically).
The PUBLIC half is what the Director publishes to the Gateway on every Hello, so other machines can seal a secret to
this one; the private half never leaves this folder, and is handed to the scrubber the moment it is read so no output
or log line of this process can carry it.

The fingerprint of a public key is the lower-case hex of SHA-256 over its 32 bytes - the Gateway computes the same
value, so the two can be compared, and the sending machine pins it (known_machines).
"""

from __future__ import annotations

import base64
import hashlib
import json
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Tuple

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey

from . import filelog, paths
from .errors import CcSecretsError
from .redact import SCRUBBER
from .storefile import UserOnlyFile

KEY_FILE = "machine-key.json"
KEY_VERSION = 1


def fingerprint(public_key: bytes) -> str:
    """Lower-case hex of SHA-256 over the 32 public key bytes."""
    return hashlib.sha256(public_key).hexdigest()


def short_fingerprint(value: str) -> str:
    """The first 16 hex digits in groups of four, for a person to compare by eye: 'ab12 cd34 ef56 7890'."""
    return " ".join(value[i:i + 4] for i in range(0, 16, 4))


def decode_public_key(text: str) -> bytes:
    """32 key bytes from base64, or a CcSecretsError that says what was wrong (never the text itself)."""
    try:
        raw = base64.b64decode(text, validate=True)
    except (ValueError, TypeError) as exc:
        raise CcSecretsError("A public key from the Gateway is not valid base64.") from exc
    if len(raw) != 32:
        raise CcSecretsError(f"A public key from the Gateway has {len(raw)} bytes; an X25519 key has 32.")
    return raw


def _raw_public(private: X25519PrivateKey) -> bytes:
    return private.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)


@dataclass(frozen=True)
class MachineKey:
    """The pair. Its text form never shows the private half."""
    private: X25519PrivateKey = field(repr=False)
    public: bytes

    @property
    def public_b64(self) -> str:
        return base64.b64encode(self.public).decode("ascii")

    @property
    def fingerprint(self) -> str:
        return fingerprint(self.public)


def _file() -> UserOnlyFile:
    return UserOnlyFile(paths.secrets_home() / KEY_FILE)


def load_or_create() -> Tuple[MachineKey, bool]:
    """This machine's key pair, made and saved the first time. Returns (key, created)."""
    file = _file()
    if file.exists():
        document = json.loads(file.read().decode("utf-8"))
        if not isinstance(document, dict) or document.get("version") != KEY_VERSION:
            raise CcSecretsError(f"{file.location} is not a machine key this cc-secrets reads (version "
                                 f"{document.get('version') if isinstance(document, dict) else None!r}).")
        private_text = str(document.get("privateKey", ""))
        SCRUBBER.add(private_text)
        try:
            private = X25519PrivateKey.from_private_bytes(base64.b64decode(private_text, validate=True))
        except ValueError as exc:
            raise CcSecretsError(f"The private key in {file.location} cannot be read.") from exc
        key = MachineKey(private=private, public=_raw_public(private))
        if str(document.get("publicKey", "")) != key.public_b64:
            raise CcSecretsError(f"The public key in {file.location} does not belong to its private key.")
        return key, False
    private = X25519PrivateKey.generate()
    private_text = base64.b64encode(private.private_bytes(serialization.Encoding.Raw, serialization.PrivateFormat.Raw,
                                                          serialization.NoEncryption())).decode("ascii")
    SCRUBBER.add(private_text)
    key = MachineKey(private=private, public=_raw_public(private))
    document = {"version": KEY_VERSION, "publicKey": key.public_b64, "privateKey": private_text,
                "createdUtc": datetime.now(timezone.utc).isoformat(timespec="seconds")}
    file.write((json.dumps(document, indent=2) + "\n").encode("utf-8"))
    filelog.write(f"[machine_key] made this machine's key pair, fingerprint {short_fingerprint(key.fingerprint)}")
    return key, True
