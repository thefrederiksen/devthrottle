"""Tests for the G0 probe's locator, refresh, write-back and guard. Scratch stores and a fake transport; no network.

Run from the repository root:
    uv run --no-project --with pytest --with google-auth python -m pytest docs/qa/mail-desk/gmail/g0/tests -v
"""

from __future__ import annotations

import importlib.util
import json
import os
import stat
import sys
from datetime import datetime, timedelta
from pathlib import Path

import pytest

PROBE_PATH = Path(__file__).resolve().parent.parent / "gmail_probe.py"
spec = importlib.util.spec_from_file_location("gmail_probe", PROBE_PATH)
probe = importlib.util.module_from_spec(spec)
sys.modules["gmail_probe"] = probe
spec.loader.exec_module(probe)

ADDRESS = "someone@example.com"
SCOPES = [
    "https://www.googleapis.com/auth/gmail.readonly",
    "https://www.googleapis.com/auth/gmail.send",
    "https://www.googleapis.com/auth/gmail.compose",
    "https://www.googleapis.com/auth/gmail.modify",
]


def token_json(**overrides) -> dict:
    data = {
        "token": "old-access",
        "refresh_token": "refresh-1",
        "token_uri": "https://oauth2.googleapis.com/token",
        "client_id": "client-1.apps.example.com",
        "client_secret": "secret-1",
        "scopes": SCOPES,
        "universe_domain": "googleapis.com",
        "account": "",
        "expiry": "2026-10-21T10:00:00.123456Z",
    }
    data.update(overrides)
    return {k: v for k, v in data.items() if v is not None}


def make_account(store: Path, nickname: str, email: str, auth_method: str = "oauth", token: dict | None = None) -> Path:
    directory = store / "accounts" / nickname
    directory.mkdir(parents=True)
    (directory / "config.json").write_text(json.dumps({"email": email, "auth_method": auth_method}))
    if token is not None:
        (directory / "token.json").write_text(json.dumps(token))
        os.chmod(directory / "token.json", 0o644)
    return directory


class FakeTransport:
    """Records every request; answers the token address with a fresh token and Gmail with an empty list."""

    def __init__(self, token_status: int = 200, token_answer: dict | None = None):
        self.log: list[tuple[str, str, bytes | None]] = []
        self.token_status = token_status
        self.token_answer = token_answer if token_answer is not None else {"access_token": "new-access", "expires_in": 3599}

    def __call__(self, method, url, headers, body):
        self.log.append((method, url, body))
        if url == probe.TOKEN_URI:
            return self.token_status, json.dumps(self.token_answer).encode()
        return 200, json.dumps({"resultSizeEstimate": 0}).encode()


@pytest.fixture(autouse=True)
def no_cc_variables(monkeypatch):
    for name in list(os.environ):
        if name.startswith("CC_"):
            monkeypatch.delenv(name)


# --- R1: the store and the account -----------------------------------------------------------------------------

def test_gmail_store_linux_uses_account_record_home_not_home_or_localappdata(tmp_path, monkeypatch):
    monkeypatch.setenv("HOME", str(tmp_path / "bogus-home"))
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "bogus-localappdata"))
    record_home = tmp_path / "record-home"
    monkeypatch.setattr(probe, "account_record_home", lambda: record_home)

    store = probe.gmail_store(system="Linux")

    assert store == record_home / ".cc-director" / "config" / "gmail"


def test_gmail_store_macos_ignores_localappdata(tmp_path, monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "bogus-localappdata"))
    store = probe.gmail_store(system="Darwin", home=tmp_path)
    assert store == tmp_path / "Library" / "Application Support" / "cc-director" / "config" / "gmail"


def test_gmail_store_windows_uses_localappdata():
    store = probe.gmail_store(system="Windows", environ={"LOCALAPPDATA": "C:/Users/x/AppData/Local"})
    assert store == Path("C:/Users/x/AppData/Local") / "cc-director" / "config" / "gmail"


def test_gmail_store_windows_without_localappdata_fails():
    with pytest.raises(probe.ChannelFailed, match="LOCALAPPDATA"):
        probe.gmail_store(system="Windows", environ={})


def test_account_record_home_is_pwd_entry(monkeypatch):
    import pwd
    monkeypatch.setenv("HOME", "/nowhere")
    assert probe.account_record_home() == Path(pwd.getpwuid(os.getuid()).pw_dir)


