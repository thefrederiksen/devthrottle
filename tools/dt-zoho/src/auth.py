"""Signing in to Zoho: an access token from the refresh token, kept for its hour in a sealed file.

Why a cache at all: Zoho gives out only about ten access tokens per refresh token in ten minutes, and answers the
eleventh with "You have made too many requests continuously". One token per command would lock the key out after
ten commands. So a token is kept and reused until five minutes before it expires.

Why it is sealed: the access token reads and sends the owner's mail for an hour, so it is never written in plain
text. The file holds it encrypted with AES-256-GCM under a key derived (HKDF-SHA256, a fresh random salt each
write) from the client secret and the refresh token - the two values cc-secrets supplies on every run and never
writes to disk. Anyone who can open the file therefore already holds what is needed to mint a token directly, so the
file gives away nothing new. The client id and the data centre are bound in as authenticated data: a file written
for another key or another data centre does not open, and is replaced by a fresh token. A rotated secret or refresh
token makes the old file unreadable in the same way, which is the right outcome.
"""

from __future__ import annotations

import base64
import json
import os
import time
import urllib.parse
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Optional

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

from . import config, transport
from .errors import DtZohoError

CACHE_VERSION = 1
CACHE_INFO = b"dt-zoho access token cache v1"
REUSE_MARGIN_SECONDS = 300      # a token is replaced this long before Zoho says it expires
RATE_LIMIT_HINT = "too many requests"


@dataclass
class Token:
    value: str
    expires_at: float
    source: str        # "cache" or "new"
    scope: str = ""

    def __repr__(self) -> str:      # never print the token itself, even by accident
        return f"Token(source={self.source!r}, expires_at={self.expires_at!r})"


def cache_path() -> Path:
    return config.config_dir() / "token.sealed"


def _key(creds: config.Credentials, salt: bytes) -> bytes:
    material = (creds.client_secret + "\n" + creds.refresh_token).encode("utf-8")
    return HKDF(algorithm=hashes.SHA256(), length=32, salt=salt, info=CACHE_INFO).derive(material)


def _bound(creds: config.Credentials, centre: config.DataCentre) -> bytes:
    return f"{creds.client_id}|{centre.code}".encode("utf-8")


def seal(creds: config.Credentials, centre: config.DataCentre, token: Token) -> bytes:
    salt, nonce = os.urandom(16), os.urandom(12)
    plain = json.dumps({"access_token": token.value, "expires_at": token.expires_at, "scope": token.scope})
    data = AESGCM(_key(creds, salt)).encrypt(nonce, plain.encode("utf-8"), _bound(creds, centre))
    sealed = {"version": CACHE_VERSION, "salt": base64.b64encode(salt).decode(),
              "nonce": base64.b64encode(nonce).decode(), "data": base64.b64encode(data).decode()}
    return json.dumps(sealed).encode("utf-8")


def unseal(creds: config.Credentials, centre: config.DataCentre, blob: bytes) -> Optional[Token]:
    """The cached token, or None when the file was sealed under another key, data centre or format."""
    try:
        sealed = json.loads(blob)
        if sealed.get("version") != CACHE_VERSION:
            return None
        salt, nonce, data = (base64.b64decode(sealed[k]) for k in ("salt", "nonce", "data"))
        plain = AESGCM(_key(creds, salt)).decrypt(nonce, data, _bound(creds, centre))
    except (ValueError, KeyError, TypeError, InvalidTag):
        return None
    record = json.loads(plain)
    return Token(record["access_token"], float(record["expires_at"]), "cache", record.get("scope", ""))


def _write_cache(blob: bytes) -> None:
    path = cache_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as handle:
        handle.write(blob)
    os.replace(tmp, path)


def forget() -> bool:
    """Delete the cached token. True when there was one."""
    path = cache_path()
    if path.exists():
        path.unlink()
        return True
    return False


def access_token(creds: config.Credentials, centre: config.DataCentre,
                 now: Callable[[], float] = time.time) -> Token:
    """A token with at least five minutes left: the cached one, or a new one from Zoho (then cached)."""
    path = cache_path()
    if path.exists():
        cached = unseal(creds, centre, path.read_bytes())
        if cached is not None and cached.expires_at - REUSE_MARGIN_SECONDS > now():
            return cached
    token = request_token(creds, centre, now)
    _write_cache(seal(creds, centre, token))
    return token


def request_token(creds: config.Credentials, centre: config.DataCentre,
                  now: Callable[[], float] = time.time) -> Token:
    """Exchange the refresh token for a new access token. Never puts a token in an error message."""
    form = urllib.parse.urlencode({"client_id": creds.client_id, "client_secret": creds.client_secret,
                                   "grant_type": "refresh_token", "refresh_token": creds.refresh_token}).encode()
    answer = transport.send("POST", centre.accounts + "/oauth/v2/token",
                            {"Content-Type": "application/x-www-form-urlencoded", "Accept": "application/json"},
                            form)
    text = answer.body.decode("utf-8", errors="replace")
    try:
        data = json.loads(text)
    except json.JSONDecodeError:
        data = None
    if isinstance(data, dict) and answer.status == 200 and data.get("access_token"):
        return Token(data["access_token"], now() + float(data.get("expires_in", 3600)), "new",
                     str(data.get("scope", "")))
    said = {k: v for k, v in data.items() if "token" not in k.lower()} if isinstance(data, dict) else text[:200]
    if RATE_LIMIT_HINT in text.lower():
        raise DtZohoError(
            f"Zoho refused a new access token: too many were asked for in the last ten minutes (about ten are "
            f"allowed). Wait ten minutes and run the command again. Zoho said: {said}")
    if isinstance(data, dict) and data.get("error") in ("invalid_code", "invalid_client"):
        raise DtZohoError(
            f"Zoho refused the key at {centre.accounts}: {said}. Either the refresh token was revoked, or the key "
            f"belongs to another data centre (this one is '{centre.code}', see dt-zoho settings show).")
    raise DtZohoError(f"Zoho's sign-in at {centre.accounts} answered {answer.status} without a token: {said}")
