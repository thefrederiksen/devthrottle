"""How much of each Claude account is used, and keeping a spare account's sign-in key usable.

Two things live here, for the License Switching mission (missions/license-switching/MISSION.md):

USAGE. For every account this machine holds - the active one in the default config folder, and each
spare - read `GET https://api.anthropic.com/api/oauth/usage` with that account's own access token. The
answer gives the 5-hour window and the week (`utilization` in percent, `resets_at`) and the extra-usage
state. Only those numbers ever leave this machine. A reading is shaped as the contract's
`LicenseHolding` (missions/license-switching/contract.md section 2.2), so the Director passes it on
without translating it.

A reading that failed carries exactly one read-error code from the contract and NO numbers: this
command keeps no state between runs, and an old number shown as if just read would be a lie. The contract's
"a failed read keeps the previous numbers and their ReadAtUtc" is done by the Director, the long-lived
side that already holds the last report. The codes are never folded into one error, because each
needs a different fix:

    unauthorized       the server refused the key (401 or 403). If the key's own expiry time had
                       passed, it is a spare that was not refreshed in time, and the detail says so;
                       otherwise it was signed out or revoked, and only a new sign-in fixes it
    refresh-failed     the key was due for renewal and the renewal failed (see SPARE REFRESH)
    rate-limited       the server answered 429
    network            the server was not reached at all (no connection, a timeout)
    unexpected-answer  the server answered, but not with a reading this code understands
    store-unreadable   the stored sign-in could not be read

SPARE REFRESH. An access token lives 8 hours; a spare that sits unused would be dead by the time a
switch needs it. Nothing Claude Code offers renews an idle folder: `claude auth status` reads the stored
key and never refreshes it (it even says "logged in" for an expired key), and Claude Code renews a key
only on its way to an API call. So `refresh_spare` posts the spare's refresh token to the token endpoint
exactly as Claude Code does, in the spare's own folder, so the sign-in still exists in exactly one
place. What was measured is in `missions/license-switching/spare-refresh.md`; the facts the code relies
on are:

- every refresh ROTATES the refresh token, and the old access token is refused at once (401). A key held
  in two places is therefore broken by the first refresh in either - the reason a key is never copied;
- the refresh token keeps the end date it was issued with (about 30 days after the sign-in): renewing
  does not extend it. A spare needs a new sign-in before then, so `signInExpiresAtUtc` is reported;
- a spent refresh token answers 400 `invalid_grant`, and trying one did not harm the current one.

ACTIVE REFRESH (phase 6). The ACTIVE key is renewed the same way, in place, but only when that cannot
pull the key out from under a running Claude Code session. What Claude Code 2.1.302 does was read from
its binary (missions/license-switching/phase-6/track-b/active-renewal.md has the evidence):

- a session renews its key only on its way to an API call, when under 5 minutes are left, holding a lock
  Claude Code takes on the config folder: the folder `<config>/.oauth_refresh.lock`, made with mkdir and
  stale after 60 seconds, plus a legacy `<config>.lock` beside the folder;
- after taking that lock, and again after any 401, it reads the stored key again and uses the newer one
  if the stored key changed - so a key renewed under it is picked up, not fought over;
- every running session registers itself as `<config>/sessions/<pid>.json` and removes it when it ends
  (a session that is killed leaves its record behind, so a record counts only while its process lives).

So `refresh_key` renews the active key when its access token has under REFRESH_MARGIN_SECONDS left
AND no live session is registered in the default folder - or once the token has already expired, when no
session can be using it. It holds the key store's lock and Claude Code's own two locks from reading the
refresh token to writing the renewed key, so it never runs inside a session's renewal, and a session that
wants to renew meanwhile waits for it and then finds the renewed key. A session that is running with time
left renews its key itself; nothing is done for it. The key exists in one place throughout: it is read,
renewed and rewritten where it lives.

THE KEY ITSELF IS NEVER SHOWN. No access token or refresh token is printed, logged, put in an exception
message, or sent anywhere but as the Bearer header to api.anthropic.com and the refresh token to the
token endpoint. Error text is built from status codes and fixed words only, never from a server body
that might echo what it was sent.

Keys are reached only through the key store (`license_store`): its `held_accounts()` says what this
machine holds, `HeldAccount.read_key()` reads a key, and `LicenseStore.store_refreshed_key()` writes a
renewed one back in the same place. It is the only code that knows where a key is kept on each platform,
the macOS Keychain included. `KeyHandle` below is the small view of one held account this module needs,
so the tests can drive it with plain files.
"""

from __future__ import annotations

import json
import logging
import math
import os
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from contextlib import contextmanager
from typing import Any, Callable, ContextManager, Dict, Iterator, List, Optional, Protocol

from .license_store import HeldAccount, LicenseStore, LicenseStoreError
from .license_store import ROLE_ACTIVE as STORE_ROLE_ACTIVE

log = logging.getLogger("cc_devthrottle.license_usage")