def test_find_account_none_names_the_fix(tmp_path):
    make_account(tmp_path, "other", "other@example.org", token=token_json())
    with pytest.raises(probe.ChannelFailed, match=r"not signed in.*cc-gmail -a <nickname> auth"):
        probe.find_account(tmp_path, ADDRESS)


def test_find_account_no_store_at_all_fails(tmp_path):
    with pytest.raises(probe.ChannelFailed, match="not signed in"):
        probe.find_account(tmp_path / "missing", ADDRESS)


def test_find_account_two_found_names_both_and_picks_none(tmp_path):
    first = make_account(tmp_path, "one", ADDRESS, token=token_json())
    second = make_account(tmp_path, "two", ADDRESS.upper(), token=token_json())
    with pytest.raises(probe.ChannelFailed) as caught:
        probe.find_account(tmp_path, ADDRESS)
    assert "more than one" in str(caught.value)
    assert str(first) in str(caught.value) and str(second) in str(caught.value)


def test_find_account_matches_case_blind_and_returns_directory_name(tmp_path):
    make_account(tmp_path, "other", "other@example.org")
    make_account(tmp_path, "personal", "Someone@Example.COM", token=token_json())
    account = probe.find_account(tmp_path, ADDRESS)
    assert account.nickname == "personal"


def test_find_account_unreadable_config_fails_loud(tmp_path):
    directory = tmp_path / "accounts" / "broken"
    directory.mkdir(parents=True)
    (directory / "config.json").write_text("{not json")
    with pytest.raises(probe.ChannelFailed, match="cannot be read"):
        probe.find_account(tmp_path, ADDRESS)


# --- R1.3 to R1.5 and R2: the token -----------------------------------------------------------------------------

def test_load_token_app_password_account_fails_naming_oauth(tmp_path):
    make_account(tmp_path, "personal", ADDRESS, auth_method="app_password")
    account = probe.find_account(tmp_path, ADDRESS)
    with pytest.raises(probe.ChannelFailed, match="cc-gmail -a personal auth --method oauth"):
        probe.load_token(account)


def test_load_token_missing_token_file_fails_naming_auth(tmp_path):
    make_account(tmp_path, "personal", ADDRESS)
    with pytest.raises(probe.ChannelFailed, match="no token.json: run cc-gmail -a personal auth$"):
        probe.load_token(probe.find_account(tmp_path, ADDRESS))


def test_load_token_without_refresh_token_fails(tmp_path):
    make_account(tmp_path, "personal", ADDRESS, token=token_json(refresh_token=None))
    with pytest.raises(probe.ChannelFailed, match="no refresh token"):
        probe.load_token(probe.find_account(tmp_path, ADDRESS))


@pytest.mark.parametrize("key", ["client_id", "client_secret", "token_uri"])
def test_load_token_missing_client_fields_fails_and_never_reads_credentials_json(tmp_path, key):
    directory = make_account(tmp_path, "personal", ADDRESS, token=token_json(**{key: None}))
    (directory / "credentials.json").write_text(json.dumps({"installed": {"client_id": "x", "client_secret": "y"}}))
    with pytest.raises(probe.ChannelFailed, match=f"lacks {key}.*auth --force"):
        probe.load_token(probe.find_account(tmp_path, ADDRESS))


def test_load_token_other_token_uri_fails(tmp_path):
    make_account(tmp_path, "personal", ADDRESS, token=token_json(token_uri="https://evil.example.net/token"))
    with pytest.raises(probe.ChannelFailed, match="token address other than Google's"):
        probe.load_token(probe.find_account(tmp_path, ADDRESS))


def test_load_token_scope_rules(tmp_path):
    make_account(tmp_path, "personal", ADDRESS, token=token_json(scopes=["https://www.googleapis.com/auth/gmail.readonly"]))
    account = probe.find_account(tmp_path, ADDRESS)
    probe.load_token(account, mode="observe")
    with pytest.raises(probe.ChannelFailed, match="scope live mode needs.*auth --force"):
        probe.load_token(account, mode="live")


def test_needs_refresh_window():
    now = datetime(2026, 10, 21, 10, 0, 0)
    assert probe.needs_refresh(token_json(expiry="2026-10-21T10:04:59Z"), now)
    assert not probe.needs_refresh(token_json(expiry="2026-10-21T10:05:01.5Z"), now)
    assert probe.needs_refresh(token_json(expiry=None), now)
    assert probe.needs_refresh(token_json(token=None, expiry="2026-10-21T11:00:00Z"), now)


