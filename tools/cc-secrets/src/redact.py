"""Removing a secret from what the tool hands back.

Every string the tool prints, logs, or writes to the audit log passes through the process-wide
SCRUBBER first, and a command's raw output is scrubbed as BYTES before it is decoded. The scrubber knows
the secret of every entry this process has read, in the forms a command or a web page echoes it back
without anyone meaning to:

- as typed; JSON-escaped; HTML-escaped; the way Python prints a string (repr, ascii, inside a dict or a
  list), with either quote mark;
- percent-encoded the ways URLs and forms do it (Python quote and quote_plus, JavaScript
  encodeURIComponent, browser form encoding), with upper- and lower-case escapes;
- base64 and base64url, with and without padding, of the secret and of "username:secret";
- hexadecimal;
- the secret ENCODED BY EVERY CODEC Python has - raw bytes, and the way Python prints those bytes
  (`b'...'`). Listing a few encodings was not enough: a diagnostic printing `s.encode('utf-16-le')` or
  Windows-1252 bytes handed the whole secret back (review of pull request 2891).

Each of those is then matched as bytes in every encoding a command's output may use here, so it is caught
whether the command wrote UTF-8, UTF-16 or a Windows code page.

A secret that is part of the replacement marker itself ("DACT" inside "[REDACTED]") would be spelled out
again by every replacement, so it is refused when it is added and when it is used (`redaction_conflict`).
And any output that still carries a secret after every replacement - a marker that happens to spell one out
together with the text beside it - is WITHHELD whole rather than shown (review of pull request 2891).

What this does NOT cover, stated plainly: a command that deliberately transforms the secret - reverses
it, hashes it, splits it across lines - produces output with no recognisable form of it. The scrubber
stops ACCIDENTAL exposure. It is not a defence against a command chosen to extract the secret; the
per-entry "uses" setting and the audit log are what limit and record that.
"""

from __future__ import annotations

import base64
import codecs
import encodings.aliases
import html
import json
import locale
import re
import sys
import threading
from typing import Dict, List, Optional, Tuple
from urllib.parse import quote, quote_plus

REDACTED = "[REDACTED]"
# Replaces a whole output that still carries a secret after every form was replaced - which can only happen
# when the replacement marker itself spells part of the secret (review of pull request 2891).
WITHHELD = "(cc-secrets withheld this output: it could not be redacted safely)"


def _lower_percent(text: str) -> str:
    return re.sub(r"%[0-9A-F]{2}", lambda m: m.group(0).lower(), text)


def _base64_forms(raw: bytes) -> set:
    standard = base64.b64encode(raw).decode("ascii")
    url_safe = base64.urlsafe_b64encode(raw).decode("ascii")
    return {standard, standard.rstrip("="), url_safe, url_safe.rstrip("=")}


def _python_forms(secret: str) -> set:
    """How Python prints the secret inside a string literal - print(dict), print(list), repr(), ascii().
    Python picks a quote mark and escapes the other characters, so a secret holding a quote mark, a
    backslash or a non-ASCII letter comes out changed and would not match as typed."""
    backslash = chr(92)
    forms = {repr(secret)[1:-1], ascii(secret)[1:-1]}
    for quote_mark in ("'", '"'):
        escaped = secret.replace(backslash, backslash * 2).replace(quote_mark, backslash + quote_mark)
        forms.add(escaped)
        forms.add(escaped.encode("ascii", "backslashreplace").decode("ascii"))
    return forms


def text_codecs() -> List[str]:
    """Every codec in this Python that can turn text into bytes, plus this machine's console encodings.

    Built from the standard library's own alias table rather than a list of the encodings we happened to
    think of, so a secret printed as bytes in any of them is still recognised.
    """
    names = set(encodings.aliases.aliases.values()) | set(output_encodings())
    usable = []
    for name in sorted(names):
        try:
            if isinstance("probe".encode(name), bytes):
                usable.append(name)
        except (LookupError, UnicodeError, TypeError, ValueError):
            continue
    return usable