USAGE_URL = "https://api.anthropic.com/api/oauth/usage"
#: The beta header the usage endpoint requires.
OAUTH_BETA = "oauth-2025-04-20"
#: Claude Code's token endpoint and public client id (read from Claude Code 2.1.296).
TOKEN_URL = "https://platform.claude.com/v1/oauth/token"
CLAUDE_CODE_CLIENT_ID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
USER_AGENT = "cc-devthrottle-license"
HTTP_TIMEOUT_SECONDS = 20

#: A spare is refreshed when its access token has less than this left. The Director reads spares every
#: ten minutes, so an hour leaves several chances before the token actually dies.
REFRESH_MARGIN_SECONDS = 60 * 60

# The contract's read-error codes (LicenseReadErrors).
UNAUTHORIZED = "unauthorized"
REFRESH_FAILED = "refresh-failed"
RATE_LIMITED = "rate-limited"
NETWORK = "network"
UNEXPECTED_ANSWER = "unexpected-answer"
STORE_UNREADABLE = "store-unreadable"
READ_ERRORS = (UNAUTHORIZED, REFRESH_FAILED, RATE_LIMITED, NETWORK, UNEXPECTED_ANSWER, STORE_UNREADABLE)

#: Why a refresh did not happen, beyond the read-error codes.
REFUSED_ACTIVE = "active"
BUSY = "busy"
#: The active key was not renewed because a Claude Code session is using it and its token is still alive:
#: the session renews it itself. Not a failure.
IN_USE = "in-use"

#: Claude Code's own refresh lock (read from Claude Code 2.1.302): a folder made with mkdir inside the config
#: folder, and a legacy one named `<config folder>.lock` beside it. Both are held while a key is renewed.
CLAUDE_REFRESH_LOCK = ".oauth_refresh.lock"
#: Claude Code treats its lock as abandoned when the folder was not touched for this long (its `stale`), and so
#: does this module. A renewal here holds it for at most one HTTP call (HTTP_TIMEOUT_SECONDS), well inside it.
CLAUDE_LOCK_STALE_SECONDS = 60
#: Where every running Claude Code registers itself: `<config>/sessions/<pid>.json`.
CLAUDE_SESSIONS_DIR = "sessions"
#: A session record's `startedAt` is written just after its process started. A process that started later than
#: this after it cannot be the one that wrote it - the number was used again by another process.
SESSION_START_SLACK_SECONDS = 5

ROLE_ACTIVE = "active"
ROLE_SPARE = "spare"


# ---------------------------------------------------------------------------------------------------
# The key interface
# ---------------------------------------------------------------------------------------------------


class StoreUnreadable(Exception):
    """The stored sign-in could not be read. The message names the place, never a key."""


class KeyWriteFailed(Exception):
    """A renewed key could not be written back. The message names the failure's type, never a key."""


class KeyHandle(Protocol):
    """One account this machine holds: where it is, and a way to read and rewrite its key.

    `read_oauth` returns Claude Code's `claudeAiOauth` block (accessToken, refreshToken, expiresAt in
    milliseconds, refreshTokenExpiresAt, scopes, subscriptionType, rateLimitTier) or raises
    StoreUnreadable. The caller never prints it. `write_oauth` replaces that block in the same place it
    was read from, atomically, leaving the rest of the stored record alone.
    """

    account: str
    folder: Path
    role: str

    def exclusive(self) -> ContextManager[None]:
        """Hold the key store's lock, and inside it bring `folder` and `role` up to date: no switch can
        move this account's key while it is held. Raises StoreBusy when another operation holds it, and
        StoreUnreadable when the account is no longer held."""
        ...

    def read_oauth(self) -> Dict[str, Any]: ...

    def write_oauth(self, oauth: Dict[str, Any]) -> None: ...

    def signed_in_at_utc(self, sign_in_ends: Optional[float]) -> Optional[str]:
        """When the sign-in that ends at `sign_in_ends` (epoch seconds) was made, or None when not known."""
        ...


class StoreBusy(Exception):
    """Another operation on the key store - a switch, an add, a remove - is running."""


