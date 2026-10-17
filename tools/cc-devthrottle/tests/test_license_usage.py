"""Usage reading and spare refresh for the License Switching mission (license_usage, license_usage_ops).

The HTTP layer is faked everywhere: no test reaches api.anthropic.com or the token endpoint, and every
config folder is a temporary one, never the machine's real Claude login. The JSON rows are checked
against the contract's LicenseHolding (missions/license-switching/contract.md section 2.2).
"""

import contextlib
import json
import logging
import os
import sys
import time
from pathlib import Path

import pytest
import typer
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import license_usage, license_usage_ops  # noqa: E402
from src.cli import app  # noqa: E402
from src.license_store import FileKeyBackend, LicenseStore, LicenseStoreError  # noqa: E402
from src.license_usage import HttpAnswer, NetworkFailure, RefreshError  # noqa: E402

ACCESS_A = "sk-ant-oat01-ACCESS-AAAA-never-shown"
REFRESH_A = "sk-ant-ort01-REFRESH-AAAA-never-shown"
ACCESS_B = "sk-ant-oat01-ACCESS-BBBB-never-shown"
REFRESH_B = "sk-ant-ort01-REFRESH-BBBB-never-shown"
NEW_ACCESS = "sk-ant-oat01-ACCESS-NEW-never-shown"
NEW_REFRESH = "sk-ant-ort01-REFRESH-NEW-never-shown"
ALL_SECRETS = [ACCESS_A, REFRESH_A, ACCESS_B, REFRESH_B, NEW_ACCESS, NEW_REFRESH]

NOW = 1_800_000_000.0
SIGN_IN_ENDS = NOW + 25 * 24 * 3600
runner = CliRunner()


def _sign_in(folder: Path, email: str, access: str, refresh: str, expires_in_seconds: float) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    (folder / ".claude.json").write_text(json.dumps({"oauthAccount": {"emailAddress": email}}), encoding="utf-8")
    (folder / ".credentials.json").write_text(json.dumps({"claudeAiOauth": {
        "accessToken": access, "refreshToken": refresh, "expiresAt": int((NOW + expires_in_seconds) * 1000),
        "refreshTokenExpiresAt": int(SIGN_IN_ENDS * 1000),
        "scopes": ["user:inference", "user:profile"], "subscriptionType": "max",
        "rateLimitTier": "default_claude_max_20x",
    }}), encoding="utf-8")


@pytest.fixture(autouse=True)
def file_backend(monkeypatch):
    """The key store's file backend on every platform: the Keychain backend is proven in test_license_store.py."""
    monkeypatch.setattr("src.license_store.sys.platform", "win32")


@pytest.fixture
def machine(tmp_path):
    """An isolated machine: account A active in the default folder, account B added to the store as a spare."""
    default = tmp_path / "default"
    _sign_in(default, "Account-A@example.com", ACCESS_A, REFRESH_A, 3600 * 5)
    _sign_in(tmp_path / "login-b", "account-b@example.com", ACCESS_B, REFRESH_B, 3600 * 5)
    store = LicenseStore(str(default), str(tmp_path / "store"), backend=FileKeyBackend())
    store.add(str(tmp_path / "login-b"))
    return store


def _key(store, account):
    for key in license_usage.held_keys(store):
        if key.account.lower() == account:
            return key
    raise AssertionError(account)


GOOD_BODY = json.dumps({
    "five_hour": {"utilization": 42.0, "resets_at": "2027-01-15T10:00:00.024253+00:00"},
    "seven_day": {"utilization": 97.5, "resets_at": "2027-01-20T09:00:00+01:00"},
    "extra_usage": {"is_enabled": False, "monthly_limit": None, "utilization": None, "spend_limit_reached": False},
}).encode()


class FakeGet:
    def __init__(self, answer=None, error=None):
        self.answer, self.error, self.calls = answer, error, []

    def __call__(self, url, headers, timeout):
        self.calls.append((url, dict(headers)))
        if self.error:
            raise self.error
        return self.answer


class FakePost:
    def __init__(self, answer=None, error=None):
        self.answer, self.error, self.calls = answer, error, []

    def __call__(self, url, payload, headers, timeout):
        self.calls.append((url, dict(payload)))
        if self.error:
            raise self.error
        return self.answer


# --- usage reading -----------------------------------------------------------------------------------


def test_read_usage_GoodAnswer_IsAContractHolding(machine):
    get = FakeGet(HttpAnswer(200, GOOD_BODY))
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"), get, now=lambda: NOW)
    assert reading.read_error is None
    assert reading.to_json() == {
        "accountEmail": "account-a@example.com",
        "isActive": True,
        "plan": "max",
        "rateLimitTier": "default_claude_max_20x",
        "fiveHour": {"utilization": 42.0, "resetsAtUtc": "2027-01-15T10:00:00Z"},
        "sevenDay": {"utilization": 97.5, "resetsAtUtc": "2027-01-20T08:00:00Z"},
        "extraUsage": {"isEnabled": False, "utilization": None},
        "readAtUtc": license_usage.iso_utc(NOW),
        "readError": None,
        "readErrorDetail": None,
        "signedInAtUtc": None,
        "signInExpiresAtUtc": license_usage.iso_utc(SIGN_IN_ENDS),
        "folder": machine.default_config_dir,
    }
    url, headers = get.calls[0]
    assert url == "https://api.anthropic.com/api/oauth/usage"
    assert headers["Authorization"] == f"Bearer {ACCESS_A}"
    assert headers["anthropic-beta"] == "oauth-2025-04-20"


def test_read_usage_401WithLiveKey_IsUnauthorizedNeedingANewSignIn(machine):
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"), FakeGet(HttpAnswer(401, b"{}")),
                                       now=lambda: NOW)
    assert reading.read_error == "unauthorized"
    assert "new sign-in" in reading.read_error_detail
    assert reading.five_hour is None and reading.read_at_utc is None


