"""The secret store: named entries in one plain JSON file, private to this user.

A secret is held in a `Secret`, whose text form is always "<hidden>". Printing an entry, formatting it
into an error, or logging it therefore cannot show the secret by accident; only `reveal()` returns the
value, and only the places that act with it (the command runner, the browser login, and saving the
store) call it.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Dict, List, Optional

from . import filelog
from .storefile import StoreFile

NAME_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._-]{0,63}$")
MIN_SECRET_LENGTH = 4
USES = ("login", "run")
STORE_VERSION = 1


class Secret:
    """A secret value that never renders as text."""

    __slots__ = ("_value",)

    def __init__(self, value: str) -> None:
        self._value = value

    def reveal(self) -> str:
        return self._value

    def __repr__(self) -> str:
        return "Secret(<hidden>)"

    __str__ = __repr__

    def __format__(self, spec: str) -> str:
        return "<hidden>"


class EntryNotAvailableError(LookupError):
    """No entry by that name is available for the requested use."""


def validate_name(name: str) -> str:
    if not NAME_PATTERN.match(name or ""):
        raise ValueError(
            f"'{name}' is not a valid entry name: use lowercase letters, digits, dot, dash or underscore, "
            "starting with a letter or digit, at most 64 characters."
        )
    return name


def normalize_domain(value: str) -> str:
    """A host name or a '*.example.com' wildcard, lowercased, with any scheme, port or path removed."""
    text = value.strip().lower()
    text = re.sub(r"^[a-z][a-z0-9+.-]*://", "", text)
    text = text.split("/", 1)[0].split(":", 1)[0]
    if not text or not re.match(r"^(\*\.)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$", text):
        raise ValueError(f"'{value}' is not a host name or a '*.domain' wildcard.")
    return text


def host_allowed(host: str, allowed: List[str]) -> bool:
    """True when `host` exactly equals an allowed host, or is a subdomain of an allowed '*.domain'.

    A wildcard does not match its own apex: '*.example.com' allows 'login.example.com' but not
    'example.com'. Nothing is implied - the owner lists what the password may be typed into.
    """
    host = (host or "").lower().rstrip(".")
    if not host:
        return False
    for pattern in allowed:
        if pattern.startswith("*."):
            if host.endswith(pattern[1:]) and host != pattern[2:]:
                return True
        elif host == pattern:
            return True
    return False


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


@dataclass
class Entry:
    name: str
    username: str
    secret: Secret
    allowed_domains: List[str] = field(default_factory=list)
    notes: str = ""
    agents_may_use: bool = False
    uses: List[str] = field(default_factory=lambda: list(USES))
    created_utc: str = field(default_factory=_utc_now)
    updated_utc: str = field(default_factory=_utc_now)

    def public_view(self) -> Dict[str, object]:
        """Everything about the entry except the secret."""
        return {
            "name": self.name,
            "username": self.username,
            "allowedDomains": list(self.allowed_domains),
            "uses": list(self.uses),
            "agentsMayUse": self.agents_may_use,
            "notes": self.notes,
            "updatedUtc": self.updated_utc,
        }

    def _to_record(self) -> Dict[str, object]:
        record = self.public_view()
        record["secret"] = self.secret.reveal()
        record["createdUtc"] = self.created_utc
        return record

    @staticmethod
    def _from_record(record: Dict[str, object]) -> "Entry":
        return Entry(
            name=str(record["name"]),
            username=str(record.get("username", "")),
            secret=Secret(str(record["secret"])),
            allowed_domains=[str(d) for d in record.get("allowedDomains", [])],
            notes=str(record.get("notes", "")),
            agents_may_use=bool(record.get("agentsMayUse", False)),
            uses=[str(u) for u in record.get("uses", list(USES))],
            created_utc=str(record.get("createdUtc", "")),
            updated_utc=str(record.get("updatedUtc", "")),
        )


def make_entry(name: str, username: str, secret: str, allowed_domains: List[str], notes: str,
               agents_may_use: bool, uses: List[str]) -> Entry:
    """Validate the owner's input and build an entry."""
    validate_name(name)
    if len(secret) < MIN_SECRET_LENGTH:
        raise ValueError(f"The secret must be at least {MIN_SECRET_LENGTH} characters.")
    bad = [u for u in uses if u not in USES]
    if bad or not uses:
        raise ValueError(f"Uses must be one or more of: {', '.join(USES)}.")
    return Entry(
        name=name,
        username=username,
        secret=Secret(secret),
        allowed_domains=sorted({normalize_domain(d) for d in allowed_domains if d.strip()}),
        notes=notes,
        agents_may_use=agents_may_use,
        uses=[u for u in USES if u in uses],
    )


class SecretStore:
    """The store for this user on this machine."""

    def __init__(self, file: StoreFile) -> None:
        self._file = file

    @property
    def location(self):
        return self._file.location

    def entries(self) -> List[Entry]:
        if not self._file.exists():
            return []
        document = json.loads(self._file.read().decode("utf-8"))
        if document.get("version") != STORE_VERSION:
            raise ValueError(f"Secret store version {document.get('version')} is not supported.")
        return [Entry._from_record(r) for r in document.get("entries", [])]

    def get(self, name: str) -> Optional[Entry]:
        return next((e for e in self.entries() if e.name == name), None)

    def put(self, entry: Entry) -> bool:
        """Add or replace an entry. Returns True when an existing entry was replaced."""
        filelog.write(f"[SecretStore] put: name={entry.name}")
        entries = self.entries()
        existing = next((e for e in entries if e.name == entry.name), None)
        if existing is not None:
            entry.created_utc = existing.created_utc
            entries = [e for e in entries if e.name != entry.name]
        entry.updated_utc = _utc_now()
        entries.append(entry)
        self._save(entries)
        return existing is not None

    def remove(self, name: str) -> bool:
        filelog.write(f"[SecretStore] remove: name={name}")
        entries = self.entries()
        remaining = [e for e in entries if e.name != name]
        if len(remaining) == len(entries):
            return False
        self._save(remaining)
        return True

    def agent_entries(self) -> List[Entry]:
        """The entries agents may use. An entry the owner did not mark is not listed at all."""
        return sorted((e for e in self.entries() if e.agents_may_use), key=lambda e: e.name)

    def entry_for_agent(self, name: str, use: str) -> Entry:
        """The entry, when agents may use it for `use`. Otherwise an error whose message is the same
        for a missing entry and for one the owner kept back, so a refusal does not reveal which."""
        entry = self.get(name)
        if entry is None or not entry.agents_may_use:
            raise EntryNotAvailableError(f"No secret named '{name}' is available to agents on this machine.")
        if use not in entry.uses:
            raise EntryNotAvailableError(
                f"Secret '{name}' is not allowed for '{use}'. It may be used for: {', '.join(entry.uses)}."
            )
        return entry

    def _save(self, entries: List[Entry]) -> None:
        document = {"version": STORE_VERSION,
                    "entries": [e._to_record() for e in sorted(entries, key=lambda e: e.name)]}
        self._file.write((json.dumps(document, indent=2) + "\n").encode("utf-8"))