class StoreKey:
    """One `HeldAccount` of the key store, seen as a KeyHandle."""

    def __init__(self, store: LicenseStore, held: HeldAccount) -> None:
        self._store = store
        self._held = held
        self._document: Optional[Dict[str, Any]] = None
        self.account = held.email or ""
        self._take(held)

    def _take(self, held: HeldAccount) -> None:
        self._held = held
        self._document = None
        self.folder = Path(held.config_dir)
        self.role = ROLE_ACTIVE if held.role == STORE_ROLE_ACTIVE else ROLE_SPARE

    @contextmanager
    def exclusive(self) -> Iterator[None]:
        try:
            lock = self._store.exclusive()
            lock.__enter__()
        except LicenseStoreError as ex:
            raise StoreBusy(str(ex)) from None
        try:
            # Where the account is NOW: a switch may have moved it since this handle was made.
            try:
                current = [h for h in self._store.held_accounts()
                           if (h.email or "").lower() == self.account.lower()]
            except LicenseStoreError as ex:
                raise StoreUnreadable(str(ex)) from None
            if not current:
                raise StoreUnreadable(f"{self.account} is no longer held on this machine.")
            self._take(current[0])
            yield
        finally:
            lock.__exit__(None, None, None)

    def read_oauth(self) -> Dict[str, Any]:
        try:
            text = self._held.read_key().reveal()
        except LicenseStoreError as ex:
            raise StoreUnreadable(str(ex)) from None
        try:
            document = json.loads(text)
        except ValueError:
            raise StoreUnreadable(f"the sign-in kept at {self._held.key_location} is not JSON") from None
        oauth = document.get("claudeAiOauth") if isinstance(document, dict) else None
        if not isinstance(oauth, dict):
            raise StoreUnreadable(f"the sign-in kept at {self._held.key_location} has no claudeAiOauth block")
        self._document = document
        return oauth

    def write_oauth(self, oauth: Dict[str, Any]) -> None:
        if self._document is None:
            self.read_oauth()
        document = dict(self._document or {})
        document["claudeAiOauth"] = oauth
        try:
            self._store.store_refreshed_key(self.account, json.dumps(document))
        except (LicenseStoreError, OSError) as ex:
            raise KeyWriteFailed(type(ex).__name__) from None

    def signed_in_at_utc(self, sign_in_ends: Optional[float]) -> Optional[str]:
        ends = None if sign_in_ends is None else datetime.fromtimestamp(sign_in_ends, timezone.utc)
        made = self._store.signed_in_at(self.account, ends)
        return None if made is None else iso_utc(made.timestamp())


def held_keys(store: LicenseStore) -> List[KeyHandle]:
    """Every account the store holds, active first."""
    return [StoreKey(store, held) for held in store.held_accounts()]


# ---------------------------------------------------------------------------------------------------
# The HTTP layer - one seam, faked in the tests
# ---------------------------------------------------------------------------------------------------


class NetworkFailure(Exception):
    """The server was not reached. The message is the failure's type name, never what was sent."""


def _network_kind(ex: NetworkFailure) -> str:
    """The failure's type name as raised by the HTTP layer, repeated only when it looks like one."""
    text = str(ex)
    return text if text.isidentifier() and len(text) <= 60 else "a connection failure"


@dataclass
class HttpAnswer:
    status: int
    body: bytes


HttpGet = Callable[[str, Dict[str, str], float], HttpAnswer]
HttpPostJson = Callable[[str, Dict[str, Any], Dict[str, str], float], HttpAnswer]


def _requests_get(url: str, headers: Dict[str, str], timeout: float) -> HttpAnswer:
    import requests

    try:
        response = requests.get(url, headers=headers, timeout=timeout, allow_redirects=False)
    except requests.RequestException as ex:
        # The exception's own text can quote the request; only its type goes out.
        raise NetworkFailure(type(ex).__name__) from None
    return HttpAnswer(response.status_code, response.content)


def _requests_post_json(url: str, payload: Dict[str, Any], headers: Dict[str, str], timeout: float) -> HttpAnswer:
    import requests

    try:
        response = requests.post(url, json=payload, headers=headers, timeout=timeout, allow_redirects=False)
    except requests.RequestException as ex:
        raise NetworkFailure(type(ex).__name__) from None
    return HttpAnswer(response.status_code, response.content)


# ---------------------------------------------------------------------------------------------------
# Times
# ---------------------------------------------------------------------------------------------------


def iso_utc(epoch_seconds: float) -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(epoch_seconds))


def _utc_text(value: str, field: str) -> str:
    """A provider time such as `2026-10-11T18:10:00.024253+00:00` as `2026-10-11T18:10:00Z`.

    `field` is a name this code wrote. The provider's value never reaches the error: a field the
    provider filled wrongly could hold anything, a sign-in key echoed back included."""
    try:
        parsed = datetime.fromisoformat(value)
    except ValueError:
        raise BadAnswer(f"{field} is not a time") from None
    if parsed.tzinfo is None:
        raise BadAnswer(f"{field} is a time without an offset")
    return parsed.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _millis_to_seconds(value: Any) -> Optional[float]:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    return value / 1000.0


# ---------------------------------------------------------------------------------------------------
# Is a Claude Code session using a config folder?
# ---------------------------------------------------------------------------------------------------


def process_started_at(pid: int) -> Optional[float]:
    """When process `pid` started, in seconds since 1970; None when no such process is running. A process that
    exists but whose start cannot be read (another user's) answers 0.0, so it still counts as running."""
    if pid <= 0:
        return None
    if os.name == "nt":
        return _windows_process_started_at(pid)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return None
    except PermissionError:
        return 0.0
    stat = Path(f"/proc/{pid}/stat")
    if stat.is_file():
        # Field 22 is the start in clock ticks after boot; the fields after the command name, which is in
        # parentheses and may hold spaces, start at field 3.
        ticks = int(stat.read_text().rsplit(")", 1)[1].split()[19])
        boot = next(float(line.split()[1]) for line in Path("/proc/stat").read_text().splitlines()
                    if line.startswith("btime "))
        return boot + ticks / os.sysconf("SC_CLK_TCK")
    import subprocess

    answer = subprocess.run(["ps", "-o", "lstart=", "-p", str(pid)], capture_output=True, text=True, timeout=10,
                            env={**os.environ, "LC_ALL": "C", "TZ": "UTC"})
    if answer.returncode != 0 or not answer.stdout.strip():
        return None
    return datetime.strptime(" ".join(answer.stdout.split()), "%a %b %d %H:%M:%S %Y").replace(
        tzinfo=timezone.utc).timestamp()