def test_read_usage_401WithExpiredKey_IsUnauthorizedAndSaysItExpired(machine):
    reading = license_usage.read_usage(_key(machine, "account-b@example.com"), FakeGet(HttpAnswer(401, b"{}")),
                                       now=lambda: NOW + 3600 * 6)
    assert reading.read_error == "unauthorized"
    assert "expired at" in reading.read_error_detail


def test_read_usage_403_IsUnauthorized(machine):
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"), FakeGet(HttpAnswer(403, b"")),
                                       now=lambda: NOW)
    assert reading.read_error == "unauthorized"


def test_read_usage_429_IsRateLimited(machine):
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"), FakeGet(HttpAnswer(429, b"")),
                                       now=lambda: NOW)
    assert reading.read_error == "rate-limited"


def test_read_usage_NetworkFailure_IsNetwork(machine):
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"),
                                       FakeGet(error=NetworkFailure("ConnectTimeout")), now=lambda: NOW)
    assert reading.read_error == "network"
    assert "ConnectTimeout" in reading.read_error_detail


def test_read_usage_ServerError_IsUnexpectedAnswer(machine):
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"), FakeGet(HttpAnswer(500, b"oops")),
                                       now=lambda: NOW)
    assert reading.read_error == "unexpected-answer"


@pytest.mark.parametrize("body", [
    b"not json", b"[]", b"{}", b'{"five_hour": {"utilization": "lots"}}',
    b'{"five_hour": {"utilization": 3, "resets_at": "tomorrow"}}',
    b'{"five_hour": {"utilization": 3, "resets_at": "2027-01-15T10:00:00"}}',
])
def test_read_usage_UnreadableBody_IsUnexpectedAnswerWithNoNumbers(machine, body):
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"), FakeGet(HttpAnswer(200, body)),
                                       now=lambda: NOW)
    assert reading.read_error == "unexpected-answer"
    assert reading.five_hour is None and reading.seven_day is None


def test_read_usage_NoCredentialsFile_IsStoreUnreadableAndMakesNoCall(machine):
    key = _key(machine, "account-b@example.com")
    (key.folder / ".credentials.json").unlink()
    get = FakeGet(HttpAnswer(200, GOOD_BODY))
    reading = license_usage.read_usage(key, get, now=lambda: NOW)
    assert reading.read_error == "store-unreadable"
    assert get.calls == []


def test_held_keys_AreTheKeyStoresAccountsActiveFirst(machine):
    keys = license_usage.held_keys(machine)
    assert [(k.account, k.role) for k in keys] == [
        ("Account-A@example.com", "active"), ("account-b@example.com", "spare")]
    assert keys[1].folder == Path(machine.spare_folder("account-b@example.com"))


def test_read_usage_AccountFileWithoutEmail_IsStoreUnreadable(machine):
    (Path(machine.default_config_dir) / ".claude.json").write_text("{}", encoding="utf-8")
    get = FakeGet(HttpAnswer(200, GOOD_BODY))
    reading = license_usage.read_usage(license_usage.held_keys(machine)[0], get, now=lambda: NOW)
    assert reading.read_error == "store-unreadable"
    assert get.calls == []


# --- spare refresh -----------------------------------------------------------------------------------


def _token_answer(access=NEW_ACCESS, refresh=NEW_REFRESH, expires_in=28800, status=200, refresh_lives=2_000_000):
    body = {"access_token": access, "expires_in": expires_in, "token_type": "Bearer",
            "scope": "user:inference user:profile", "refresh_token_expires_in": refresh_lives}
    if refresh is not None:
        body["refresh_token"] = refresh
    return HttpAnswer(status, json.dumps(body).encode())


def _stored(key):
    return json.loads((key.folder / ".credentials.json").read_text(encoding="utf-8"))["claudeAiOauth"]


def test_refresh_spare_DueKey_PostsLikeClaudeCodeAndWritesBackInPlace(machine):
    key = _key(machine, "account-b@example.com")
    post = FakePost(_token_answer())
    at = NOW + 3600 * 4.5
    result = license_usage.refresh_spare(key, http_post_json=post, now=lambda: at)
    assert result.refreshed and result.refresh_token_rotated is True
    url, payload = post.calls[0]
    assert url == "https://platform.claude.com/v1/oauth/token"
    assert payload == {"grant_type": "refresh_token", "refresh_token": REFRESH_B,
                       "client_id": "9d1c250a-e61b-44d9-88ed-5944d1962f5e", "scope": "user:inference user:profile"}
    stored = _stored(key)
    assert stored["accessToken"] == NEW_ACCESS and stored["refreshToken"] == NEW_REFRESH
    assert stored["expiresAt"] == int((at + 28800) * 1000)
    assert stored["refreshTokenExpiresAt"] == int((at + 2_000_000) * 1000)
    assert result.sign_in_expires_at_utc == license_usage.iso_utc(at + 2_000_000)
    assert stored["subscriptionType"] == "max", "the rest of the stored sign-in is kept"
    assert not list(key.folder.glob("*.tmp"))
    _assert_store_lock_released(machine)


def test_refresh_spare_NoRefreshTokenInAnswer_KeepsTheOldOne(machine):
    key = _key(machine, "account-b@example.com")
    result = license_usage.refresh_spare(key, force=True, http_post_json=FakePost(_token_answer(refresh=None)),
                                         now=lambda: NOW)
    assert result.refresh_token_rotated is False
    assert _stored(key)["refreshToken"] == REFRESH_B


