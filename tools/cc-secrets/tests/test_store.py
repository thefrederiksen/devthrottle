import json

import pytest

from conftest import add_entry, new_secret
from src import paths
from src.errors import InputError, StoreFormatError
from src.redact import SCRUBBER
from src.store import (EntryNotAvailableError, make_entry, normalize_origin, origin_allowed)


def test_Put_ThenGet_RoundTripsEveryField(store):
    secret = add_entry(store, name="github-work", domains=("https://GitHub.com:443/login",), notes="n")

    entry = store.get("github-work")

    assert entry.secret.reveal() == secret
    assert entry.username == "leak-user"
    assert entry.allowed_domains == ["https://github.com"]
    assert entry.notes == "n"


def test_AnOldRecordWithTheAgentsField_StillLoads_AndTheFieldIsDroppedOnTheNextSave(store):
    # Records written before 2026-10-10 carry "agentsMayUse" - true or false. Both load, both are available to
    # agents, and the field is gone from the file after the next save of any entry.
    add_entry(store, name="old-on")
    add_entry(store, name="old-off")
    path = paths.store_path()
    document = json.loads(path.read_text(encoding="utf-8"))
    for record in document["entries"]:
        record["agentsMayUse"] = record["name"] == "old-on"
    path.write_text(json.dumps(document), encoding="utf-8")

    assert [e.name for e in store.entries()] == ["old-off", "old-on"]
    assert store.entry_for_agent("old-off", "run").name == "old-off"
    assert "agentsMayUse" not in store.get("old-off").public_view()

    add_entry(store, name="new")
    assert "agentsMayUse" not in path.read_text(encoding="utf-8")


def test_StoreFile_IsPlainJsonNamedSecretsJson(store):
    secret = add_entry(store)

    document = json.loads(paths.store_path().read_text(encoding="utf-8"))

    assert paths.store_path().name == "secrets.json"
    assert document["version"] == 1
    assert document["entries"][0]["secret"] == secret


def test_Secret_NeverRendersAsText(store):
    secret = add_entry(store)
    entry = store.get("devlinux")

    rendered = [repr(entry), str(entry), f"{entry.secret}", str(entry.secret), repr(entry.secret),
                json.dumps(entry.public_view())]

    assert all(secret not in text for text in rendered)
    assert "<hidden>" in repr(entry)


def test_Entries_HandsEverySecretToTheScrubber(store):
    kept = add_entry(store, name="kept-back")
    shared = add_entry(store, name="shared")
    SCRUBBER.clear()

    store.entries()

    assert SCRUBBER.scrub(f"{kept} {shared}") == "[REDACTED] [REDACTED]"


def test_Entries_NewerStoreVersion_RegistersSecretsBeforeRefusing(store):
    kept = add_entry(store, name="kept-back")
    path = paths.store_path()
    path.write_text(path.read_text(encoding="utf-8").replace('"version": 1', '"version": 2'), encoding="utf-8")
    SCRUBBER.clear()

    with pytest.raises(StoreFormatError, match="version 2"):
        store.entries()
    assert SCRUBBER.scrub(kept) == "[REDACTED]"


def test_EntryForAgent_Missing_SaysItIsNotOnThisMachine(store):
    with pytest.raises(EntryNotAvailableError, match="No secret named 'nothing-here' is on this machine"):
        store.entry_for_agent("nothing-here", "run")


def test_EntryForAgent_UseNotAllowed_Refused(store):
    add_entry(store, name="website", uses=("login",))

    with pytest.raises(EntryNotAvailableError, match="not allowed for 'run'"):
        store.entry_for_agent("website", "run")
    assert store.entry_for_agent("website", "login").name == "website"


def test_Put_ExistingName_ReplacesAndKeepsCreatedTime(store):
    add_entry(store)
    created = store.get("devlinux").created_utc
    second = add_entry(store)

    assert len(store.entries()) == 1
    assert store.get("devlinux").secret.reveal() == second
    assert store.get("devlinux").created_utc == created


def test_Remove_DeletesOnlyThatEntry(store):
    add_entry(store, name="a")
    add_entry(store, name="b")

    assert store.remove("a") is True
    assert store.remove("a") is False
    assert [e.name for e in store.entries()] == ["b"]


@pytest.mark.parametrize("url,allowed,expected", [
    ("https://example.com/login", ["https://example.com"], True),
    ("https://EXAMPLE.com:443/x", ["https://example.com"], True),
    ("http://example.com/login", ["https://example.com"], False),
    ("http://example.com:443/login", ["https://example.com"], False),   # same port, other scheme
    ("https://127.0.0.1:5000/login", ["http://127.0.0.1:5000"], False),  # same port, other scheme
    ("https://example.com:8443/login", ["https://example.com"], False),
    ("http://127.0.0.1:5000/login", ["http://127.0.0.1:5000"], True),
    ("http://127.0.0.1:5001/login", ["http://127.0.0.1:5000"], False),
    ("http://127.0.0.1/login", ["http://127.0.0.1:5000"], False),
    ("https://login.example.com/", ["https://*.example.com"], True),
    ("http://login.example.com/", ["https://*.example.com"], False),
    ("https://example.com/", ["https://*.example.com"], False),
    ("https://evil-example.com/", ["https://*.example.com"], False),
    ("https://example.com.evil.net/", ["https://example.com"], False),
    ("https://example.com@evil.net/", ["https://example.com"], False),
    ("http://localhost:5000/", ["http://127.0.0.1:5000"], False),
    ("chrome-error://chromewebdata/", ["https://example.com"], False),
    ("javascript:alert(1)", ["https://example.com"], False),
    ("", ["https://example.com"], False),
])
def test_OriginAllowed_ComparesSchemeHostAndPortExactly(url, allowed, expected):
    assert origin_allowed(url, allowed) is expected


@pytest.mark.parametrize("given,canonical", [
    ("example.com", "https://example.com"),
    (" HTTPS://Login.Example.com:443/path?q=1 ", "https://login.example.com"),
    ("http://example.com:80", "http://example.com"),
    ("http://127.0.0.1:5000/login", "http://127.0.0.1:5000"),
    ("https://example.com:8443", "https://example.com:8443"),
    ("https://*.Example.com", "https://*.example.com"),
])
def test_NormalizeOrigin_KeepsSchemeAndPort(given, canonical):
    assert normalize_origin(given) == canonical


@pytest.mark.parametrize("given", ["*.com", "https://*.com", "ftp://example.com", "https://user@example.com",
                                   "not a host", "https://example.com:99999", ""])
def test_NormalizeOrigin_RefusesWhatCannotBeCheckedExactly(given):
    with pytest.raises(InputError):
        normalize_origin(given)


def test_MakeEntry_RejectsBadInput():
    with pytest.raises(InputError, match="valid entry name"):
        make_entry("Bad Name", "u", new_secret(), [], "", ["run"])
    with pytest.raises(InputError, match="at least"):
        make_entry("ok", "u", "abc", [], "", ["run"])
    with pytest.raises(InputError, match="Uses"):
        make_entry("ok", "u", new_secret(), [], "", ["reveal"])
