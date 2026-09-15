import json

import pytest

from conftest import add_entry, new_secret
from src import paths
from src.store import (EntryNotAvailableError, Secret, host_allowed, make_entry, normalize_domain)


def test_Put_ThenGet_RoundTripsEveryField(store):
    secret = add_entry(store, name="github-work", domains=("https://GitHub.com:443/login",), notes="n")

    entry = store.get("github-work")

    assert entry.secret.reveal() == secret
    assert entry.username == "leak-user"
    assert entry.allowed_domains == ["github.com"]
    assert entry.notes == "n"
    assert entry.agents_may_use is True


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


def test_AgentEntries_LeavesOutEntriesNotMarkedForAgents(store):
    add_entry(store, name="shared", agents=True)
    add_entry(store, name="kept-back", agents=False)

    assert [e.name for e in store.agent_entries()] == ["shared"]


def test_EntryForAgent_KeptBackAndMissing_GiveTheSameMessage(store):
    add_entry(store, name="kept-back", agents=False)

    with pytest.raises(EntryNotAvailableError) as kept:
        store.entry_for_agent("kept-back", "run")
    with pytest.raises(EntryNotAvailableError) as missing:
        store.entry_for_agent("nothing-here", "run")

    assert str(kept.value).replace("kept-back", "X") == str(missing.value).replace("nothing-here", "X")


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


@pytest.mark.parametrize("host,allowed,expected", [
    ("example.com", ["example.com"], True),
    ("EXAMPLE.com.", ["example.com"], True),
    ("login.example.com", ["example.com"], False),
    ("login.example.com", ["*.example.com"], True),
    ("example.com", ["*.example.com"], False),
    ("evil-example.com", ["*.example.com"], False),
    ("example.com.evil.net", ["example.com"], False),
    ("", ["example.com"], False),
    ("127.0.0.1", ["127.0.0.1"], True),
    ("localhost", ["127.0.0.1"], False),
])
def test_HostAllowed_MatchesOnlyWhatIsListed(host, allowed, expected):
    assert host_allowed(host, allowed) is expected


def test_NormalizeDomain_StripsSchemePortAndPath():
    assert normalize_domain(" HTTPS://Login.Example.com:8443/path?q=1 ") == "login.example.com"
    assert normalize_domain("*.example.com") == "*.example.com"
    with pytest.raises(ValueError):
        normalize_domain("not a host")


def test_MakeEntry_RejectsBadInput():
    with pytest.raises(ValueError, match="valid entry name"):
        make_entry("Bad Name", "u", new_secret(), [], "", True, ["run"])
    with pytest.raises(ValueError, match="at least"):
        make_entry("ok", "u", "abc", [], "", True, ["run"])
    with pytest.raises(ValueError, match="Uses"):
        make_entry("ok", "u", new_secret(), [], "", True, ["reveal"])