def test_refresh_spare_PlentyOfTimeLeft_DoesNothing(machine):
    key = _key(machine, "account-b@example.com")
    post = FakePost(_token_answer())
    result = license_usage.refresh_spare(key, http_post_json=post, now=lambda: NOW)
    assert not result.refreshed and result.reason == "not-needed"
    assert post.calls == []
    assert _stored(key)["accessToken"] == ACCESS_B


def test_refresh_spare_ActiveKey_IsRefusedWithoutAnyCall(machine):
    post = FakePost(_token_answer())
    with pytest.raises(RefreshError) as err:
        license_usage.refresh_spare(_key(machine, "account-a@example.com"), force=True, http_post_json=post,
                                    now=lambda: NOW)
    assert err.value.kind == "active"
    assert post.calls == []


@pytest.mark.parametrize("status", [400, 401])
def test_refresh_spare_SpentRefreshToken_IsUnauthorizedAndKeyUnchanged(machine, status):
    key = _key(machine, "account-b@example.com")
    answer = HttpAnswer(status, json.dumps({"error": "invalid_grant", "error_description": REFRESH_B}).encode())
    with pytest.raises(RefreshError) as err:
        license_usage.refresh_spare(key, force=True, http_post_json=FakePost(answer), now=lambda: NOW)
    assert err.value.kind == "unauthorized"
    assert "invalid_grant" in str(err.value)
    assert REFRESH_B not in str(err.value), "a server body echoing the token must not reach the message"
    assert _stored(key)["accessToken"] == ACCESS_B


def test_refresh_spare_NetworkFailure_IsNetworkAndKeyUnchanged(machine):
    key = _key(machine, "account-b@example.com")
    with pytest.raises(RefreshError) as err:
        license_usage.refresh_spare(key, force=True, http_post_json=FakePost(error=NetworkFailure("ConnectionError")),
                                    now=lambda: NOW)
    assert err.value.kind == "network"
    assert _stored(key)["refreshToken"] == REFRESH_B
    _assert_store_lock_released(machine)


def test_refresh_spare_AnswerWithoutAccessToken_IsUnexpected(machine):
    key = _key(machine, "account-b@example.com")
    with pytest.raises(RefreshError) as err:
        license_usage.refresh_spare(key, force=True, http_post_json=FakePost(HttpAnswer(200, b'{"expires_in": 5}')),
                                    now=lambda: NOW)
    assert err.value.kind == "unexpected-answer"
    assert _stored(key)["accessToken"] == ACCESS_B


def test_refresh_spare_WriteFailsAfterRotation_SaysTheSpareNeedsANewSignIn(machine, monkeypatch):
    key = _key(machine, "account-b@example.com")

    def broken_write(oauth):
        raise PermissionError("denied")

    monkeypatch.setattr(key, "write_oauth", broken_write)
    with pytest.raises(RefreshError) as err:
        license_usage.refresh_spare(key, force=True, http_post_json=FakePost(_token_answer()), now=lambda: NOW)
    assert "needs a new sign-in" in str(err.value)
    assert NEW_REFRESH not in str(err.value)


def test_refresh_spare_TheStoreRefusesTheWrite_SaysTheSpareNeedsANewSignIn(machine, monkeypatch):
    def refused(email, credentials_json):
        raise LicenseStoreError(f"{email} is not held here", ["cc-devthrottle license list"])

    monkeypatch.setattr(machine, "store_refreshed_key", refused)
    with pytest.raises(RefreshError) as err:
        license_usage.refresh_spare(_key(machine, "account-b@example.com"), force=True,
                                    http_post_json=FakePost(_token_answer()), now=lambda: NOW)
    assert "LicenseStoreError" in str(err.value) and "needs a new sign-in" in str(err.value)


def test_refresh_spare_WritesThroughTheKeyStore_AndItLogsTheRefresh(machine):
    license_usage.refresh_spare(_key(machine, "account-b@example.com"), force=True,
                                http_post_json=FakePost(_token_answer()), now=lambda: NOW)
    log_text = (Path(machine.store_dir) / "license-store.log").read_text(encoding="utf-8")
    assert '"event": "key-refreshed"' in log_text
    for secret in ALL_SECRETS:
        assert secret not in log_text


def _other_process(machine):
    """The same store opened a second time: its lock is a separate handle on the lock file, exactly what
    another process - a Director running a switch - holds."""
    return LicenseStore(machine.default_config_dir, machine.store_dir, backend=FileKeyBackend())


def _assert_store_lock_released(machine):
    with _other_process(machine).exclusive():
        pass


def test_refresh_spare_ASwitchHoldsTheStore_IsBusyAndSendsNothing(machine):
    key = _key(machine, "account-b@example.com")
    post = FakePost(_token_answer())
    with _other_process(machine).exclusive():
        with pytest.raises(RefreshError) as err:
            license_usage.refresh_spare(key, force=True, http_post_json=post, now=lambda: NOW)
    assert err.value.kind == "busy"
    assert post.calls == [], "nothing was sent, so no refresh token was spent"
    assert _stored(key)["refreshToken"] == REFRESH_B


def test_refresh_spare_ASwitchTriedWhileTheProviderAnswers_IsRefusedAndTheRotatedKeyIsKept(machine):
    """Review round 2, first order: before the fix the switch ran in this gap, the write was refused, and
    the active folder ended with the spent refresh token."""
    key = _key(machine, "account-b@example.com")
    other = _other_process(machine)
    refused = []

    def provider_answers_while_a_switch_is_tried(url, payload, headers, timeout):
        with pytest.raises(LicenseStoreError) as err:
            other.switch("account-b@example.com")
        refused.append(err.value.reason_code)
        return _token_answer()

    result = license_usage.refresh_spare(key, force=True, http_post_json=provider_answers_while_a_switch_is_tried,
                                         now=lambda: NOW)
    assert refused == ["switch-in-progress"]
    assert result.refreshed and _stored(key)["refreshToken"] == NEW_REFRESH
    # The switch the Director retries afterwards moves the RENEWED key: nothing spent is left anywhere.
    other.switch("account-b@example.com")
    active = json.loads((Path(machine.default_config_dir) / ".credentials.json").read_text(encoding="utf-8"))
    assert active["claudeAiOauth"]["refreshToken"] == NEW_REFRESH


