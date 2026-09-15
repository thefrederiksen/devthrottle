"""Removing a secret from text before it leaves the tool.

Every string the tool prints, logs, or writes to the audit log passes through the process-wide
SCRUBBER first. The scrubber knows the secret of every entry this process has opened, in the forms a
command or a web page most often echoes it back: as typed, URL-encoded, JSON-escaped, and base64
(on its own and as the "username:secret" pair of an HTTP basic authorization header).

What this does NOT cover, stated plainly: a command that deliberately transforms the secret - reverses
it, hashes it, splits it across lines - produces text that contains no recognisable form of it. The
scrubber stops ACCIDENTAL exposure (a tool that echoes its input, an error that quotes a value). It is
not a defence against a command chosen in order to extract the secret; the per-entry "uses" setting
and the audit log are what limit and record that.
"""

from __future__ import annotations

import base64
import json
import threading
from typing import List
from urllib.parse import quote, quote_plus

REDACTED = "[REDACTED]"


def variants_for(secret: str, username: str = "") -> List[str]:
    """Every form of `secret` the scrubber removes, longest first so a longer form is never half-removed."""
    if not secret:
        raise ValueError("A secret variant list needs a non-empty secret.")
    forms = {
        secret,
        quote(secret, safe=""),
        quote_plus(secret),
        json.dumps(secret)[1:-1],
        base64.b64encode(secret.encode("utf-8")).decode("ascii"),
    }
    if username:
        forms.add(base64.b64encode(f"{username}:{secret}".encode("utf-8")).decode("ascii"))
    return sorted((f for f in forms if f), key=len, reverse=True)


class Scrubber:
    """Holds the secret forms this process must never emit, and removes them from text."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._variants: List[str] = []

    def add(self, secret: str, username: str = "") -> None:
        with self._lock:
            merged = set(self._variants) | set(variants_for(secret, username))
            self._variants = sorted(merged, key=len, reverse=True)

    def clear(self) -> None:
        with self._lock:
            self._variants = []

    def scrub(self, text: str) -> str:
        if text is None:
            return text
        with self._lock:
            variants = list(self._variants)
        for v in variants:
            text = text.replace(v, REDACTED)
        return text

    def contains(self, text: str) -> bool:
        with self._lock:
            variants = list(self._variants)
        return any(v in text for v in variants)


SCRUBBER = Scrubber()
