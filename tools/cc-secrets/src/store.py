"""The secret store: named entries in one plain JSON file, private to this user.

A secret is held in a `Secret`, whose text form is always "<hidden>". Printing an entry, formatting it
into an error, or logging it therefore cannot show the secret by accident; only `reveal()` returns the
value, and only the places that act with it (the command runner, the browser login, and saving the
store) call it.

Reading the store hands every secret in it to the scrubber before anything else is done with the file,
so no later output, log line or error message of this process can carry any of them.

An entry's allowed addresses are ORIGINS: scheme, host and port, compared exactly. "example.com" means
https://example.com on port 443 and nothing else; http://example.com or https://example.com:8443 are
different sites. A wildcard '*.example.com' allows subdomains on the same scheme and port, not the apex.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Dict, List, Optional, Tuple
from urllib.parse import urlsplit

from . import filelog
from .errors import CcSecretsError, InputError, StoreFormatError
from .redact import SCRUBBER, redaction_conflict
from .storefile import StoreFile

NAME_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._-]{0,63}$")
_HOST_PATTERN = re.compile(r"^(\*\.)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$")
DEFAULT_PORTS = {"http": 80, "https": 443}
MIN_SECRET_LENGTH = 4
USES = ("login", "run")
KIND_SECRET = "secret"
KIND_SETTING = "setting"
KINDS = (KIND_SECRET, KIND_SETTING)
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


class EntryNotAvailableError(CcSecretsError, LookupError):
    """No entry by that name is available for the requested use."""


def validate_name(name: str) -> str:
    if not NAME_PATTERN.match(name or ""):
        raise InputError(
            f"'{name}' is not a valid entry name: use lowercase letters, digits, dot, dash or underscore, "
            "starting with a letter or digit, at most 64 characters."
        )
    return name


def normalize_origin(value: str) -> str:
    """The owner's address as a canonical origin: scheme://host[:port], the port left out when it is the
    scheme's default. No scheme means https. A path is dropped; a user name, another scheme, a bare
    top-level wildcard such as '*.com', or anything that is not a host name is refused."""
    text = value.strip()
    if not text:
        raise InputError("An allowed address cannot be empty.")
    if "://" not in text:
        text = "https://" + text
    parts = urlsplit(text)
    scheme = parts.scheme.lower()
    if scheme not in DEFAULT_PORTS:
        raise InputError(f"'{value}' must be an http or https address.")
    if "@" in parts.netloc:
        raise InputError(f"'{value}' must not contain a user name.")
    host = (parts.hostname or "").rstrip(".")
    if not _HOST_PATTERN.match(host):
        raise InputError(f"'{value}' is not a host name or a '*.domain' wildcard.")
    if host.startswith("*.") and "." not in host[2:]:
        raise InputError(f"'{value}' is too broad: a wildcard needs at least a name and a top-level domain, "
                         "for example '*.example.com'.")
    try:
        port = parts.port
    except ValueError as exc:
        raise InputError(f"'{value}' has a port that is not a number between 0 and 65535.") from exc
    port_text = "" if port is None or port == DEFAULT_PORTS[scheme] else f":{port}"
    return f"{scheme}://{host}{port_text}"


def _origin_parts(url: str) -> Optional[Tuple[str, str, int]]:
    parts = urlsplit(url or "")
    scheme = parts.scheme.lower()
    if scheme not in DEFAULT_PORTS or "@" in parts.netloc:
        return None
    host = (parts.hostname or "").lower().rstrip(".")
    if not host:
        return None
    try:
        port = parts.port
    except ValueError:
        return None
    return scheme, host, port if port is not None else DEFAULT_PORTS[scheme]


def origin_of(url: str) -> str:
    """scheme://host:port of `url` for messages, or the start of the text when it is not an http address."""
    parts = _origin_parts(url)
    if parts is None:
        return (url or "")[:40]
    return f"{parts[0]}://{parts[1]}:{parts[2]}"


def origin_allowed(url: str, allowed: List[str]) -> bool:
    """True when `url` has exactly the scheme and port of an allowed origin, and its host equals that
    origin's host or is a subdomain of an allowed '*.domain'."""
    target = _origin_parts(url)
    if target is None:
        return False
    for pattern in allowed:
        wanted = _origin_parts(pattern)
        if wanted is None or wanted[0] != target[0] or wanted[2] != target[2]:
            continue
        if wanted[1].startswith("*."):
            if target[1].endswith(wanted[1][1:]) and target[1] != wanted[1][2:]:
                return True
        elif target[1] == wanted[1]:
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
    # The environment variable `run` puts the secret in when the caller does not choose one. Empty: CC_SECRET.
    env_name: str = ""
    # secret: hidden from every output, never printed. setting: not secret (a host, an email address, an
    # identifier) - kept here so every credential has one home, readable with `get`, and NOT hidden from output,
    # because hiding a host name would blank it out of everything that prints it.
    kind: str = KIND_SECRET
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
            "envName": self.env_name,
            "kind": self.kind,
            "updatedUtc": self.updated_utc,
        }

    @property
    def is_setting(self) -> bool:
        return self.kind == KIND_SETTING

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
            env_name=str(record.get("envName", "")),
            kind=str(record.get("kind", KIND_SECRET)),
            created_utc=str(record.get("createdUtc", "")),
            updated_utc=str(record.get("updatedUtc", "")),
        )