def test_refresh_spare_ASwitchFinishedFirst_RefusesTheNowActiveAccountAndSendsNothing(machine):
    """Review round 2, second order: the handle was made while B was a spare, then a switch made B active.
    Before the fix the refresh went ahead and the handle was left pointing at the emptied spare folder."""
    key = _key(machine, "account-b@example.com")
    _other_process(machine).switch("account-b@example.com")
    post = FakePost(_token_answer())
    with pytest.raises(RefreshError) as err:
        license_usage.refresh_spare(key, force=True, http_post_json=post, now=lambda: NOW)
    assert err.value.kind == "active"
    assert post.calls == []
    assert key.folder == Path(machine.default_config_dir), "the handle follows the account to where it is now"


def test_read_with_refresh_ASwitchFinishedFirst_ReadsTheAccountWhereItIsNow(machine):
    # The handle was made while B was a spare; a switch made B active. B is renewed and read where it is NOW - in
    # the default folder, by the active account's rule (half an hour left and no session, so it is renewed).
    key = _key(machine, "account-b@example.com")
    _other_process(machine).switch("account-b@example.com")
    get = FakeGet(HttpAnswer(200, GOOD_BODY))
    reading = license_usage.read_with_refresh(key, get, FakePost(_token_answer()), now=lambda: NOW + 3600 * 4.5,
                                              sessions_in_use=lambda folder: [])
    assert reading.read_error is None
    assert key.folder == Path(machine.default_config_dir)
    assert get.calls[0][1]["Authorization"] == f"Bearer {NEW_ACCESS}"


def test_read_with_refresh_StoreBusy_ReadsTheKeyAsItIsInsteadOfBarringIt(machine):
    key = _key(machine, "account-b@example.com")
    get = FakeGet(HttpAnswer(200, GOOD_BODY))
    post = FakePost(_token_answer())
    with _other_process(machine).exclusive():
        reading = license_usage.read_with_refresh(key, get, post, now=lambda: NOW + 3600 * 4.5)
    assert reading.read_error is None, "a busy store is not refresh-failed: that would bar a usable spare"
    assert post.calls == []


def test_read_with_refresh_DueSpare_IsRenewedThenReadWithTheNewKey(machine):
    key = _key(machine, "account-b@example.com")
    get = FakeGet(HttpAnswer(200, GOOD_BODY))
    reading = license_usage.read_with_refresh(key, get, FakePost(_token_answer()), now=lambda: NOW + 3600 * 4.5)
    assert reading.read_error is None
    assert get.calls[0][1]["Authorization"] == f"Bearer {NEW_ACCESS}"


def test_read_with_refresh_RenewalFails_IsRefreshFailedAndNothingIsRead(machine):
    key = _key(machine, "account-b@example.com")
    get = FakeGet(HttpAnswer(200, GOOD_BODY))
    reading = license_usage.read_with_refresh(key, get, FakePost(HttpAnswer(400, b'{"error":"invalid_grant"}')),
                                              now=lambda: NOW + 3600 * 4.5)
    assert reading.read_error == "refresh-failed"
    assert "new sign-in" in reading.read_error_detail
    assert get.calls == []


# --- the active key (phase 6): renewed in place only when no Claude Code session can be using it -------------


A_IN_HALF_AN_HOUR = NOW + 3600 * 4.5  # account A's token has 30 minutes left
A_EXPIRED = NOW + 3600 * 5 + 60  # account A's token ended a minute ago


def _no_sessions(folder):
    return []


def _one_session(folder):
    return [4242]


def test_read_with_refresh_ActiveKey_UnderAnHourAndNoSession_IsRenewedInPlaceThenRead(machine):
    # The machine's Director is the only thing running: the active key has half an hour left and no Claude Code
    # session is registered in the default folder, so it is renewed where it lives and read with the new token.
    post, get = FakePost(_token_answer()), FakeGet(HttpAnswer(200, GOOD_BODY))
    key = _key(machine, "account-a@example.com")

    reading = license_usage.read_with_refresh(key, get, post, now=lambda: A_IN_HALF_AN_HOUR,
                                              sessions_in_use=_no_sessions)

    assert reading.read_error is None
    assert [payload["refresh_token"] for _, payload in post.calls] == [REFRESH_A]
    assert get.calls[0][1]["Authorization"] == f"Bearer {NEW_ACCESS}"
    stored = _stored(key)
    assert (stored["accessToken"], stored["refreshToken"]) == (NEW_ACCESS, NEW_REFRESH)
    assert key.folder == Path(machine.default_config_dir)
    _assert_store_lock_released(machine)
    assert not (key.folder / ".oauth_refresh.lock").exists(), "Claude Code's lock is released"
    assert not Path(f"{machine.default_config_dir}.lock").exists(), "and so is its legacy lock"


def test_read_with_refresh_ActiveKey_ASessionIsUsingIt_IsNotRenewed(machine):
    # A Claude Code session is running on the active key and its token still works: the session renews it itself
    # before it ends, so nothing is sent from here and the key is read as it is.
    post, get = FakePost(_token_answer()), FakeGet(HttpAnswer(200, GOOD_BODY))
    key = _key(machine, "account-a@example.com")

    reading = license_usage.read_with_refresh(key, get, post, now=lambda: A_IN_HALF_AN_HOUR,
                                              sessions_in_use=_one_session)

    assert reading.read_error is None
    assert post.calls == []
    assert get.calls[0][1]["Authorization"] == f"Bearer {ACCESS_A}"
    assert _stored(key)["accessToken"] == ACCESS_A