def _windows_process_started_at(pid: int) -> Optional[float]:
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    process_query_limited_information, still_active, access_denied = 0x1000, 259, 5
    handle = kernel32.OpenProcess(process_query_limited_information, False, pid)
    if not handle:
        return 0.0 if ctypes.get_last_error() == access_denied else None
    try:
        code = wintypes.DWORD()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value != still_active:
            return None
        times = [wintypes.FILETIME() for _ in range(4)]
        if not kernel32.GetProcessTimes(handle, *(ctypes.byref(t) for t in times)):
            return 0.0
        created = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        return created / 10_000_000 - 11_644_473_600  # 100-nanosecond steps since 1601 -> seconds since 1970
    finally:
        kernel32.CloseHandle(handle)


ProcessStartedAt = Callable[[int], Optional[float]]


def claude_sessions_in_use(folder: Path, started_at: ProcessStartedAt = process_started_at) -> List[int]:
    """The process numbers of the Claude Code sessions registered in `<folder>/sessions/` that are running now.

    A record counts while its process lives and started no later than the record says it did (plus
    SESSION_START_SLACK_SECONDS); a record whose process is gone, or whose number another process took since, is
    left from a session that was killed and does not count. A record that cannot be read counts as in use - not
    knowing is never read as "no session"."""
    sessions = folder / CLAUDE_SESSIONS_DIR
    if not sessions.is_dir():
        return []
    live: List[int] = []
    for record in sorted(sessions.glob("*.json")):
        try:
            data = json.loads(record.read_text(encoding="utf-8"))
            pid, registered = int(data["pid"]), float(data["startedAt"]) / 1000.0
        except (OSError, ValueError, KeyError, TypeError):
            log.info("sessions: %s cannot be read, so it counts as a session in use", record.name)
            live.append(-1)
            continue
        began = started_at(pid)
        if began is not None and began <= registered + SESSION_START_SLACK_SECONDS:
            live.append(pid)
    return live


# ---------------------------------------------------------------------------------------------------
# Claude Code's own refresh lock
# ---------------------------------------------------------------------------------------------------


class ClaudeLockBusy(Exception):
    """A Claude Code session holds its refresh lock on the config folder."""


def _claude_lock_paths(folder: Path) -> List[Path]:
    real = os.path.realpath(folder)
    return [folder / CLAUDE_REFRESH_LOCK, Path(f"{real}.lock")]


def _take_claude_lock(path: Path, now: Callable[[], float]) -> None:
    try:
        os.mkdir(path)
        return
    except FileExistsError:
        pass
    try:
        age = now() - path.stat().st_mtime
    except FileNotFoundError:
        age = None  # released a moment ago
    if age is not None and age <= CLAUDE_LOCK_STALE_SECONDS:
        raise ClaudeLockBusy(f"a Claude Code session holds its refresh lock {path}")
    if age is not None:
        log.info("refresh: %s was left by a Claude Code session that ended (untouched for %d seconds); taking it",
                 path, int(age))
        try:
            os.rmdir(path)
        except FileNotFoundError:
            pass
    try:
        os.mkdir(path)
    except FileExistsError:
        raise ClaudeLockBusy(f"a Claude Code session took its refresh lock {path}") from None


@contextmanager
def claude_refresh_lock(folder: Path, now: Callable[[], float] = time.time) -> Iterator[None]:
    """Hold Claude Code's refresh lock on `folder` - the folder inside it and the legacy one beside it, taken in
    Claude Code's order. Raises ClaudeLockBusy, holding nothing, when a session holds either."""
    taken: List[Path] = []
    try:
        for path in _claude_lock_paths(folder):
            _take_claude_lock(path, now)
            taken.append(path)
        yield
    finally:
        for path in reversed(taken):
            try:
                os.rmdir(path)
            except FileNotFoundError:
                pass


# ---------------------------------------------------------------------------------------------------
# Usage
# ---------------------------------------------------------------------------------------------------


@dataclass
class Window:
    utilization: float
    resets_at_utc: Optional[str]


@dataclass
class ExtraUsage:
    is_enabled: Optional[bool]
    utilization: Optional[float]