def test_refresh_posts_the_standard_form_to_the_one_address(tmp_path):
    make_account(tmp_path, "personal", ADDRESS, token=token_json())
    token_path, data = probe.load_token(probe.find_account(tmp_path, ADDRESS))
    transport = FakeTransport()

    probe.ensure_fresh_token(token_path, data, transport, now=datetime(2026, 10, 21, 10, 0, 0))

    assert len(transport.log) == 1
    method, url, body = transport.log[0]
    assert (method, url) == ("POST", "https://oauth2.googleapis.com/token")
    from urllib.parse import parse_qs
    assert parse_qs(body.decode()) == {
        "grant_type": ["refresh_token"], "client_id": ["client-1.apps.example.com"],
        "client_secret": ["secret-1"], "refresh_token": ["refresh-1"]}


def test_write_back_mode_0600_and_every_other_key_unchanged(tmp_path):
    make_account(tmp_path, "personal", ADDRESS, token=token_json())
    token_path, data = probe.load_token(probe.find_account(tmp_path, ADDRESS))
    now = datetime(2026, 10, 21, 10, 0, 0, 250000)

    used, renewed = probe.ensure_fresh_token(token_path, data, FakeTransport(), now=now)

    written = json.loads(token_path.read_text())
    assert renewed
    assert stat.S_IMODE(token_path.stat().st_mode) == 0o600
    assert list(written) == list(data)
    assert written["token"] == "new-access" == used["token"]
    assert written["expiry"] == "2026-10-21T10:59:59.250000Z"
    for key in data:
        if key not in ("token", "expiry"):
            assert written[key] == data[key], key
    assert [p.name for p in token_path.parent.iterdir() if p.name.startswith(".token-")] == []


def test_write_back_takes_a_new_refresh_token_when_google_sends_one(tmp_path):
    make_account(tmp_path, "personal", ADDRESS, token=token_json())
    token_path, data = probe.load_token(probe.find_account(tmp_path, ADDRESS))
    answer = {"access_token": "new-access", "expires_in": 3599, "refresh_token": "refresh-2"}
    probe.ensure_fresh_token(token_path, data, FakeTransport(token_answer=answer), force=True)
    assert json.loads(token_path.read_text())["refresh_token"] == "refresh-2"


def test_written_file_is_read_by_google_auth_as_valid(tmp_path):
    from google.oauth2.credentials import Credentials

    make_account(tmp_path, "personal", ADDRESS, token=token_json())
    token_path, data = probe.load_token(probe.find_account(tmp_path, ADDRESS))
    probe.ensure_fresh_token(token_path, data, FakeTransport(), now=probe.utcnow())

    creds = Credentials.from_authorized_user_file(str(token_path), SCOPES)

    assert creds.token == "new-access"
    assert creds.valid and not creds.expired
    assert creds.client_id == data["client_id"] and creds.refresh_token == data["refresh_token"]


def test_no_refresh_when_token_is_fresh_and_file_untouched(tmp_path):
    expiry = probe.format_expiry(probe.utcnow() + timedelta(minutes=30))
    make_account(tmp_path, "personal", ADDRESS, token=token_json(expiry=expiry))
    token_path, data = probe.load_token(probe.find_account(tmp_path, ADDRESS))
    before = token_path.read_bytes()
    transport = FakeTransport()

    _, renewed = probe.ensure_fresh_token(token_path, data, transport)

    assert not renewed and transport.log == [] and token_path.read_bytes() == before


def test_forced_refresh_renews_a_fresh_token(tmp_path):
    expiry = probe.format_expiry(probe.utcnow() + timedelta(minutes=30))
    make_account(tmp_path, "personal", ADDRESS, token=token_json(expiry=expiry))
    token_path, data = probe.load_token(probe.find_account(tmp_path, ADDRESS))
    _, renewed = probe.ensure_fresh_token(token_path, data, FakeTransport(), force=True)
    assert renewed


def test_refused_refresh_fails_naming_auth_force_and_leaves_file_unchanged(tmp_path):
    make_account(tmp_path, "personal", ADDRESS, token=token_json())
    token_path, data = probe.load_token(probe.find_account(tmp_path, ADDRESS))
    before = token_path.read_bytes()
    transport = FakeTransport(token_status=400, token_answer={"error": "invalid_grant"})
    with pytest.raises(probe.ChannelFailed, match=r"invalid_grant.*auth --force"):
        probe.ensure_fresh_token(token_path, data, transport, force=True)
    assert token_path.read_bytes() == before
    assert not (token_path.parent / "token-refresh-error.json").exists()


# --- The guard -------------------------------------------------------------------------------------------------