def test_read_with_refresh_ActiveKey_MoreThanAnHourLeft_IsNotRenewed(machine):
    post = FakePost(_token_answer())
    reading = license_usage.read_with_refresh(_key(machine, "account-a@example.com"),
                                              FakeGet(HttpAnswer(200, GOOD_BODY)), post, now=lambda: NOW + 3600 * 3.5,
                                              sessions_in_use=_no_sessions)
    assert reading.read_error is None
    assert post.calls == []


def test_read_with_refresh_ActiveKey_AlreadyExpiredWhileASessionIsRegistered_IsRenewed(machine):
    # Sessions that sat idle past the token's end: no session can be using a dead token, so renewing it ends
    # nothing. Each session renews under its own lock on its next call, reads the stored key there first, and
    # finds this renewal instead of posting the spent refresh token.
    post, get = FakePost(_token_answer()), FakeGet(HttpAnswer(200, GOOD_BODY))
    key = _key(machine, "account-a@example.com")

    reading = license_usage.read_with_refresh(key, get, post, now=lambda: A_EXPIRED, sessions_in_use=_one_session)

    assert reading.read_error is None
    assert len(post.calls) == 1
    assert _stored(key)["accessToken"] == NEW_ACCESS


def test_refresh_key_ActiveKey_ClaudeCodeHoldsItsRefreshLock_IsBusyAndSendsNothing(machine):
    # A Claude Code session is renewing the key this moment (its lock folder is fresh): nothing is sent and
    # nothing is written; the read goes ahead with the key as it is, and the next round tries again.
    key = _key(machine, "account-a@example.com")
    lock = key.folder / ".oauth_refresh.lock"
    lock.mkdir()
    os.utime(lock, (A_IN_HALF_AN_HOUR - 5, A_IN_HALF_AN_HOUR - 5))  # touched five seconds ago, as a holder does
    post = FakePost(_token_answer())

    with pytest.raises(RefreshError) as err:
        license_usage.refresh_key(key, http_post_json=post, now=lambda: A_IN_HALF_AN_HOUR,
                                  sessions_in_use=_no_sessions)
    assert err.value.kind == "busy"
    assert post.calls == [] and _stored(key)["accessToken"] == ACCESS_A
    assert (key.folder / ".oauth_refresh.lock").is_dir(), "the session's lock is left alone"

    reading = license_usage.read_with_refresh(key, FakeGet(HttpAnswer(200, GOOD_BODY)), post,
                                              now=lambda: A_IN_HALF_AN_HOUR, sessions_in_use=_no_sessions)
    assert reading.read_error is None and post.calls == []


def test_refresh_key_ActiveKey_OnlyTheLegacyLockIsHeld_IsBusy_AndTheFirstLockIsGivenBack(machine):
    # Claude Code takes two locks; a session holding only the legacy one still holds the key. Nothing is sent, and
    # the lock folder taken first is released again, so no session is left waiting on it.
    key = _key(machine, "account-a@example.com")
    legacy = Path(f"{os.path.realpath(machine.default_config_dir)}.lock")
    legacy.mkdir()
    os.utime(legacy, (A_IN_HALF_AN_HOUR - 5, A_IN_HALF_AN_HOUR - 5))
    post = FakePost(_token_answer())

    with pytest.raises(RefreshError) as err:
        license_usage.refresh_key(key, http_post_json=post, now=lambda: A_IN_HALF_AN_HOUR,
                                  sessions_in_use=_no_sessions)

    assert err.value.kind == "busy" and post.calls == []
    assert not (key.folder / ".oauth_refresh.lock").exists()
    assert legacy.is_dir()


def test_refresh_key_ActiveKey_ALockLeftByAKilledSession_IsTakenAfterSixtySeconds(machine):
    # Claude Code treats its lock as abandoned once untouched for 60 seconds; so does this, or a session killed
    # mid-renewal would stop the active key being renewed for ever.
    key = _key(machine, "account-a@example.com")
    lock = key.folder / ".oauth_refresh.lock"
    lock.mkdir()
    os.utime(lock, (A_IN_HALF_AN_HOUR - 61, A_IN_HALF_AN_HOUR - 61))
    post = FakePost(_token_answer())

    result = license_usage.refresh_key(key, http_post_json=post, now=lambda: A_IN_HALF_AN_HOUR,
                                       sessions_in_use=_no_sessions)

    assert result.refreshed and len(post.calls) == 1
    assert not lock.exists()


def test_refresh_key_ActiveKey_ASessionRenewedItFirst_UsesThatAndSendsNothing(machine, monkeypatch):
    # Between the first read and taking Claude Code's lock, a session renewed the key. It is read again under
    # the lock - as Claude Code does - and the session's renewal stands; the spent refresh token is never posted.
    key = _key(machine, "account-a@example.com")
    real_lock = license_usage.claude_refresh_lock

    @contextlib.contextmanager
    def lock_after_a_session_renewed(folder, now):
        _sign_in(Path(folder), "Account-A@example.com", "sk-ant-oat01-ACCESS-SESSION-never-shown",
                 "sk-ant-ort01-REFRESH-SESSION-never-shown", 3600 * 8)
        with real_lock(folder, now):
            yield

    monkeypatch.setattr(license_usage, "claude_refresh_lock", lock_after_a_session_renewed)
    post = FakePost(_token_answer())

    result = license_usage.refresh_key(key, http_post_json=post, now=lambda: A_IN_HALF_AN_HOUR,
                                       sessions_in_use=_no_sessions)

    assert not result.refreshed and result.reason == "renewed-by-a-session"
    assert post.calls == []
    assert _stored(key)["accessToken"] == "sk-ant-oat01-ACCESS-SESSION-never-shown"