def encoded_secret_bytes(secret: str) -> Dict[bytes, str]:
    """{the secret encoded: the codec that produced it}, one entry per distinct byte string."""
    found: Dict[bytes, str] = {}
    for name in text_codecs():
        try:
            raw = secret.encode(name)
        except (LookupError, UnicodeError, TypeError, ValueError):
            continue
        if raw and raw not in found:
            found[raw] = name
    return found


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
    forms |= _python_forms(secret)
    for percent in (quote(secret, safe=""), quote_plus(secret), quote(secret, safe="-_.!~*'()"),
                    quote_plus(secret, safe="*-._")):
        forms.add(percent)
        forms.add(_lower_percent(percent))
    raw = secret.encode("utf-8")
    forms |= _base64_forms(raw)
    forms |= {raw.hex(), raw.hex().upper()}
    if username:
        forms |= _base64_forms(f"{username}:{secret}".encode("utf-8"))
    # How Python prints those bytes: print(s.encode('utf-16-le')), a bytearray, a list of them.
    forms |= {repr(encoded)[2:-1] for encoded in encoded_secret_bytes(secret)}
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


def _needle_pairs(variants: List[str], secret: str) -> List[Tuple[bytes, bytes]]:
    """(bytes to remove, the marker that replaces them): every text form in every encoding this machine's
    commands write, and the secret encoded by any codec at all."""
    pairs: List[Tuple[bytes, bytes]] = []
    for encoding in output_encodings():
        marker = REDACTED.encode(encoding)
        for variant in variants:
            try:
                pairs.append((variant.encode(encoding), marker))
            except UnicodeEncodeError:
                continue
    for raw, codec_name in encoded_secret_bytes(secret).items():
        try:
            marker = REDACTED.encode(codec_name)
        except (LookupError, UnicodeError, TypeError, ValueError):
            marker = REDACTED.encode("ascii")
        pairs.append((raw, marker))
    return pairs


def redaction_conflict(secret: str, username: str = "") -> Optional[str]:
    """Why `secret` cannot be hidden, or None. A secret that is part of the replacement marker "[REDACTED]"
    - "RED", "ACT" - is spelled out again by every replacement, so it could never be shown hidden (review of
    pull request 2891). A secret that only meets the marker at an edge is not refused: when a replacement
    happens to spell it out next to the surrounding output, the whole output is withheld instead."""
    variants = variants_for(secret, username)
    withheld = WITHHELD.encode("ascii")
    if any(v in REDACTED or v in WITHHELD for v in variants) or any(
            needle in marker or needle in withheld for needle, marker in _needle_pairs(variants, secret)):
        # The marker is not quoted: it contains the secret, so quoting it would spell the secret out.
        return ("it is part of the text cc-secrets shows in place of a password, or of its notice for withheld "
                "output, so it could not be hidden. Use a different password for this entry")
    return None


class Scrubber:
    """Holds the secret forms this process must never emit, and removes them from text and bytes."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._variants: List[str] = []
        self._needles: List[Tuple[bytes, bytes]] = []

    def add(self, secret: str, username: str = "") -> None:
        """Remember `secret` so it is removed from everything this process emits. Always registers, even a
        secret that conflicts with the marker: reading the store registers every entry, and one bad entry must
        not stop the others being scrubbed. Commands that USE a secret refuse a conflicting one first."""
        with self._lock:
            merged = set(self._variants) | set(variants_for(secret, username))
            self._variants = sorted(merged, key=len, reverse=True)
            needles = dict(self._needles)
            for needle, marker in _needle_pairs(self._variants, secret):
                needles.setdefault(needle, marker)
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
        if any(v in text for v in variants):
            return WITHHELD
        return text

    def scrub_bytes(self, data: bytes) -> bytes:
        with self._lock:
            needles = list(self._needles)
        for needle, marker in needles:
            data = data.replace(needle, marker)
        if any(needle in data for needle, _ in needles):
            return WITHHELD.encode("ascii")
        return data

    def decode_scrubbed(self, data: bytes) -> str:
        """Scrub raw output as bytes in every encoding, decode it, then scrub the text again."""
        return self.scrub(decode_output(self.scrub_bytes(data)))

    def contains(self, text: str) -> bool:
        with self._lock:
            variants = list(self._variants)
        return any(v in text for v in variants)


SCRUBBER = Scrubber()