@dataclass
class UsageReading:
    """One holding, shaped as the contract's LicenseHolding, plus where it is kept."""

    account_email: str
    is_active: bool
    folder: str
    plan: Optional[str] = None
    rate_limit_tier: Optional[str] = None
    five_hour: Optional[Window] = None
    seven_day: Optional[Window] = None
    extra_usage: Optional[ExtraUsage] = None
    read_at_utc: Optional[str] = None
    read_error: Optional[str] = None
    read_error_detail: Optional[str] = None
    sign_in_expires_at_utc: Optional[str] = None
    signed_in_at_utc: Optional[str] = None

    def to_json(self) -> Dict[str, Any]:
        def window(w: Optional[Window]) -> Optional[Dict[str, Any]]:
            return None if w is None else {"utilization": w.utilization, "resetsAtUtc": w.resets_at_utc}

        extra = None if self.extra_usage is None else {
            "isEnabled": self.extra_usage.is_enabled, "utilization": self.extra_usage.utilization}
        return {
            "accountEmail": self.account_email,
            "isActive": self.is_active,
            "plan": self.plan,
            "rateLimitTier": self.rate_limit_tier,
            "fiveHour": window(self.five_hour),
            "sevenDay": window(self.seven_day),
            "extraUsage": extra,
            "readAtUtc": self.read_at_utc,
            "readError": self.read_error,
            "readErrorDetail": self.read_error_detail,
            # When the sign-in was made. Nothing Claude Code stores records it, so it is known only for a
            # sign-in `license sign-in` made and recorded; otherwise null (the contract's "not known").
            "signedInAtUtc": self.signed_in_at_utc,
            "signInExpiresAtUtc": self.sign_in_expires_at_utc,
            "folder": self.folder,
        }


class BadAnswer(ValueError):
    """The usage answer is not one this code can read. The message is a fixed sentence naming a field
    this code knows - never a value from the answer, never an exception's own text."""


def _percent(raw: Dict[str, Any], field: str) -> Optional[float]:
    """A percentage from the answer: a finite number from 0 to 100, as the contract requires. Anything
    else - a negative, a NaN, 250 - is a broken answer, not a reading: a wrong low number would make a
    full account look like the best switch target."""
    value = raw.get("utilization")
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise BadAnswer(f"{field}.utilization is not a number")
    if not math.isfinite(value) or not 0 <= value <= 100:
        raise BadAnswer(f"{field}.utilization is outside 0 to 100")
    return float(value)


def _window(answer: Dict[str, Any], key: str) -> Optional[Window]:
    raw = answer.get(key)
    if raw is None:
        return None
    if not isinstance(raw, dict):
        raise BadAnswer(f"{key} is not an object")
    utilization = _percent(raw, key)
    if utilization is None:
        return None
    resets_at = raw.get("resets_at")
    if resets_at is not None and not isinstance(resets_at, str):
        raise BadAnswer(f"{key}.resets_at is not text")
    return Window(utilization, None if resets_at is None else _utc_text(resets_at, f"{key}.resets_at"))


def _extra(answer: Dict[str, Any]) -> Optional[ExtraUsage]:
    raw = answer.get("extra_usage")
    if raw is None:
        return None
    if not isinstance(raw, dict):
        raise BadAnswer("extra_usage is not an object")
    enabled = raw.get("is_enabled")
    if enabled is not None and not isinstance(enabled, bool):
        raise BadAnswer("extra_usage.is_enabled is not true or false")
    return ExtraUsage(enabled, _percent(raw, "extra_usage"))


def _failed(reading: UsageReading, code: str, detail: str) -> UsageReading:
    reading.read_error, reading.read_error_detail = code, detail
    reading.five_hour = reading.seven_day = reading.extra_usage = None
    reading.read_at_utc = None
    log.warning("usage %s: %s (%s)", reading.account_email, code, detail)
    return reading


def _new_reading(key: KeyHandle) -> UsageReading:
    return UsageReading(account_email=key.account.lower(), is_active=key.role == ROLE_ACTIVE, folder=str(key.folder))