def test_read_with_refresh_ActiveKey_RenewalRefused_IsRefreshFailedSayingItNeedsASignIn(machine):
    # The token endpoint refuses the active key's refresh token: never silent - the reading is refresh-failed,
    # which the Director reports as "the account needs a new sign-in", and nothing is read with the dead key.
    get = FakeGet(HttpAnswer(200, GOOD_BODY))
    reading = license_usage.read_with_refresh(_key(machine, "account-a@example.com"), get,
                                              FakePost(HttpAnswer(400, b'{"error": "invalid_grant"}')),
                                              now=lambda: A_IN_HALF_AN_HOUR, sessions_in_use=_no_sessions)
    assert reading.read_error == "refresh-failed"
    assert "the active account needs a new sign-in" in reading.read_error_detail
    assert get.calls == []


def test_refresh_key_ActiveKey_AfterRenewal_TheKeyIsInExactlyOnePlace_AndTheOldTokenIsGone(machine, tmp_path):
    # Every file under the machine - the default config folder, the key store, and a stand-in for the Director's
    # own folders - is searched: the new key is in one file, the default folder's credentials file, and neither
    # the old access token nor the old refresh token is left anywhere on disk.
    director = tmp_path / "director"
    (director / "logs").mkdir(parents=True)
    (director / "logs" / "director.log").write_text("nothing", encoding="utf-8")
    key = _key(machine, "account-a@example.com")

    license_usage.refresh_key(key, http_post_json=FakePost(_token_answer()), now=lambda: A_IN_HALF_AN_HOUR,
                              sessions_in_use=_no_sessions)

    holding = {secret: [] for secret in (ACCESS_A, REFRESH_A, NEW_ACCESS, NEW_REFRESH)}
    for path in tmp_path.rglob("*"):
        if path.is_file():
            text = path.read_text(encoding="utf-8", errors="replace")
            for secret in holding:
                if secret in text:
                    holding[secret].append(path)
    credentials = Path(machine.default_config_dir) / ".credentials.json"
    assert holding[NEW_ACCESS] == [credentials] and holding[NEW_REFRESH] == [credentials]
    assert holding[ACCESS_A] == [] and holding[REFRESH_A] == []


# --- which Claude Code sessions are using a folder ---------------------------------------------------------


def _register(folder: Path, pid, started_at_ms) -> None:
    (folder / "sessions").mkdir(parents=True, exist_ok=True)
    (folder / "sessions" / f"{pid}.json").write_text(json.dumps({
        "pid": pid, "sessionId": "x", "cwd": "/", "startedAt": started_at_ms, "kind": "interactive"}), encoding="utf-8")


def test_claude_sessions_in_use_NoSessionsFolder_IsNone(tmp_path):
    assert license_usage.claude_sessions_in_use(tmp_path) == []


def test_claude_sessions_in_use_ThisRunningProcess_IsInUse(tmp_path):
    # The record Claude Code writes, naming a process that is certainly running (this test's own), registered
    # after it started: in use. This reads the real process table for this one process.
    _register(tmp_path, os.getpid(), int(time.time() * 1000))
    assert license_usage.claude_sessions_in_use(tmp_path) == [os.getpid()]


def test_claude_sessions_in_use_ARecordLeftByAKilledSession_IsNotInUse(tmp_path):
    # Measured: a session that is killed leaves its record behind. Its process is gone, so it does not count.
    _register(tmp_path, 4242, 1_000_000)
    assert license_usage.claude_sessions_in_use(tmp_path, started_at=lambda pid: None) == []


def test_claude_sessions_in_use_ANumberAnotherProcessTookSince_IsNotInUse(tmp_path):
    # The record's process number now belongs to a process that started after the record was written.
    _register(tmp_path, 4242, 1_000_000_000)
    assert license_usage.claude_sessions_in_use(tmp_path, started_at=lambda pid: 1_000_000.0 + 3600) == []


def test_claude_sessions_in_use_AnUnreadableRecord_CountsAsInUse(tmp_path):
    # Not knowing is never read as "no session": the active key is then left to the sessions.
    (tmp_path / "sessions").mkdir()
    (tmp_path / "sessions" / "77.json").write_text("{not json", encoding="utf-8")
    assert license_usage.claude_sessions_in_use(tmp_path, started_at=lambda pid: None) == [-1]


def test_process_started_at_ThisProcess_IsInThePast(tmp_path):
    began = license_usage.process_started_at(os.getpid())
    assert began is not None and 0 < began <= time.time()


# --- the commands ------------------------------------------------------------------------------------


def _args(machine, *rest):
    return ["license", *rest, "--default-config-dir", machine.default_config_dir, "--store-dir", machine.store_dir]


def _dirs(machine):
    return machine.default_config_dir, machine.store_dir


def test_license_usage_Command_OneRowPerAccountWithCount(machine, monkeypatch):
    monkeypatch.setattr(license_usage, "_requests_get", FakeGet(HttpAnswer(200, GOOD_BODY)))
    result = runner.invoke(app, _args(machine, "usage"))
    assert result.exit_code == 0, result.output
    assert "count: 2 (ok 2)" in result.stdout
    assert "accounts[2]{account,state,five-hour,week,five-hour-resets,week-resets,extra-usage}:" in result.stdout
    assert "account-a@example.com,ok,42%,97.5%,2027-01-15T10:00:00Z,2027-01-20T08:00:00Z,off" in result.stdout