ENV_NAME_PATTERN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]{0,127}$")


def validate_env_name(env_name: str) -> str:
    if env_name and not ENV_NAME_PATTERN.match(env_name):
        raise InputError(f"'{env_name}' is not a valid environment variable name: letters, digits and underscores, "
                         "not starting with a digit.")
    return env_name


def make_entry(name: str, username: str, secret: str, allowed_domains: List[str], notes: str,
               agents_may_use: bool, uses: List[str], env_name: str = "", kind: str = KIND_SECRET) -> Entry:
    """Validate the owner's input and build an entry."""
    validate_name(name)
    validate_env_name(env_name)
    if kind not in KINDS:
        raise InputError(f"The kind must be one of: {', '.join(KINDS)}.")
    if kind == KIND_SETTING:
        if not secret or "\n" in secret or "\r" in secret:
            raise InputError("A setting must be one non-empty line.")
    else:
        if len(secret) < MIN_SECRET_LENGTH:
            raise InputError(f"The secret must be at least {MIN_SECRET_LENGTH} characters.")
        conflict = redaction_conflict(secret, username)
        if conflict is not None:
            raise InputError(f"This secret cannot be stored: {conflict}.")
    bad = [u for u in uses if u not in USES]
    if bad or not uses:
        raise InputError(f"Uses must be one or more of: {', '.join(USES)}.")
    return Entry(
        name=name,
        username=username,
        secret=Secret(secret),
        allowed_domains=sorted({normalize_origin(d) for d in allowed_domains if d.strip()}),
        notes=notes,
        agents_may_use=agents_may_use,
        uses=[u for u in USES if u in uses],
        env_name=env_name,
        kind=kind,
    )


def _register_secrets(document: object) -> None:
    """Hand every secret in a parsed store to the scrubber, before the document is checked any further."""
    records = document.get("entries") if isinstance(document, dict) else None
    if not isinstance(records, list):
        return
    for record in records:
        if not isinstance(record, dict) or record.get("kind") == KIND_SETTING:
            continue  # a setting is not secret, and hiding it would blank it out of every output
        if isinstance(record.get("secret"), str) and record["secret"]:
            SCRUBBER.add(record["secret"], str(record.get("username", "")))


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
        _register_secrets(document)
        if not isinstance(document, dict) or document.get("version") != STORE_VERSION:
            version = document.get("version") if isinstance(document, dict) else None
            raise StoreFormatError(f"The store is in format version {version!r}, which this cc-secrets does not "
                                   f"read (it reads version {STORE_VERSION}).")
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

    def put_many(self, new_entries: List[Entry], replace: bool) -> Dict[str, str]:
        """Add many entries in ONE save. Returns {name: "added" | "replaced" | "exists"}; an existing entry is
        only replaced when `replace` is true. Reading and saving the store once keeps an import of dozens of
        entries from re-reading every secret for each one."""
        filelog.write(f"[SecretStore] put_many: count={len(new_entries)}, replace={replace}")
        entries = self.entries()
        by_name = {e.name: e for e in entries}
        outcomes: Dict[str, str] = {}
        for entry in new_entries:
            existing = by_name.get(entry.name)
            if existing is not None and not replace:
                outcomes[entry.name] = "exists"
                continue
            if existing is not None:
                entry.created_utc = existing.created_utc
            entry.updated_utc = _utc_now()
            by_name[entry.name] = entry
            outcomes[entry.name] = "replaced" if existing is not None else "added"
        if any(o != "exists" for o in outcomes.values()):
            self._save(list(by_name.values()))
        return outcomes

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
        if entry.is_setting and use == "login":
            raise EntryNotAvailableError(f"'{name}' is a setting, not a password, so it cannot be used to log in.")
        # Stored before cc-secrets refused such secrets. The reason is not given here: it would say what the
        # secret is part of.
        if not entry.is_setting and redaction_conflict(entry.secret.reveal(), entry.username) is not None:
            raise EntryNotAvailableError(
                f"Secret '{name}' cannot be used, because its secret could not be hidden in output. The owner "
                f"should replace it: cc-secrets add {name} --replace"
            )
        return entry

    def setting_for_agent(self, name: str) -> Entry:
        """A setting agents may read. A secret is refused - naming it a secret, which list already shows."""
        entry = self.get(name)
        if entry is None or not entry.agents_may_use:
            raise EntryNotAvailableError(f"No setting named '{name}' is available to agents on this machine.")
        if not entry.is_setting:
            raise EntryNotAvailableError(f"'{name}' is a secret, and a secret is never printed. Use it with "
                                         f"cc-secrets run {name} -- <command>.")
        return entry

    def _save(self, entries: List[Entry]) -> None:
        document = {"version": STORE_VERSION,
                    "entries": [e._to_record() for e in sorted(entries, key=lambda e: e.name)]}
        self._file.write((json.dumps(document, indent=2) + "\n").encode("utf-8"))
