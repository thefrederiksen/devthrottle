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

THE APPROVER CHECK. A profile that looks signed in can still be unable to approve: claude.ai can ask for a fresh
sign-in on the approval page itself while its chat works. So ``check_approver`` opens a REAL approval page - from a
throwaway login into a temporary folder, cancelled and deleted after - and reads it with the same ``classify_page``
the approval uses, pressing nothing. The Director runs it daily for every approver it holds.

NOTHING SECRET IS SHOWN. The new key never passes through this module: Claude Code writes it into the folder it
belongs in, and nothing moves it. The authorize code passes through, is checked for shape, and is typed into the login; it is
never logged, and no error message carries it.
"""

from __future__ import annotations

import datetime as _dt
import inspect
import json
import logging
import os
import re
import shutil
import subprocess
import tempfile
import threading
import time
import urllib.parse
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional, Protocol

from .license_store import (
    ENV_ALLOW_REAL_DEFAULT,
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
NEEDS_FRESH_SIGN_IN = "needs-fresh-sign-in"  # signed in, but the approval page asks for a recent sign-in ("reauth")
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
        # The allowance is for the store this tool opened, never for anything it starts.
        env.pop(ENV_ALLOW_REAL_DEFAULT, None)
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


# What a settled approval page says. The approval and the daily approver check read a page with the same function, so
# a page the check calls able to approve is a page the approval would approve on, and no other.
CAN_APPROVE = "can-approve"                 # the Authorize button is on screen and the page names the right account
PAGE_WRONG_ACCOUNT = "wrong-account"        # the page names another account
SIGNED_OUT = "signed-out"                   # the page is claude.ai's sign-in page: no session at all
PAGE_NEEDS_FRESH_SIGN_IN = "needs-fresh-sign-in"  # a session exists, but the page asks for a recent sign-in first
CHECK_FAILED = "check-failed"               # the page is none of those, or the check could not run
APPROVER_STATES = (CAN_APPROVE, PAGE_WRONG_ACCOUNT, SIGNED_OUT, PAGE_NEEDS_FRESH_SIGN_IN, CHECK_FAILED)


def classify_page(text: str, address: str, authorize_on_screen: bool, email: str):
    """What a settled approval page says for ``email``: ``(state, shown)``, where ``state`` is one of
    ``APPROVER_STATES`` and ``shown`` the account the page says is signed in, or None.

    The page shapes are the ones recorded live (test_license_signin.py holds copies with made-up names):

    - the consent page: an Authorize button and "Logged in as <account>";
    - the sign-in page: claude.ai/login, with "Continue with email" and no account;
    - the "reauth" page, in two forms: the authorize page itself saying "Sign in again to continue ... granting this
      access requires a recent sign-in" above "Logged in as <account>", with no Authorize button; or claude.ai/login
      with ``reauth=1`` and the account as ``email``. The browser IS signed in (chat works) and still cannot approve.

    The browser step runs this same function inside browser-harness (``_classifier_source``), so it imports what it
    uses itself."""
    import re as _re
    import urllib.parse as _parse

    parts = _parse.urlsplit(address or "")
    query = _parse.parse_qs(parts.query)
    login_page = parts.path.rstrip("/") == "/login"
    reauth_login = login_page and query.get("reauth") == ["1"]
    named = _re.search(r"Logged in as\s+([^\s]+@[^\s]+)", text or "", _re.I)
    shown = named.group(1).strip().rstrip(".") if named else None
    if shown is None and reauth_login:
        shown = (query.get("email") or [""])[0].strip() or None
    asks_again = reauth_login or bool(_re.search(r"requires a recent sign-in|Sign in again to continue", text or "",
                                                 _re.I))
    if shown and shown.lower() != email.strip().lower():
        return "wrong-account", shown
    if asks_again:
        return "needs-fresh-sign-in", shown
    if authorize_on_screen and shown:
        return "can-approve", shown
    if login_page:
        return "signed-out", None
    return "check-failed", shown


def _classifier_source() -> str:
    """``classify_page`` as source, for the browser step: one function, never a copy that can drift."""
    return inspect.getsource(classify_page)


#: The part of both browser steps that opens the URL and waits for a page that can be judged. Formatted with url,
#: email and wait, after ``classify_page`` is defined.
_SETTLE_SCRIPT = r'''
import json, re, time
URL = %(url)s
EMAIL = %(email)s
WAIT = %(wait)s

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

def settle():
    # Judge only a settled page: the Authorize button can render before the "Logged in as" line (seen live), and a
    # page can pass through another on its way. An approvable page is taken at once; any other answer only when two
    # reads in a row agree, and an unreadable page only when the time is up.
    deadline = time.time() + WAIT
    previous = None
    while True:
        authorize = authorize_button()
        text = js("document.body ? document.body.innerText : ''") or ""
        address = page_info().get("url", "")
        state, shown = classify_page(text, address, authorize is not None, EMAIL)
        if state == "can-approve" or (state != "check-failed" and state == previous) or time.time() >= deadline:
            return state, shown, authorize, address.split("?")[0]
        previous = state
        time.sleep(2)

new_tab(URL)
tab = current_tab()
# The tab is driven in the background, never brought to the front. A page can ignore input while it believes it
# has no focus (a click was lost this way live), so focus is emulated while the page is read and switched off after.
cdp("Emulation.setFocusEmulationEnabled", enabled=True)
'''

#: What browser-harness runs to approve one authorize URL. It prints exactly one line starting "RESULT ", a JSON
#: object whose "state" is code, no-code, or one of the page states other than can-approve. It clicks Authorize only
#: on a page ``classify_page`` calls can-approve.
_APPROVE_SCRIPT = _SETTLE_SCRIPT + r'''
try:
    wait_for_load()
    state, shown, authorize, address = settle()
    if state != "can-approve":
        finish(state, shown=shown, url=address)
    else:
        CODE = re.compile(r"[A-Za-z0-9_\-]{20,}#[A-Za-z0-9_\-]{20,}")
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

#: What browser-harness runs for the daily approver check: the same page, read the same way, and NOTHING pressed -
#: no click, no typing, no sign-in. It prints exactly one "RESULT " line whose "state" is one of the page states.
_CHECK_SCRIPT = _SETTLE_SCRIPT + r'''
try:
    wait_for_load()
    state, shown, authorize, address = settle()
    finish(state, shown=shown)
finally:
    cdp("Emulation.setFocusEmulationEnabled", enabled=False)
    cdp("Target.closeTarget", targetId=tab["targetId"])
'''

#: Which account a browser profile is signed in to claude.ai as: claude.ai's own account endpoint, read from a
#: claude.ai tab so the profile's session is what answers. Prints one "RESULT " line: state "account" with the
#: email, "signed-out" (refused as unauthorised), or "unreadable" with the status.
_ACCOUNT_SCRIPT = r'''
import json
def finish(state, **extra):
    print("RESULT " + json.dumps(dict(state=state, **extra)))
new_tab("https://claude.ai/")
tab = current_tab()
try:
    wait_for_load()
    answer = js("""fetch('/api/account', {credentials: 'include'}).then(async r => {
        let email = null;
        if (r.status === 200) { try { email = (await r.json()).email_address || null; } catch (e) {} }
        return {status: r.status, email: email};
    })""") or {}
    status = answer.get("status")
    if status in (401, 403):
        finish("signed-out")
    elif status == 200 and answer.get("email"):
        finish("account", email=answer["email"])
    else:
        finish("unreadable", status=status)
finally:
    cdp("Target.closeTarget", targetId=tab["targetId"])
'''


class BrowserProfile(Protocol):
    """A Director browser profile, started for driving. ``stop`` puts it back as it was found. ``id`` is its
    identifier (the Director's slug, stable across a rename), ``name`` its display name."""

    id: str
    name: str

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
    full_env.pop(ENV_ALLOW_REAL_DEFAULT, None)
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
        out = run_harness(_page_script(_APPROVE_SCRIPT, url, email), browser.environment(), BROWSER_WAIT_SECONDS + 30)
    finally:
        browser.stop()
    answer = _result(out)
    state = answer.get("state")
    if state == "code" and isinstance(answer.get("code"), str) and CODE_SHAPE.match(answer["code"]):
        log.info("approve %s: approved in profile %s", email, profile)
        return answer["code"]
    log.warning("approve %s: refused in profile %s (%s)", email, profile, state)
    if state == PAGE_WRONG_ACCOUNT:
        raise SignInError(
            WRONG_ACCOUNT,
            f"the browser profile \"{profile}\" is signed in to claude.ai as {answer.get('shown')}, not {email}; "
            "nothing was approved.",
            ["cc-devthrottle browser list --json", f"cc-devthrottle license set-browser {email} <profile>"])
    if state == SIGNED_OUT:
        raise SignInError(
            NOT_SIGNED_IN,
            f"the browser profile \"{profile}\" showed claude.ai's sign-in page: it is not signed in to claude.ai. "
            "Signing a profile in is done by hand, once.",
            [f"cc-devthrottle browser signin \"{profile}\""])
    if state == PAGE_NEEDS_FRESH_SIGN_IN:
        raise SignInError(
            NEEDS_FRESH_SIGN_IN,
            f"the browser profile \"{profile}\" is signed in to claude.ai, but the approval page asks for a fresh "
            "sign-in first (\"Sign in again to continue\"); nothing was approved. Signing a profile in again is done "
            "by hand, once.",
            [f"cc-devthrottle browser signin \"{profile}\""])
    if state == CHECK_FAILED:
        raise SignInError(APPROVAL_FAILED, "the approval page showed neither an Authorize button for the account nor "
                                           "a sign-in page, so nothing was approved")
    raise SignInError(APPROVAL_FAILED, f"Authorize was clicked {answer.get('clicks', '?')} time(s) but the page "
                                       f"showed no code (page visibility {answer.get('visibility', '?')}, focused "
                                       f"{answer.get('focused', '?')}; it is at "
                                       f"{answer.get('url') or 'an unknown address'} and says: "
                                       f"{answer.get('text') or 'nothing'})")


def _page_script(body: str, url: str, email: str) -> str:
    """A browser step for one authorize URL: the shared page classifier, then ``body`` formatted for the URL."""
    return _classifier_source() + body % {"url": json.dumps(url.strip()), "email": json.dumps(email),
                                          "wait": BROWSER_WAIT_SECONDS // 2}


def _result(out: str) -> dict:
    """The one RESULT line a browser step prints, as JSON."""
    lines = [line for line in out.splitlines() if line.startswith("RESULT ")]
    if len(lines) != 1:
        raise SignInError(APPROVAL_FAILED, "the browser step ended without a result")
    try:
        answer = json.loads(lines[0][len("RESULT "):])
    except ValueError:
        raise SignInError(APPROVAL_FAILED, "the browser step's result is not JSON") from None
    if not isinstance(answer, dict):
        raise SignInError(APPROVAL_FAILED, "the browser step's result is not a JSON object")
    return answer


def browser_approver(profile: str, **kwargs) -> Approve:
    """An ``approve`` for ``sign_in`` that approves in a browser profile on this machine."""
    return lambda url, email: approve_in_browser(url, email, profile, **kwargs)


# ---------------------------------------------------------------------------------------------------
# The daily approver check: can this browser profile approve a sign-in right now?
# ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class ApproverCheck:
    """What one look at a real approval page said. ``detail`` is this module's own sentence, never page text.

    ``browser_profile`` is the profile's IDENTIFIER (the Director's slug), never its display name: approver records
    are matched on it exactly. ``browser_profile_name`` is the display name, for sentences; None when the profile
    could not be found, and then ``browser_profile`` is what the caller gave."""

    browser_profile: str
    browser_profile_name: Optional[str]
    account_email: Optional[str]
    shown_account: Optional[str]
    state: str
    detail: str

    def to_json(self) -> dict:
        return {
            "browserProfile": self.browser_profile,
            "browserProfileName": self.browser_profile_name,
            "accountEmail": self.account_email,
            "shownAccount": self.shown_account,
            "state": self.state,
            "detail": self.detail,
        }


def _detail(state: str, profile: str, email: Optional[str], shown: Optional[str]) -> str:
    who = f"The browser profile \"{profile}\""
    if state == CAN_APPROVE:
        return f"{who} can approve sign-ins for {email}."
    if state == PAGE_WRONG_ACCOUNT:
        return f"{who} is signed in to claude.ai as {shown}, not {email}."
    if state == SIGNED_OUT:
        return f"{who} is not signed in to claude.ai."
    if state == PAGE_NEEDS_FRESH_SIGN_IN:
        return f"{who} is signed in to claude.ai, but the approval page for {email} asks for a fresh sign-in."
    return f"{who} showed an approval page for {email} that was neither the consent page nor a sign-in page."


def read_signed_in_account(env: Dict[str, str], run_harness: RunHarness = _run_harness) -> Optional[str]:
    """The account the browser profile behind ``env`` is signed in to claude.ai as, or None when it is signed out.
    Raises SignInError when claude.ai's answer is neither."""
    answer = _result(run_harness(_ACCOUNT_SCRIPT, env, BROWSER_WAIT_SECONDS))
    if answer.get("state") == "signed-out":
        return None
    if answer.get("state") == "account" and isinstance(answer.get("email"), str) and "@" in answer["email"]:
        return answer["email"].strip().lower()
    raise SignInError(APPROVAL_FAILED, f"claude.ai's account endpoint answered status {answer.get('status')}, which "
                                       "says neither who is signed in nor that nobody is")


def _temporary_login_folder() -> str:
    """An empty folder of its own for the throwaway login: never a store folder, never the real default login."""
    folder = tempfile.mkdtemp(prefix="cc-license-check-")
    if is_real_default(folder):
        raise SignInError(LOGIN_FAILED, f"the temporary folder {folder} is the machine's real Claude login folder")
    return folder


def check_approver(profile: str, email: Optional[str] = None, *,
                   open_profile: Callable[[str], BrowserProfile] = director_browser,
                   run_harness: RunHarness = _run_harness,
                   start_login: Optional[StartLogin] = None,
                   make_folder: Callable[[], str] = _temporary_login_folder) -> ApproverCheck:
    """Could ``profile`` approve a sign-in for ``email`` now? Answered from a REAL approval page, because claude.ai
    can demand a fresh sign-in there while its chat works (the 16 October report, problem 12).

    With no ``email``, the account the profile is signed in to claude.ai as is read first; a signed-out profile is
    answered ``signed-out`` with no account. The page comes from a throwaway ``claude auth login`` into a new
    temporary folder, which is cancelled and deleted on every path. Nothing on the page is pressed: no Authorize, no
    typing, no sign-in - the code that would come back is never asked for, so this can never sign anything in.

    Every failure of the check itself is answered ``check-failed`` with why, never raised: the Director reports it."""
    browser: Optional[BrowserProfile] = None
    login: Optional[LoginProcess] = None
    folder: Optional[str] = None
    # Until the profile is found, the answer can only echo what the caller gave; after, it names the profile by its
    # identifier, whichever of identifier or name the caller used.
    profile_id, profile_name = profile, None
    try:
        browser = open_profile(profile)
        profile_id, profile_name = browser.id, browser.name
        env = browser.environment()
        if email is None:
            email = read_signed_in_account(env, run_harness)
            if email is None:
                log.info("check-approver %s: signed out of claude.ai", profile)
                return ApproverCheck(profile_id, profile_name, None, None, SIGNED_OUT,
                                     _detail(SIGNED_OUT, profile_name, None, None))
        email = email.strip().lower()
        folder = make_folder()
        login = (start_login or start_claude_login)(folder, email)
        url = login.authorize_url(URL_WAIT_SECONDS)
        if not AUTHORIZE_URL.fullmatch(url.strip()):
            raise SignInError(LOGIN_FAILED, "claude auth login printed something that is not an authorize URL")
        answer = _result(run_harness(_page_script(_CHECK_SCRIPT, url, email), env, BROWSER_WAIT_SECONDS + 30))
        state = answer.get("state")
        if state not in APPROVER_STATES:
            raise SignInError(APPROVAL_FAILED, f"the browser step answered a state this tool does not know ({state!r})")
        shown = answer.get("shown") if isinstance(answer.get("shown"), str) else None
        log.info("check-approver %s for %s: %s", profile, email, state)
        return ApproverCheck(profile_id, profile_name, email, shown, state, _detail(state, profile_name, email, shown))
    except SignInError as ex:
        log.warning("check-approver %s for %s: the check could not run (%s) %s", profile, email, ex.kind, ex)
        return ApproverCheck(profile_id, profile_name, email, None, CHECK_FAILED, f"The check could not run: {ex}")
    finally:
        if login is not None:
            login.cancel()
        # The login is ended first, so nothing still holds a file in the folder. A folder that cannot be removed is
        # raised, never left behind quietly.
        if folder is not None and os.path.isdir(folder):
            shutil.rmtree(folder)
        if browser is not None:
            browser.stop()


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
