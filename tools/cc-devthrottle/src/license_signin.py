"""Signing a Claude account in as a spare, and renewing a spare before its sign-in dies.

For the License Switching mission (missions/license-switching/MISSION.md, "Adding a spare to a machine").

THE FLOW (proven by hand on 10 October 2026, before this code):

1. The TARGET machine runs ``claude auth login --claudeai --email X`` with BROWSER=none and CLAUDE_CONFIG_DIR set to
   the folder the account belongs in: its spare folder for a new spare, or the folder already holding it for a
   renewal - its spare folder, or the default folder when it is the ACTIVE account. Claude Code prints an authorize
   URL and waits for a code.
2. The APPROVING machine - the one that holds a browser profile signed in to claude.ai as X - opens that URL in that
   profile. The page says "Logged in as X"; the approver refuses when it names anyone else, then clicks Authorize
   and reads the ``code#state`` the page shows.
3. The code goes back to the target, which types it into the waiting login - holding the key store's lock, after
   checking the folder still holds what it did when the login started (``LicenseStore.finish_sign_in``). Claude
   Code's own single write puts the new sign-in in place, replacing the old one when it renews. The code is useless
   anywhere else: it only completes the login that holds the matching verifier.
4. The store reads the folder back and records the time of the sign-in; ``license usage`` reports it as
   ``signedInAtUtc``.

THE SIGN-IN NEVER MOVES. It is written once, where it belongs, by Claude Code. Moving a key from one folder to
another always has a moment when it is in both, and a refresh of one copy kills the other (MISSION.md trap 2) - so
there is no staging folder and no copy step, and the folder of an active account always holds a sign-in.

The target and the approver are the same machine when the browser profile is local (``sign_in`` with
``browser_approver``). They are two machines when it is not: the target runs ``sign_in`` with an ``approve`` that
hands the URL over and reads the code back (``license sign-in --remote-approval``), and the approver runs
``approve_in_browser`` (``license approve``).

WHY RENEWAL EXISTS. A sign-in dies about 30 days after it was made, however often its access is refreshed
(missions/license-switching/spare-refresh.md) - the active account's too, or the machine stops. So every held account
is signed in again before then: ``renew_due`` renews each one whose sign-in ends within ``RENEW_AHEAD`` (five days,
so at about 25 days old), using the browser profile the
owner configured for that account. Which profile is signed in as which account is the OWNER's configuration, kept in
the key store's folder on his machine (``sign-in-browsers.json``), never in this repository. A renewal that fails
is reported in the result and makes the command exit non-zero - never silent.

NOTHING SECRET IS SHOWN. The new key never passes through this module: Claude Code writes it into the folder it
belongs in, and nothing moves it. The authorize code passes through, is checked for shape, and is typed into the login; it is
never logged, and no error message carries it.
"""

from __future__ import annotations

import datetime as _dt
import json
import logging
import os
import re
import shutil
import subprocess
import threading
import time
import urllib.parse
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional, Protocol

from .license_store import (
    LicenseStore,
    LicenseStoreError,
    is_real_default,
    sign_in_ends,
)

log = logging.getLogger("cc_devthrottle.license_signin")

#: A spare is signed in again when its sign-in ends within this. A sign-in lives about 30 days, so this renews it
#: at about 25 days, leaving five days of daily attempts before it dies.
RENEW_AHEAD = _dt.timedelta(days=5)

#: How long Claude Code may take to print the authorize URL, and to finish once the code is typed in.
URL_WAIT_SECONDS = 60
FINISH_WAIT_SECONDS = 90
#: How long the browser may take to show the consent page and then the code.
BROWSER_WAIT_SECONDS = 150

AUTHORIZE_URL = re.compile(r"https://\S+/oauth/authorize\?\S+")
#: What the callback page shows and the login reads: two long URL-safe runs joined by "#".
CODE_SHAPE = re.compile(r"^[A-Za-z0-9_\-]{20,}#[A-Za-z0-9_\-]{20,}$")

#: The owner's account-to-browser-profile map, in the key store's folder.
BROWSERS_FILE = "sign-in-browsers.json"