def test_license_usage_Command_MixedStates_AreCountedSeparately(machine, capsys):
    answers = iter([HttpAnswer(200, GOOD_BODY), HttpAnswer(401, b"")])
    license_usage_ops.usage(False, "account,state,detail", *_dirs(machine), http_get=lambda u, h, t: next(answers))
    text = capsys.readouterr().out
    assert "count: 2 (ok 1, unauthorized 1)" in text
    assert "account-b@example.com,unauthorized" in text
    assert "cc-devthrottle license refresh <account>" in text


def test_license_usage_Active_ReadsOnlyTheActiveHoldingInBothShapes(machine, capsys):
    get = FakeGet(HttpAnswer(200, GOOD_BODY))
    license_usage_ops.usage(True, None, *_dirs(machine), active_only=True, http_get=get)
    answer = json.loads(capsys.readouterr().out)
    assert (answer["count"], answer["total"]) == (1, 2)
    rows = answer["accounts"]
    assert [r["accountEmail"] for r in rows] == ["account-a@example.com"]
    assert len(get.calls) == 1
    license_usage_ops.usage(False, None, *_dirs(machine), active_only=True, http_get=get)
    assert "count: 1 of 2 total (ok 1)" in capsys.readouterr().out


def test_license_usage_Refresh_RenewsDueSparesThenReads(machine, capsys):
    post = FakePost(_token_answer())
    license_usage_ops.usage(True, None, *_dirs(machine), refresh_due=True, http_get=FakeGet(HttpAnswer(200, GOOD_BODY)),
                            http_post_json=post)
    rows = json.loads(capsys.readouterr().out)["accounts"]
    assert [r["readError"] for r in rows] == [None, None]
    assert post.calls == [], "nothing was due: both keys have five hours left"


def test_license_usage_Json_IsTheContractShapeAndHasNoToken(machine, capsys):
    license_usage_ops.usage(True, None, *_dirs(machine), http_get=FakeGet(HttpAnswer(200, GOOD_BODY)))
    out = capsys.readouterr().out
    answer = json.loads(out)
    assert set(answer) == {"count", "total", "defaultConfigDir", "storeDir", "recovered", "accounts"}
    assert (answer["count"], answer["total"]) == (2, 2)
    assert answer["defaultConfigDir"] == str(machine.default_config_dir)
    rows = answer["accounts"]
    assert [(r["accountEmail"], r["isActive"]) for r in rows] == [
        ("account-a@example.com", True), ("account-b@example.com", False)]
    contract = {"accountEmail", "isActive", "plan", "rateLimitTier", "fiveHour", "sevenDay", "extraUsage",
                "readAtUtc", "readError", "readErrorDetail", "signedInAtUtc", "signInExpiresAtUtc"}
    assert contract <= set(rows[0])
    for secret in ALL_SECRETS:
        assert secret not in out


def test_license_usage_FieldsWithJson_IsAUsageError(machine):
    result = runner.invoke(app, _args(machine, "usage", "--json", "--fields", "account"))
    assert result.exit_code == 2


def test_license_usage_UnknownField_IsAUsageErrorListingValidFields(machine):
    result = runner.invoke(app, _args(machine, "usage", "--fields", "bogus"))
    assert result.exit_code == 2
    assert "sign-in-expires" in result.stderr


@pytest.mark.parametrize("command", ["usage", "refresh"])
def test_license_usage_And_Refresh_RealDefault_IsRefusedByTheKeyStore(tmp_path, command):
    result = runner.invoke(app, ["license", command, "--default-config-dir", str(Path.home() / ".claude"),
                                 "--store-dir", str(tmp_path / "store")])
    assert result.exit_code == 1
    assert "refusing to use the machine's real default Claude folder" in result.stderr


def test_license_usage_NoFolderGiven_FallsToTheRealDefaultAndIsRefused(monkeypatch, tmp_path):
    monkeypatch.delenv("CC_LICENSE_DEFAULT_CONFIG_DIR", raising=False)
    monkeypatch.setenv("CC_LICENSE_STORE_DIR", str(tmp_path / "store"))
    result = runner.invoke(app, ["license", "usage"])
    assert result.exit_code == 1
    assert "CC_LICENSE_DEFAULT_CONFIG_DIR" in result.stderr


def test_license_refresh_Command_EverySpare_ReportsRotation(machine, capsys):
    license_usage_ops.refresh(None, True, False, None, *_dirs(machine), http_post_json=FakePost(_token_answer()))
    out = capsys.readouterr().out
    assert "count: 1 (refreshed 1)" in out
    assert "spares[1]{account,result,expires-at,refresh-token-rotated}:" in out
    assert "account-b@example.com,refreshed," in out and ",true" in out


def test_license_refresh_Command_Failure_ExitsOneAndNamesTheCause(machine, capsys):
    with pytest.raises(typer.Exit) as exited:
        license_usage_ops.refresh(None, True, False, None, *_dirs(machine),
                                  http_post_json=FakePost(error=NetworkFailure("ConnectTimeout")))
    assert exited.value.exit_code == 1
    captured = capsys.readouterr()
    assert "failed-network" in captured.out
    assert "1 of 1 refreshes failed" in captured.err


def test_license_refresh_Command_ActiveAccount_IsRefused(machine, capsys):
    with pytest.raises(typer.Exit):
        license_usage_ops.refresh("account-a@example.com", True, False, None, *_dirs(machine),
                                  http_post_json=FakePost(_token_answer()))
    assert "failed-active" in capsys.readouterr().out


def test_license_refresh_Command_UnknownAccount_Fails(machine):
    result = runner.invoke(app, _args(machine, "refresh", "nobody@example.com"))
    assert result.exit_code == 1
    assert "holds no account" in result.stderr


def test_license_refresh_Command_NoSpares_SaysSo(tmp_path, capsys):
    _sign_in(tmp_path / "default", "account-a@example.com", ACCESS_A, REFRESH_A, 3600)
    license_usage_ops.refresh(None, False, False, None, str(tmp_path / "default"), str(tmp_path / "store"))
    out = capsys.readouterr().out
    assert "count: 0" in out
    assert "no spare account" in out