@pytest.mark.parametrize("path, query", [
    ("/gmail/v1/users/me/messages/abc/modify", []),
    ("/gmail/v1/users/me/messages/send", [("x", "1")]),
    ("/gmail/v1/users/me/drafts", []),
    ("/gmail/v1/users/me/messages/abc/trash", []),
    ("/gmail/v1/users/me/messages/batchModify", [("q", "x")]),
    ("/gmail/v1/users/other/messages", []),
    ("/gmail/v1/users/me/threads", []),
    ("/gmail/v1/users/me/labels", []),
    ("/gmail/v2/users/me/messages", []),
    ("/gmail/v1/users/me/messages/ab-c", []),
    ("/gmail/v1/users/me/messages/abc", [("format", "full")]),
    ("/gmail/v1/users/me/messages/abc", [("format", "raw")]),
    ("/gmail/v1/users/me/messages", [("fields", "x")]),
    ("/gmail/v1/users/me/messages", [("format", "metadata")]),
    ("/gmail/v1/users/me/messages/abc", [("labelIds", "INBOX")]),
    ("/gmail/v1/users/me/messages", [("q", "a\nb")]),
    ("/gmail/v1/users/me/messages ", []),
    ("/gmail/v1/users/me/messages", [("q", "a\tb")]),
    ("/gmail/v1/users/me/messages", [("label Ids", "INBOX")]),
])
def test_guard_refuses_before_the_network(path, query):
    transport = FakeTransport()
    gmail = probe.Gmail("access", transport)
    with pytest.raises(probe.GuardRefused):
        gmail.gmail_get("test", path, query)
    assert transport.log == []


def test_guard_allows_the_two_gets():
    transport = FakeTransport()
    gmail = probe.Gmail("access", transport)
    gmail.gmail_get("list", probe.MESSAGES_PATH, [("labelIds", "INBOX"), ("q", "after:1 has:attachment"), ("maxResults", "10")])
    gmail.gmail_get("get", probe.MESSAGES_PATH + "/19a2b3c4d5e6f708", [("format", "metadata"), ("metadataHeaders", "From")])
    assert [m for m, _, _ in transport.log] == ["GET", "GET"]
    assert all(u.startswith("https://gmail.googleapis.com/gmail/v1/users/me/messages") for _, u, _ in transport.log)


def test_refresh_refuses_another_token_address():
    transport = FakeTransport()
    with pytest.raises(probe.GuardRefused):
        probe.post_token_refresh(token_json(token_uri="https://evil.example.net/token"), transport)
    assert transport.log == []


# --- The whole probe, with HOME and LOCALAPPDATA pointing at a decoy ---------------------------------------------

def test_main_ignores_home_and_localappdata_on_linux(tmp_path, monkeypatch, capsys):
    if sys.platform != "linux":
        pytest.skip("Linux branch")
    record_home = tmp_path / "record-home"
    real_store = record_home / ".cc-director" / "config" / "gmail"
    make_account(real_store, "personal", ADDRESS, token=token_json())
    decoy_home = tmp_path / "decoy-home"
    make_account(decoy_home / ".cc-director" / "config" / "gmail", "decoy", ADDRESS, token=token_json())
    make_account(tmp_path / "decoy-local" / "cc-director" / "config" / "gmail", "decoy2", ADDRESS, token=token_json())
    monkeypatch.setenv("HOME", str(decoy_home))
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "decoy-local"))
    monkeypatch.setattr(probe, "account_record_home", lambda: record_home)

    code = probe.main(["--address", ADDRESS], transport=FakeTransport())

    out = json.loads(capsys.readouterr().out)
    assert code == 0, out
    assert out["a_lookup"]["accountNickname"] == "personal"
    assert out["a_lookup"]["store"] == "~/.cc-director/config/gmail"
    assert ADDRESS not in json.dumps(out)
    assert "new-access" not in json.dumps(out) and "refresh-1" not in json.dumps(out)


def test_main_two_accounts_exits_1_without_network(tmp_path, monkeypatch, capsys):
    store = tmp_path / ".cc-director" / "config" / "gmail"
    make_account(store, "one", ADDRESS, token=token_json())
    make_account(store, "two", ADDRESS, token=token_json())
    monkeypatch.setattr(probe, "account_record_home", lambda: tmp_path)
    transport = FakeTransport()
    if sys.platform != "linux":
        pytest.skip("Linux branch")

    code = probe.main(["--address", ADDRESS], transport=transport)

    assert code == 1
    assert "more than one" in json.loads(capsys.readouterr().out)["failed"]
    assert transport.log == []