# Why a sign-in failed. Each needs a different fix, so they are never folded into one.
LOGIN_FAILED = "login-failed"              # claude auth login did not start, print a URL, or finish
APPROVAL_FAILED = "approval-failed"        # the browser step did not produce a code
WRONG_ACCOUNT = "wrong-account"            # the browser profile is signed in to claude.ai as someone else
NOT_SIGNED_IN = "browser-not-signed-in"    # the browser profile is not signed in to claude.ai at all
NO_APPROVER = "no-approver"                # no browser profile is set to approve the account (contract 8.1)
STORE_REFUSED = "store-refused"            # the key store would not take the new sign-in


class SignInError(Exception):
    """A sign-in did not happen. ``kind`` is one of the words above; the message never carries a code or a key."""

    def __init__(self, kind: str, message: str, next_steps: Optional[List[str]] = None):
        super().__init__(message)
        self.kind = kind
        self.next_steps = list(next_steps or [])


def utc_now() -> _dt.datetime:
    return _dt.datetime.now(_dt.timezone.utc)


def iso(moment: Optional[_dt.datetime]) -> Optional[str]:
    return None if moment is None else moment.astimezone(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# ---------------------------------------------------------------------------------------------------
# The login process on the target machine - one seam, faked in the tests
# ---------------------------------------------------------------------------------------------------


class LoginProcess(Protocol):
    """One running ``claude auth login`` into one folder."""

    def authorize_url(self, timeout_seconds: float) -> str:
        """The URL Claude Code printed. Raises SignInError (login-failed) when none came in time."""
        ...

    def finish(self, code: str, timeout_seconds: float) -> None:
        """Type ``code`` into the waiting login and wait for it to end. Raises SignInError (login-failed)."""
        ...

    def cancel(self) -> None:
        """End the login if it is still running. Safe to call more than once."""
        ...


StartLogin = Callable[[str, str], LoginProcess]
Approve = Callable[[str, str], str]


class ClaudeLogin:
    """``claude auth login --claudeai --email <email>`` with CLAUDE_CONFIG_DIR=<folder> and BROWSER=none."""

    def __init__(self, folder: str, email: str, claude: Optional[str] = None):
        exe = claude or shutil.which("claude")
        if not exe:
            raise SignInError(LOGIN_FAILED, "Claude Code (claude) is not on this machine's PATH.",
                              ["Install Claude Code, then run: claude --version"])
        env = dict(os.environ, CLAUDE_CONFIG_DIR=folder, BROWSER="none")
        if is_real_default(folder):
            # Claude Code names the real default login by CLAUDE_CONFIG_DIR being UNSET (license_store); set to the
            # same folder, it would sign in under another Keychain name.
            del env["CLAUDE_CONFIG_DIR"]
        log.info("login %s: starting claude auth login in %s", email, folder)
        self._email = email
        self._lines: List[str] = []
        self._lock = threading.Lock()
        self._process = subprocess.Popen(
            [exe, "auth", "login", "--claudeai", "--email", email], env=env,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", bufsize=1)
        threading.Thread(target=self._pump, daemon=True).start()

    def _pump(self) -> None:
        stream = self._process.stdout
        assert stream is not None
        for line in stream:
            with self._lock:
                self._lines.append(line.rstrip())

    def _output(self) -> str:
        with self._lock:
            return "\n".join(self._lines)

    def _tail(self) -> str:
        # Claude Code's own words, for an error. They never hold the code: it was typed in, not echoed.
        with self._lock:
            lines = [AUTHORIZE_URL.sub("<authorize URL>", line) for line in self._lines[-4:] if line.strip()]
        return " / ".join(lines) or "(no output)"

    def authorize_url(self, timeout_seconds: float) -> str:
        deadline = time.monotonic() + timeout_seconds
        while time.monotonic() < deadline:
            found = AUTHORIZE_URL.search(self._output())
            if found:
                log.info("login %s: authorize URL received", self._email)
                return found.group(0)
            if self._process.poll() is not None:
                raise SignInError(LOGIN_FAILED, f"claude auth login ended (exit {self._process.returncode}) "
                                                f"without an authorize URL: {self._tail()}")
            time.sleep(0.25)
        raise SignInError(LOGIN_FAILED, f"claude auth login printed no authorize URL in {timeout_seconds:g} "
                                        f"seconds: {self._tail()}")

    def finish(self, code: str, timeout_seconds: float) -> None:
        stdin = self._process.stdin
        assert stdin is not None
        try:
            stdin.write(code + "\n")
            stdin.flush()
        except OSError as ex:
            raise SignInError(LOGIN_FAILED, f"the code could not be typed into claude auth login "
                                            f"({type(ex).__name__}): {self._tail()}") from None
        try:
            rc = self._process.wait(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            raise SignInError(LOGIN_FAILED, f"claude auth login did not finish {timeout_seconds:g} seconds after "
                                            f"the code was typed in: {self._tail()}") from None
        if rc != 0:
            raise SignInError(LOGIN_FAILED, f"claude auth login ended with exit {rc}: {self._tail()}")
        log.info("login %s: finished", self._email)

    def cancel(self) -> None:
        if self._process.poll() is None:
            self._process.kill()
            self._process.wait(timeout=10)


def start_claude_login(folder: str, email: str) -> LoginProcess:
    return ClaudeLogin(folder, email)


# ---------------------------------------------------------------------------------------------------
# The sign-in, on the target machine
# ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class SignInResult:
    email: str
    role: str
    config_dir: str
    renewed: bool
    signed_in_at_utc: str
    sign_in_expires_at_utc: Optional[str]

    def to_json(self) -> dict:
        return {
            "accountEmail": self.email,
            "result": "renewed" if self.renewed else "added",
            "role": self.role,
            "configDir": self.config_dir,
            "signedInAtUtc": self.signed_in_at_utc,
            "signInExpiresAtUtc": self.sign_in_expires_at_utc,
        }


def sign_in(store: LicenseStore, email: str, approve: Approve, *,
            start_login: Optional[StartLogin] = None,
            now: Callable[[], _dt.datetime] = utc_now) -> SignInResult:
    """Sign ``email`` in on this machine: a new spare, or a renewal in place of the sign-in held (spare or active).

    ``approve(url, email)`` returns the ``code#state`` for the authorize URL - from a local browser profile, or from
    another machine. Until the code is typed in, nothing has been written: a failure before then leaves every folder
    as it was, and removes a new spare folder that never received a sign-in."""
    email = email.strip()
    try:
        target = store.sign_in_target(email)
    except LicenseStoreError as ex:
        raise SignInError(STORE_REFUSED, str(ex), ex.next_steps) from None
    login: Optional[LoginProcess] = None
    finished = False
    try:
        login = (start_login or start_claude_login)(target.folder, email)
        url = login.authorize_url(URL_WAIT_SECONDS)
        code = approve(url, email).strip()
        if not CODE_SHAPE.match(code):
            raise SignInError(APPROVAL_FAILED, "the approval returned something that is not an authorize code")
        typed_in = login
        try:
            result = store.finish_sign_in(target, lambda: typed_in.finish(code, FINISH_WAIT_SECONDS), now)
        except LicenseStoreError as ex:
            raise SignInError(STORE_REFUSED, str(ex), ex.next_steps) from None
        finished = True
    finally:
        if login is not None:
            login.cancel()
        if not finished:
            _abandon(store, target)
    log.info("sign-in %s: %s", email, "renewed" if result.renewed else "added")
    return SignInResult(email.lower(), result.account.role, result.account.config_dir, result.renewed,
                        iso(result.signed_in_at), iso(result.sign_in_ends))


def _abandon(store: LicenseStore, target) -> None:
    try:
        store.abandon_sign_in(target)
    except (LicenseStoreError, OSError) as ex:
        # The sign-in failed already and that is what is raised; this is said in the log, not swallowed.
        log.warning("new spare folder %s could not be removed (%s); remove it by hand", target.folder,
                    type(ex).__name__)


# ---------------------------------------------------------------------------------------------------
# Approving, in a browser profile on this machine
# ---------------------------------------------------------------------------------------------------


#: What browser-harness runs to approve one authorize URL. It prints exactly one line starting "RESULT ", a JSON
#: object whose "state" is code, wrong-account, not-signed-in, no-account-shown or no-code.
_APPROVE_SCRIPT = r'''
import json, re, time
URL = %(url)s
EMAIL = %(email)s
WAIT = %(wait)s
SHOWN = re.compile(r"Logged in as\s+([^\s]+@[^\s]+)", re.I)
CODE = re.compile(r"[A-Za-z0-9_\-]{20,}#[A-Za-z0-9_\-]{20,}")

def authorize_button():
    # The consent page is long: the button can sit below the visible area, where a click at its position lands on
    # nothing. Scroll it to the middle first, then read where it is, and only count it when it is on screen.
    found = js("""(()=>{const b=[...document.querySelectorAll('button')].find(b=>b.offsetParent&&b.innerText.trim()==='Authorize');
        if(!b) return null; b.scrollIntoView({block:'center'}); const r=b.getBoundingClientRect();
        return [r.x+r.width/2, r.y+r.height/2, innerWidth, innerHeight];})()""")
    if not found:
        return None
    x, y, width, height = found
    return (x, y) if 0 <= x <= width and 0 <= y <= height else None

def finish(state, **extra):
    print("RESULT " + json.dumps(dict(state=state, **extra)))

new_tab(URL)
tab = current_tab()
# The tab is driven in the background, never brought to the front. A page can ignore input while it believes it
# has no focus (a click was lost this way live), so focus is emulated for the approval and switched off after.
cdp("Emulation.setFocusEmulationEnabled", enabled=True)
try:
    wait_for_load()
    deadline = time.time() + WAIT
    text, authorize = "", None
    # Judge only a settled page: the Authorize button can render before the "Logged in as" line (seen live).
    while time.time() < deadline:
        authorize = authorize_button()
        text = js("document.body ? document.body.innerText : ''") or ""
        if authorize and SHOWN.search(text):
            break
        time.sleep(2)
    shown = SHOWN.search(text)
    if not authorize:
        finish("not-signed-in", url=page_info().get("url", ""))
    elif not shown:
        finish("no-account-shown")
    elif shown.group(1).strip().rstrip(".").lower() != EMAIL.lower():
        finish("wrong-account", shown=shown.group(1).strip().rstrip("."))
    else:
        # A click made while the page is still settling can be lost (seen live: the consent page stayed up). So
        # the button is read again just before the click, and clicked again while it is still showing.
        time.sleep(1)
        authorize = authorize_button() or authorize
        click_at_xy(authorize[0], authorize[1])
        clicks, last_click = 1, time.time()
        code = None
        deadline = time.time() + WAIT
        while time.time() < deadline and not code:
            time.sleep(1.5)
            again = authorize_button()
            if again and clicks < 3 and time.time() - last_click > 6:
                click_at_xy(again[0], again[1])
                clicks, last_click = clicks + 1, time.time()
            values = js("[...document.querySelectorAll('input,textarea,code,pre')].map(e=>e.value||e.innerText||'')") or []
            for s in values + [js("document.body ? document.body.innerText : ''") or ""]:
                m = CODE.search(s)
                if m:
                    code = m.group(0)
                    break
        if code:
            finish("code", code=code)
        else:
            page = js("document.body ? document.body.innerText : ''") or ""
            state = js("[document.visibilityState, document.hasFocus()]") or ["?", "?"]
            finish("no-code", clicks=clicks, url=page_info().get("url", "").split("?")[0],
                   text=" ".join(page.split())[:300], visibility=state[0], focused=state[1])
finally:
    cdp("Emulation.setFocusEmulationEnabled", enabled=False)
    cdp("Target.closeTarget", targetId=tab["targetId"])
'''


class BrowserProfile(Protocol):
    """A Director browser profile, started for driving. ``stop`` puts it back as it was found."""

    def environment(self) -> Dict[str, str]:
        """BU_NAME and BU_CDP_URL for browser-harness."""
        ...

    def stop(self) -> None: ...


def director_browser(profile: str) -> BrowserProfile:
    """The Director's own browser profile ``profile`` (name or id), started if it was stopped."""
    from cc_shared import gateway

    from . import browser_ops

    try:
        return browser_ops.DrivenBrowser(profile)
    except LookupError as ex:
        raise SignInError(APPROVAL_FAILED, str(ex), ["cc-devthrottle browser list"]) from None
    except gateway.GatewayError as ex:
        raise SignInError(APPROVAL_FAILED, f"the browser profile could not be started: {ex}",
                          ["cc-devthrottle session whoami", "cc-devthrottle browser list"]) from None


RunHarness = Callable[[str, Dict[str, str], float], str]


def _run_harness(script: str, env: Dict[str, str], timeout_seconds: float) -> str:
    exe = shutil.which("browser-harness")
    if not exe:
        raise SignInError(APPROVAL_FAILED, "browser-harness is not on this machine's PATH.",
                          ["Install browser-harness, then run: browser-harness --doctor"])
    full_env = dict(os.environ, **env)
    try:
        done = subprocess.run([exe], input=script, capture_output=True, text=True, encoding="utf-8",
                              errors="replace", env=full_env, timeout=timeout_seconds)
    except subprocess.TimeoutExpired:
        raise SignInError(APPROVAL_FAILED, f"the browser step did not finish in {timeout_seconds:g} seconds") from None
    finally:
        # End this profile's browser-harness daemon. The profile is stopped next when it was stopped before, and a
        # daemon left attached to a stopped browser collides with the next one started for it (seen live: the
        # next approval failed with "Access is denied" on the daemon's port file).
        stopped = subprocess.run([exe, "--reload"], capture_output=True, text=True, encoding="utf-8",
                                 errors="replace", env=full_env, timeout=60)
        if stopped.returncode != 0:
            log.warning("browser-harness --reload exited %d for %s: %s", stopped.returncode, env.get("BU_NAME"),
                        " / ".join((stopped.stderr or stopped.stdout).strip().splitlines()[-2:]))
    if done.returncode != 0:
        # browser-harness's own error text names the page or the connection; the code is never in it, because a
        # run that found the code printed it only on the RESULT line and exited 0.
        detail = " / ".join(line for line in (done.stderr or done.stdout).splitlines()[-3:] if line.strip())
        raise SignInError(APPROVAL_FAILED, f"browser-harness failed (exit {done.returncode}): {detail}",
                          ["browser-harness --doctor"])
    return done.stdout


def approve_in_browser(url: str, email: str, profile: str, *,
                       open_profile: Callable[[str], BrowserProfile] = director_browser,
                       run_harness: RunHarness = _run_harness) -> str:
    """Approve one authorize URL in the browser profile ``profile``, which must be signed in to claude.ai as
    ``email``. Returns the ``code#state``. Refuses - clicking nothing - when the page names another account, names
    no account, or is not signed in.

    It refuses before any browser opens when the URL itself is for another account. ``claude auth login --email X``
    puts X in the URL as ``login_hint``; the consent page shows only who the BROWSER is signed in as. Without this
    check, an approval run by hand with the wrong email and that email's profile would agree with itself, approve,
    and hand the waiting login a code for another account - which on a renewal replaces the held sign-in before
    anything can read it back. A URL with no ``login_hint`` names no account and is refused the same way."""
    if not AUTHORIZE_URL.fullmatch(url.strip()):
        raise SignInError(APPROVAL_FAILED, "that is not a Claude authorize URL (https://.../oauth/authorize?...)")
    hints = urllib.parse.parse_qs(urllib.parse.urlsplit(url.strip()).query).get("login_hint", [])
    if len(hints) != 1 or not hints[0].strip():
        raise SignInError(WRONG_ACCOUNT, "the authorize URL does not say which account it signs in (no login_hint); "
                                         "nothing was approved.",
                          ["Start the sign-in again with: cc-devthrottle license sign-in <email> --remote-approval"])
    if hints[0].strip().lower() != email.strip().lower():
        raise SignInError(
            WRONG_ACCOUNT,
            f"the authorize URL signs in {hints[0].strip()}, not {email}; nothing was approved.",
            [f"cc-devthrottle license approve --email {hints[0].strip()} --browser <its profile> <url>"])
    browser = open_profile(profile)
    try:
        script = _APPROVE_SCRIPT % {"url": json.dumps(url.strip()), "email": json.dumps(email),
                                    "wait": BROWSER_WAIT_SECONDS // 2}
        out = run_harness(script, browser.environment(), BROWSER_WAIT_SECONDS + 30)
    finally:
        browser.stop()
    lines = [line for line in out.splitlines() if line.startswith("RESULT ")]
    if len(lines) != 1:
        raise SignInError(APPROVAL_FAILED, "the browser step ended without a result")
    try:
        answer = json.loads(lines[0][len("RESULT "):])
    except ValueError:
        raise SignInError(APPROVAL_FAILED, "the browser step's result is not JSON") from None
    state = answer.get("state")
    if state == "code" and isinstance(answer.get("code"), str) and CODE_SHAPE.match(answer["code"]):
        log.info("approve %s: approved in profile %s", email, profile)
        return answer["code"]
    log.warning("approve %s: refused in profile %s (%s)", email, profile, state)
    if state == "wrong-account":
        raise SignInError(
            WRONG_ACCOUNT,
            f"the browser profile \"{profile}\" is signed in to claude.ai as {answer.get('shown')}, not {email}; "
            "nothing was approved.",
            ["cc-devthrottle browser list --json", f"cc-devthrottle license set-browser {email} <profile>"])
    if state == "not-signed-in":
        raise SignInError(
            NOT_SIGNED_IN,
            f"the browser profile \"{profile}\" showed no Authorize button: it is not signed in to claude.ai. "
            "Signing a profile in is done by hand, once.",
            [f"cc-devthrottle browser signin \"{profile}\""])
    if state == "no-account-shown":
        raise SignInError(APPROVAL_FAILED, "the consent page did not say which account is signed in, so nothing "
                                           "was approved")
    raise SignInError(APPROVAL_FAILED, f"Authorize was clicked {answer.get('clicks', '?')} time(s) but the page "
                                       f"showed no code (page visibility {answer.get('visibility', '?')}, focused "
                                       f"{answer.get('focused', '?')}; it is at "
                                       f"{answer.get('url') or 'an unknown address'} and says: "
                                       f"{answer.get('text') or 'nothing'})")


def browser_approver(profile: str, **kwargs) -> Approve:
    """An ``approve`` for ``sign_in`` that approves in a browser profile on this machine."""
    return lambda url, email: approve_in_browser(url, email, profile, **kwargs)


# ---------------------------------------------------------------------------------------------------
# Which browser profile signs which account in - the owner's configuration
# ---------------------------------------------------------------------------------------------------


def _browsers_path(store: LicenseStore) -> str:
    return os.path.join(store.store_dir, BROWSERS_FILE)


def load_browsers(store: LicenseStore) -> Dict[str, str]:
    """The owner's map of account email (lower case) to browser profile name, from the store's folder."""
    path = _browsers_path(store)
    if not os.path.isfile(path):
        return {}
    try:
        data = json.loads(open(path, encoding="utf-8").read())
    except ValueError:
        raise SignInError(NO_APPROVER, f"{path} is not valid JSON", [f"Open {path} and repair it"]) from None
    accounts = data.get("accounts") if isinstance(data, dict) else None
    if not isinstance(accounts, dict):
        raise SignInError(NO_APPROVER, f"{path} has no \"accounts\" object", [f"Open {path} and repair it"])
    return {str(k).strip().lower(): str(v) for k, v in accounts.items() if isinstance(v, str) and v.strip()}


def set_browser(store: LicenseStore, email: str, profile: str) -> str:
    """Record that ``profile`` signs ``email`` in. Returns the file written."""
    current = load_browsers(store)
    current[email.strip().lower()] = profile.strip()
    path = _browsers_path(store)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    temp = f"{path}.{os.getpid()}.tmp"
    with open(temp, "w", encoding="utf-8") as handle:
        handle.write(json.dumps({"accounts": dict(sorted(current.items()))}, indent=2))
    os.replace(temp, path)
    return path


def browser_for(store: LicenseStore, email: str) -> str:
    profile = load_browsers(store).get(email.strip().lower())
    if not profile:
        raise SignInError(
            NO_APPROVER,
            f"no browser profile is configured to sign {email} in.",
            [f"cc-devthrottle license set-browser {email} <profile>", "cc-devthrottle browser list"])
    return profile


# ---------------------------------------------------------------------------------------------------
# Renewing spares before their sign-in dies
# ---------------------------------------------------------------------------------------------------


@dataclass
class RenewalRow:
    account: str
    role: str
    sign_in_expires_at_utc: Optional[str]
    result: str = ""     # renewed | not-due | unknown-expiry | failed-<kind>
    detail: Optional[str] = None
    browser: Optional[str] = None
    signed_in_at_utc: Optional[str] = None

    @property
    def failed(self) -> bool:
        return self.result.startswith("failed-")

    def to_json(self) -> dict:
        return {
            "accountEmail": self.account,
            "role": self.role,
            "result": self.result,
            "signInExpiresAtUtc": self.sign_in_expires_at_utc,
            "signedInAtUtc": self.signed_in_at_utc,
            "browser": self.browser,
            "detail": self.detail,
        }


def renew_due(store: LicenseStore, *, account: Optional[str] = None, force: bool = False, check: bool = False,
              approver_for: Optional[Callable[[str], Approve]] = None,
              start_login: Optional[StartLogin] = None,
              now: Callable[[], _dt.datetime] = utc_now) -> List[RenewalRow]:
    """Sign in again every account held - spares AND the active one - whose sign-in ends within RENEW_AHEAD (or
    ``account`` alone; ``force`` renews whether due or not). One row per account held, so the answer says what was
    NOT renewed and why too:

    - ``not-due``: the sign-in has more than RENEW_AHEAD left;
    - ``unknown-expiry``: the key carries no end date, so it is not known when it dies (reported, not guessed);
    - ``failed-<kind>``: the renewal was tried and did not work, with the reason.

    With ``check`` nothing is signed in: an account whose renewal would be tried is answered ``due`` instead, with
    the browser profile set for it here (or none). The Director asks this once a day and then signs each due
    account in itself, so that one with no profile on this machine can be approved on another."""
    try:
        held = store.held_accounts()
    except LicenseStoreError as ex:
        raise SignInError(STORE_REFUSED, str(ex), ex.next_steps) from None
    if account is not None:
        held = [h for h in held if (h.email or "").lower() == account.strip().lower()]
        if not held:
            raise SignInError(STORE_REFUSED, f"this machine holds no account {account!r}",
                              ["cc-devthrottle license list", f"cc-devthrottle license sign-in {account} --browser <profile>"])
    rows: List[RenewalRow] = []
    for h in held:
        email = (h.email or "").lower()
        try:
            ends = sign_in_ends(h.read_key())
        except LicenseStoreError as ex:
            rows.append(RenewalRow(email, h.role, None, f"failed-{STORE_REFUSED}", str(ex)))
            continue
        row = RenewalRow(email, h.role, iso(ends))
        rows.append(row)
        due = ends is not None and ends - now() <= RENEW_AHEAD
        if not force and ends is None:
            row.result = "unknown-expiry"
            continue
        if not force and not due:
            row.result = "not-due"
            continue
        if check:
            row.result, row.browser = "due", load_browsers(store).get(email)
            continue
        try:
            row.browser = browser_for(store, email)
            result = sign_in(store, email, (approver_for or browser_approver)(row.browser), start_login=start_login,
                             now=now)
        except SignInError as ex:
            row.result, row.detail = f"failed-{ex.kind}", str(ex)
            log.warning("renew %s: FAILED (%s) %s", email, ex.kind, ex)
            continue
        row.result = "renewed"
        row.sign_in_expires_at_utc = result.sign_in_expires_at_utc
        row.signed_in_at_utc = result.signed_in_at_utc
        log.info("renew %s: renewed, sign-in now ends %s", email, result.sign_in_expires_at_utc)
    return rows
