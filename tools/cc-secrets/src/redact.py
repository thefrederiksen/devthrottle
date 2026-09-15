"""Removing a secret from what the tool hands back.

Every string the tool prints, logs, or writes to the audit log passes through the process-wide
SCRUBBER first, and a command's raw output is scrubbed as BYTES before it is decoded. The scrubber knows
the secret of every entry this process has read, in the forms a command or a web page echoes it back
without anyone meaning to:

- as typed; JSON-escaped; HTML-escaped;
- percent-encoded the ways URLs and forms do it (Python quote and quote_plus, JavaScript
  encodeURIComponent, browser form encoding), with upper- and lower-case escapes;
- base64 and base64url, with and without padding, of the secret and of "username:secret";
- hexadecimal;

and each of those in every encoding a command's output may use here: UTF-8, UTF-16 in both byte
orders, and on Windows the console, OEM and ANSI code pages (what `cmd /c echo` writes).

What this does NOT cover, stated plainly: a command that deliberately transforms the secret - reverses
it, hashes it, splits it across lines - produces output with no recognisable form of it. The scrubber
stops ACCIDENTAL exposure. It is not a defence against a command chosen to extract the secret; the
per-entry "uses" setting and the audit log are what limit and record that.
"""

from __future__ import annotations

import base64
import codecs
import html
import json
import locale
import re
import sys
import threading
from typing import List, Tuple
from urllib.parse import quote, quote_plus

REDACTED = "[REDACTED]"


def _lower_percent(text: str) -> str:
    return re.sub(r"%[0-9A-F]{2}", lambda m: m.group(0).lower(), text)


def _base64_forms(raw: bytes) -> set:
    standard = base64.b64encode(raw).decode("ascii")
    url_safe = base64.urlsafe_b64encode(raw).decode("ascii")
    return {standard, standard.rstrip("="), url_safe, url_safe.rstrip("=")}


def variants_for(secret: str, username: str = "") -> List[str]:
    """Every text form of `secret` the scrubber removes, longest first so a longer form is never half-removed."""
    if not secret:
        raise ValueError("A secret variant list needs a non-empty secret.")
    forms = {
        secret,
        json.dumps(secret)[1:-1],
        json.dumps(secret, ensure_ascii=False)[1:-1],
        html.escape(secret, quote=True),
        html.escape(secret, quote=False),
    }
    for percent in (quote(secret, safe=""), quote_plus(secret), quote(secret, safe="-_.!~*'()"),
                    quote_plus(secret, safe="*-._")):
        forms.add(percent)
        forms.add(_lower_percent(percent))
    raw = secret.encode("utf-8")
    forms |= _base64_forms(raw)
    forms |= {raw.hex(), raw.hex().upper()}
    if username:
        forms |= _base64_forms(f"{username}:{secret}".encode("utf-8"))
    return sorted((f for f in forms if f), key=len, reverse=True)


def output_encodings() -> List[str]:
    """The encodings a command's output may be in on this machine, most likely first."""
    names = ["utf-8", "utf-16-le", "utf-16-be"]
    if sys.platform == "win32":
        import ctypes

        kernel32 = ctypes.windll.kernel32
        for code_page in (kernel32.GetConsoleOutputCP(), kernel32.GetOEMCP(), kernel32.GetACP()):
            if code_page:
                names.append(f"cp{code_page}")
    names += [locale.getpreferredencoding(False), "latin-1"]
    result: List[str] = []
    for name in names:
        try:
            canonical = codecs.lookup(name).name
        except LookupError:
            continue
        if canonical not in result:
            result.append(canonical)
    return result


def decode_output(data: bytes) -> str:
    """Decode a command's output: UTF-16 when it looks like UTF-16, else UTF-8, else the code pages."""
    if data.startswith(codecs.BOM_UTF16_LE):
        return data[2:].decode("utf-16-le", "replace")
    if data.startswith(codecs.BOM_UTF16_BE):
        return data[2:].decode("utf-16-be", "replace")
    if len(data) >= 4:
        if data[1::2].count(0) >= len(data) // 4:
            return data.decode("utf-16-le", "replace")
        if data[0::2].count(0) >= len(data) // 4:
            return data.decode("utf-16-be", "replace")
    for encoding in [e for e in output_encodings() if not e.startswith("utf-16")]:
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue
    return data.decode("latin-1")


class Scrubber:
    """Holds the secret forms this process must never emit, and removes them from text and bytes."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._variants: List[str] = []
        self._needles: List[Tuple[bytes, bytes]] = []

    def add(self, secret: str, username: str = "") -> None:
        with self._lock:
            merged = set(self._variants) | set(variants_for(secret, username))
            self._variants = sorted(merged, key=len, reverse=True)
            needles = {}
            for encoding in output_encodings():
                marker = REDACTED.encode(encoding)
                for variant in self._variants:
                    try:
                        needles.setdefault(variant.encode(encoding), marker)
                    except UnicodeEncodeError:
                        continue
            self._needles = sorted(needles.items(), key=lambda item: len(item[0]), reverse=True)

    def clear(self) -> None:
        with self._lock:
            self._variants = []
            self._needles = []

    def scrub(self, text: str) -> str:
        if text is None:
            return text
        with self._lock:
            variants = list(self._variants)
        for v in variants:
            text = text.replace(v, REDACTED)
        return text

    def scrub_bytes(self, data: bytes) -> bytes:
        with self._lock:
            needles = list(self._needles)
        for needle, marker in needles:
            data = data.replace(needle, marker)
        return data

    def decode_scrubbed(self, data: bytes) -> str:
        """Scrub raw output as bytes in every encoding, decode it, then scrub the text again."""
        return self.scrub(decode_output(self.scrub_bytes(data)))

    def contains(self, text: str) -> bool:
        with self._lock:
            variants = list(self._variants)
        return any(v in text for v in variants)


SCRUBBER = Scrubber()