# --- no key ever shows -------------------------------------------------------------------------------


def test_NoTokenAppearsInOutputOrLogs_AcrossEveryPath(machine, capsys, caplog):
    caplog.set_level(logging.DEBUG, logger="cc_devthrottle.license_usage")
    echo = json.dumps({"error": "invalid_grant", "echo": ALL_SECRETS}).encode()
    for get in (FakeGet(HttpAnswer(200, GOOD_BODY)), FakeGet(HttpAnswer(401, echo)), FakeGet(HttpAnswer(500, echo)),
                FakeGet(HttpAnswer(429, echo)), FakeGet(error=NetworkFailure("ConnectionError"))):
        for as_json in (False, True):
            license_usage_ops.usage(as_json, None, *_dirs(machine), http_get=get)
    for post in (FakePost(HttpAnswer(400, echo)), FakePost(HttpAnswer(500, echo)),
                 FakePost(error=NetworkFailure("ConnectionError")), FakePost(_token_answer())):
        for as_json in (False, True):
            try:
                license_usage_ops.refresh(None, True, as_json, None, *_dirs(machine), http_post_json=post)
            except typer.Exit:
                pass
    captured = capsys.readouterr()
    everything = captured.out + captured.err + caplog.text
    assert "account-b@example.com" in everything and "refresh account-b" in caplog.text, \
        "the check must have seen real output and real log lines"
    for secret in ALL_SECRETS:
        assert secret not in everything


# --- review of pull request 3858 (missions/license-switching/review-3858-answers.md) -----------------


def test_read_usage_TokenEchoedInAMalformedField_NeverReachesDetailOrLog(machine, caplog):
    caplog.set_level(logging.DEBUG, logger="cc_devthrottle.license_usage")
    body = json.dumps({"five_hour": {"utilization": 3, "resets_at": ACCESS_A}}).encode()
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"), FakeGet(HttpAnswer(200, body)),
                                       now=lambda: NOW)
    assert reading.read_error == "unexpected-answer"
    assert "five_hour.resets_at" in reading.read_error_detail
    assert ACCESS_A not in reading.read_error_detail and ACCESS_A not in caplog.text


def test_read_usage_TokenAsANumberFieldValue_NeverReachesDetail(machine):
    body = json.dumps({"five_hour": {"utilization": ACCESS_A}}).encode()
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"), FakeGet(HttpAnswer(200, body)),
                                       now=lambda: NOW)
    assert reading.read_error == "unexpected-answer"
    assert ACCESS_A not in reading.read_error_detail


def test_refresh_spare_TokenEchoedAsTheErrorCode_IsNotRepeated(machine):
    key = _key(machine, "account-b@example.com")
    echoed = "sk_ant_ort01_REFRESH_BBBB"  # letters, digits and underscores: passed the old shape check
    for error in (echoed, {"type": echoed}):
        answer = HttpAnswer(400, json.dumps({"error": error}).encode())
        with pytest.raises(RefreshError) as err:
            license_usage.refresh_spare(key, force=True, http_post_json=FakePost(answer), now=lambda: NOW)
        assert echoed not in str(err.value)
        assert err.value.kind == "unauthorized"


@pytest.mark.parametrize("code", ["invalid_request", "invalid_client", "invalid_grant", "unauthorized_client",
                                  "unsupported_grant_type", "invalid_scope"])
def test_refresh_spare_KnownOAuthErrorCode_IsNamed(machine, code):
    answer = HttpAnswer(400, json.dumps({"error": code}).encode())
    with pytest.raises(RefreshError) as err:
        license_usage.refresh_spare(_key(machine, "account-b@example.com"), force=True,
                                    http_post_json=FakePost(answer), now=lambda: NOW)
    assert f", {code})" in str(err.value)


def test_NetworkFailure_WithTextThatIsNotATypeName_IsNotRepeated(machine):
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"),
                                       FakeGet(error=NetworkFailure(f"refused for {ACCESS_A}")), now=lambda: NOW)
    assert reading.read_error == "network"
    assert ACCESS_A not in reading.read_error_detail


@pytest.mark.parametrize("value", [-4, -0.01, 100.5, 250, "NaN", "Infinity", "-Infinity"])
@pytest.mark.parametrize("field", ["five_hour", "seven_day", "extra_usage"])
def test_read_usage_PercentageOutside0To100OrNotFinite_IsUnexpectedAnswer(machine, field, value):
    good = json.loads(GOOD_BODY)
    raw = json.dumps(good).replace('"utilization": null', '"utilization": 0')
    body = json.loads(raw)
    body[field]["utilization"] = 1234567.0  # placeholder, replaced as raw text below
    text = json.dumps(body).replace("1234567.0", value if isinstance(value, str) else json.dumps(value))
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"), FakeGet(HttpAnswer(200, text.encode())),
                                       now=lambda: NOW)
    assert reading.read_error == "unexpected-answer"
    assert f"{field}.utilization is outside 0 to 100" in reading.read_error_detail
    assert reading.five_hour is None and reading.seven_day is None


@pytest.mark.parametrize("value", [0, 0.0, 100, 100.0])
def test_read_usage_PercentageAtTheBounds_IsAReading(machine, value):
    body = json.dumps({"five_hour": {"utilization": value, "resets_at": None},
                       "extra_usage": {"is_enabled": True, "utilization": value}}).encode()
    reading = license_usage.read_usage(_key(machine, "account-a@example.com"), FakeGet(HttpAnswer(200, body)),
                                       now=lambda: NOW)
    assert reading.read_error is None
    assert reading.five_hour.utilization == float(value)
    assert reading.extra_usage.utilization == float(value)
