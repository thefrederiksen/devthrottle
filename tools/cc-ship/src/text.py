"""All cc-ship output is ASCII. Session-written text is folded to ASCII, never dropped."""

from __future__ import annotations

import unicodedata

_REPLACEMENTS = {
    "—": "-", "–": "-", "‒": "-", "−": "-",
    "‘": "'", "’": "'", "“": '"', "”": '"',
    "…": "...", "→": "->", "←": "<-", " ": " ",
    "•": "-", "×": "x",
}


def _fold(char: str) -> str:
    if ord(char) < 128:
        return char
    if char in _REPLACEMENTS:
        return _REPLACEMENTS[char]
    # Accented letters keep their base letter; anything else is marked, not dropped.
    base = unicodedata.normalize("NFKD", char).encode("ascii", "ignore").decode("ascii")
    return base or "?"


def ascii_safe(value: str) -> str:
    return "".join(_fold(c) for c in value)