def read_usage(key: KeyHandle, http_get: HttpGet = _requests_get, now: Callable[[], float] = time.time) -> UsageReading:
    """One account's usage. Always returns a reading; `read_error` is None exactly when it worked."""
    reading = _new_reading(key)
    if not key.account:
        return _failed(reading, STORE_UNREADABLE, f"The account file for {key.folder} names no email.")
    try:
        oauth = key.read_oauth()
    except StoreUnreadable as ex:
        return _failed(reading, STORE_UNREADABLE, str(ex))
    for name, attribute in (("subscriptionType", "plan"), ("rateLimitTier", "rate_limit_tier")):
        value = oauth.get(name)
        setattr(reading, attribute, value if isinstance(value, str) and value else None)
    sign_in_ends = _millis_to_seconds(oauth.get("refreshTokenExpiresAt"))
    reading.sign_in_expires_at_utc = None if sign_in_ends is None else iso_utc(sign_in_ends)
    reading.signed_in_at_utc = key.signed_in_at_utc(sign_in_ends)
    token = oauth.get("accessToken")
    if not isinstance(token, str) or not token:
        return _failed(reading, STORE_UNREADABLE, "the stored sign-in has no access token")
    expires = _millis_to_seconds(oauth.get("expiresAt"))

    headers = {"Authorization": f"Bearer {token}", "anthropic-beta": OAUTH_BETA, "User-Agent": USER_AGENT}
    asked_at = now()
    try:
        answer = http_get(USAGE_URL, headers, HTTP_TIMEOUT_SECONDS)
    except NetworkFailure as ex:
        return _failed(reading, NETWORK, f"api.anthropic.com was not reached ({_network_kind(ex)}).")

    if answer.status in (401, 403):
        if expires is not None and expires <= asked_at:
            return _failed(reading, UNAUTHORIZED,
                           f"The access token expired at {iso_utc(expires)} and was not renewed in time.")
        return _failed(reading, UNAUTHORIZED,
                       f"The server refused the sign-in (HTTP {answer.status}); it needs a new sign-in.")
    if answer.status == 429:
        return _failed(reading, RATE_LIMITED, "The server asked for fewer usage reads (HTTP 429).")
    if answer.status != 200:
        return _failed(reading, UNEXPECTED_ANSWER, f"The server answered HTTP {answer.status}.")
    try:
        body = json.loads(answer.body.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return _failed(reading, UNEXPECTED_ANSWER, "The usage answer is not JSON.")
    try:
        if not isinstance(body, dict):
            raise BadAnswer("the answer is not an object")
        reading.five_hour = _window(body, "five_hour")
        reading.seven_day = _window(body, "seven_day")
        reading.extra_usage = _extra(body)
    except BadAnswer as ex:
        return _failed(reading, UNEXPECTED_ANSWER, f"The usage answer could not be read: {ex}.")
    if reading.five_hour is None and reading.seven_day is None:
        return _failed(reading, UNEXPECTED_ANSWER, "The usage answer carried neither five_hour nor seven_day.")
    reading.read_at_utc = iso_utc(asked_at)
    log.info("usage %s: read", reading.account_email)
    return reading


def read_with_refresh(key: KeyHandle, http_get: HttpGet = _requests_get,
                      http_post_json: Optional[HttpPostJson] = None,
                      now: Callable[[], float] = time.time,
                      sessions_in_use: Callable[[Path], List[int]] = claude_sessions_in_use) -> UsageReading:
    """A holding's reading, after renewing its key when it is due (`refresh_key`): a spare by `refresh_spare`'s
    rule, the active key by the ACTIVE REFRESH rule at the top of this module. A renewal that fails is the
    reading's `refresh-failed` - the account needs a new sign-in - and nothing is read with a key that could not
    be renewed."""
    try:
        refresh_key(key, http_post_json=http_post_json or _requests_post_json, now=now,
                    sessions_in_use=sessions_in_use)
    except RefreshError as ex:
        if ex.kind == STORE_UNREADABLE:
            return _failed(_new_reading(key), STORE_UNREADABLE, str(ex))
        if ex.kind == BUSY:
            # A switch, or a Claude Code session's own renewal, is running: nothing was sent, so the key is as
            # good as it was. It is read as it is and renewed on the next round. Calling it refresh-failed would
            # bar a usable key.
            log.info("usage %s: refresh skipped, %s", key.account.lower(), ex)
        else:
            return _failed(_new_reading(key), REFRESH_FAILED, str(ex))
    return read_usage(key, http_get, now)


# ---------------------------------------------------------------------------------------------------
# Spare refresh
# ---------------------------------------------------------------------------------------------------


class RefreshError(Exception):
    """A refresh did not happen. `kind` is a read-error code, or REFUSED_ACTIVE, or BUSY."""

    def __init__(self, kind: str, message: str) -> None:
        super().__init__(message)
        self.kind = kind


@dataclass
class RefreshResult:
    account_email: str
    folder: str
    refreshed: bool
    reason: str
    expires_at_utc: Optional[str] = None
    refresh_token_rotated: Optional[bool] = None
    sign_in_expires_at_utc: Optional[str] = None

    def to_json(self) -> Dict[str, Any]:
        return {
            "accountEmail": self.account_email,
            "folder": self.folder,
            "refreshed": self.refreshed,
            "reason": self.reason,
            "expiresAtUtc": self.expires_at_utc,
            "refreshTokenRotated": self.refresh_token_rotated,
            "signInExpiresAtUtc": self.sign_in_expires_at_utc,
        }


def refresh_spare(
    key: KeyHandle,
    *,
    force: bool = False,
    http_post_json: HttpPostJson = _requests_post_json,
    now: Callable[[], float] = time.time,
) -> RefreshResult:
    """Renew a spare's access token in the spare's own folder, when it has under REFRESH_MARGIN_SECONDS
    left (or always, with `force`). Raises RefreshError; never leaves a half-done refresh unreported.

    The whole refresh - read the refresh token, ask the provider, write the renewed key - runs while the key
    store's lock is held (`key.exclusive()`). The provider spends the old refresh token the moment it
    answers, so a switch that moved the key between that answer and the write would leave the spent token
    behind with the new one nowhere. Holding the store lock, no switch can start; a switch already running
    makes this refresh `busy` before anything is sent; and the folder and role are read under the lock, so
    an account a finished switch made active is refused rather than refreshed in its old folder.
    """
    account = key.account.lower()
    try:
        with key.exclusive():
            if key.role != ROLE_SPARE:
                raise RefreshError(
                    REFUSED_ACTIVE,
                    f"{account} is the ACTIVE account. It is renewed by the Director's usage read only when no "
                    "Claude Code session is using it, never by this command.",
                )
            return _refresh_holding_the_lock(key, account, force, http_post_json, now)
    except StoreBusy as ex:
        raise RefreshError(BUSY, f"The key store is busy, so {account} was not refreshed this time: {ex}") from None
    except StoreUnreadable as ex:
        raise RefreshError(STORE_UNREADABLE, str(ex)) from None


def refresh_key(
    key: KeyHandle,
    *,
    http_post_json: HttpPostJson = _requests_post_json,
    now: Callable[[], float] = time.time,
    sessions_in_use: Callable[[Path], List[int]] = claude_sessions_in_use,
) -> RefreshResult:
    """Renew `key` when it is due, by the rule for the role it has NOW (read under the key store's lock, so a
    switch since the list was read is seen): a spare as `refresh_spare` does, the active key by the ACTIVE
    REFRESH rule at the top of this module. Raises RefreshError."""
    account = key.account.lower()
    try:
        with key.exclusive():
            if key.role == ROLE_SPARE:
                return _refresh_holding_the_lock(key, account, False, http_post_json, now)
            return _refresh_active_holding_the_lock(key, account, http_post_json, now, sessions_in_use)
    except StoreBusy as ex:
        raise RefreshError(BUSY, f"The key store is busy, so {account} was not refreshed this time: {ex}") from None
    except StoreUnreadable as ex:
        raise RefreshError(STORE_UNREADABLE, str(ex)) from None


def _refresh_active_holding_the_lock(key: KeyHandle, account: str, http_post_json: HttpPostJson,
                                     now: Callable[[], float],
                                     sessions_in_use: Callable[[Path], List[int]]) -> RefreshResult:
    """The active key's renewal, holding the key store's lock. See ACTIVE REFRESH at the top of this module."""
    oauth = _read_for_refresh(key, account)
    expires = _millis_to_seconds(oauth.get("expiresAt"))
    left = None if expires is None else expires - now()
    if left is not None and left > REFRESH_MARGIN_SECONDS:
        log.info("refresh %s (active): not needed (%d minutes left)", account, int(left // 60))
        return _not_renewed(key, account, oauth, "not-needed")
    if left is None or left > 0:
        users = sessions_in_use(key.folder)
        if users:
            # The access token still works, so a session may be using it right now. It renews the key itself,
            # under its own lock, before the token ends; a renewal from here would end that token mid-turn.
            log.info("refresh %s (active): left to the %d Claude Code session(s) using it", account, len(users))
            return _not_renewed(key, account, oauth, IN_USE)
    try:
        with claude_refresh_lock(key.folder, now):
            # A session may have renewed the key between the read above and taking its lock: read again, as
            # Claude Code itself does under this lock, and use what it wrote.
            current = _read_for_refresh(key, account)
            if current.get("accessToken") != oauth.get("accessToken"):
                log.info("refresh %s (active): a Claude Code session renewed it first", account)
                return _not_renewed(key, account, current, "renewed-by-a-session")
            return _renew(key, account, current, "active-due", now, http_post_json, what="the active account")
    except ClaudeLockBusy as ex:
        raise RefreshError(BUSY, f"{account} was not renewed this time: {ex}") from None


def _read_for_refresh(key: KeyHandle, account: str) -> Dict[str, Any]:
    try:
        oauth = key.read_oauth()
    except StoreUnreadable as ex:
        raise RefreshError(STORE_UNREADABLE, str(ex)) from None
    refresh_token = oauth.get("refreshToken")
    if not isinstance(refresh_token, str) or not refresh_token:
        raise RefreshError(STORE_UNREADABLE, f"The stored sign-in for {account} has no refresh token.")
    return oauth


def _not_renewed(key: KeyHandle, account: str, oauth: Dict[str, Any], reason: str) -> RefreshResult:
    expires = _millis_to_seconds(oauth.get("expiresAt"))
    sign_in_ends = _millis_to_seconds(oauth.get("refreshTokenExpiresAt"))
    return RefreshResult(account, str(key.folder), False, reason, None if expires is None else iso_utc(expires),
                         sign_in_expires_at_utc=None if sign_in_ends is None else iso_utc(sign_in_ends))


def _refresh_holding_the_lock(key: KeyHandle, account: str, force: bool, http_post_json: HttpPostJson,
                              now: Callable[[], float]) -> RefreshResult:
    """A spare's renewal, holding the key store's lock: when it is due, or always with `force`."""
    oauth = _read_for_refresh(key, account)
    expires = _millis_to_seconds(oauth.get("expiresAt"))
    left = None if expires is None else expires - now()
    if not force and left is not None and left > REFRESH_MARGIN_SECONDS:
        log.info("refresh %s: not needed (%d minutes left)", account, int(left // 60))
        return _not_renewed(key, account, oauth, "not-needed")
    return _renew(key, account, oauth, "forced" if force else "due", now, http_post_json, what="this spare")


def _renew(key: KeyHandle, account: str, oauth: Dict[str, Any], reason: str, now: Callable[[], float],
           http_post_json: HttpPostJson, *, what: str) -> RefreshResult:
    """Post the stored refresh token exactly as Claude Code does and write the answer back in the same place.
    `what` names the holding in a failure ("this spare", "the active account")."""
    refresh_token = oauth["refreshToken"]
    sign_in_ends = _millis_to_seconds(oauth.get("refreshTokenExpiresAt"))
    expires = _millis_to_seconds(oauth.get("expiresAt"))
    left = None if expires is None else expires - now()
    scopes = oauth.get("scopes")
    payload: Dict[str, Any] = {
        "grant_type": "refresh_token",
        "refresh_token": refresh_token,
        "client_id": oauth.get("clientId") or CLAUDE_CODE_CLIENT_ID,
    }
    if isinstance(scopes, list) and scopes:
        payload["scope"] = " ".join(str(s) for s in scopes)
    headers = {"Content-Type": "application/json", "User-Agent": USER_AGENT}
    log.info("refresh %s: renewing (%s left)", account,
             "unknown time" if left is None else f"{int(left // 60)} minutes")
    try:
        answer = http_post_json(TOKEN_URL, payload, headers, HTTP_TIMEOUT_SECONDS)
    except NetworkFailure as ex:
        raise RefreshError(NETWORK, f"The token endpoint was not reached ({_network_kind(ex)}).") from None
    if answer.status in (400, 401, 403):
        # Measured: a spent or revoked refresh token answers 400 invalid_grant.
        raise RefreshError(
            UNAUTHORIZED,
            f"The token endpoint refused the refresh token for {account} (HTTP {answer.status}"
            f"{_oauth_error_code(answer.body)}); {what} needs a new sign-in.",
        )
    if answer.status == 429:
        raise RefreshError(RATE_LIMITED, "The token endpoint asked for fewer requests (HTTP 429).")
    if answer.status != 200:
        raise RefreshError(UNEXPECTED_ANSWER, f"The token endpoint answered HTTP {answer.status}.")
    try:
        body = json.loads(answer.body.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        raise RefreshError(UNEXPECTED_ANSWER, "The token endpoint's answer is not JSON.") from None
    new_access = body.get("access_token") if isinstance(body, dict) else None
    expires_in = body.get("expires_in") if isinstance(body, dict) else None
    if not isinstance(new_access, str) or not new_access or isinstance(expires_in, bool) \
            or not isinstance(expires_in, (int, float)):
        raise RefreshError(UNEXPECTED_ANSWER, "The token endpoint's answer lacks access_token or expires_in.")
    new_refresh = body.get("refresh_token")
    rotated = isinstance(new_refresh, str) and bool(new_refresh) and new_refresh != refresh_token

    answered_at = now()
    updated = dict(oauth)
    updated["accessToken"] = new_access
    if isinstance(new_refresh, str) and new_refresh:
        updated["refreshToken"] = new_refresh
    new_expires = answered_at + float(expires_in)
    updated["expiresAt"] = int(new_expires * 1000)
    refresh_lives = body.get("refresh_token_expires_in")
    if not isinstance(refresh_lives, bool) and isinstance(refresh_lives, (int, float)):
        sign_in_ends = answered_at + float(refresh_lives)
        updated["refreshTokenExpiresAt"] = int(sign_in_ends * 1000)
    granted = body.get("scope")
    if isinstance(granted, str) and granted.strip():
        updated["scopes"] = granted.split()
    # The server has already spent the old refresh token, so if this write fails the spare is
    # lost; the error says so plainly rather than leaving it to be found at 3 AM.
    try:
        key.write_oauth(updated)
    except (OSError, KeyWriteFailed) as ex:
        failure = str(ex) if isinstance(ex, KeyWriteFailed) else type(ex).__name__
        raise RefreshError(
            UNEXPECTED_ANSWER,
            f"The renewed key for {account} could not be written ({failure}); "
            + (f"the server rotated the refresh token, so {what} needs a new sign-in."
               if rotated else "the stored key is unchanged."),
        ) from None
    log.info("refresh %s: renewed, expires %s, refresh token rotated: %s", account, iso_utc(new_expires), rotated)
    return RefreshResult(account, str(key.folder), True, reason, iso_utc(new_expires),
                         rotated, None if sign_in_ends is None else iso_utc(sign_in_ends))


#: The OAuth error codes (RFC 6749 section 5.2) a refusal may name. Only these are ever repeated: any
#: other value in the answer's `error` field could be anything the server echoed, a key included.
OAUTH_ERROR_CODES = frozenset({
    "invalid_request", "invalid_client", "invalid_grant", "unauthorized_client", "unsupported_grant_type",
    "invalid_scope",
})


def _oauth_error_code(body: bytes) -> str:
    """`, invalid_grant` when the answer names a known OAuth error code, else nothing."""
    try:
        parsed = json.loads(body.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return ""
    code = parsed.get("error") if isinstance(parsed, dict) else None
    if isinstance(code, dict):
        code = code.get("type")
    return f", {code}" if isinstance(code, str) and code in OAUTH_ERROR_CODES else ""
