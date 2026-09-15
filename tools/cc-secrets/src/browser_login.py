"""Filling a login form in a Director-owned browser over its debug port.

The secret goes from this process straight to the page, over the browser's loopback debug connection.
It never passes through the agent, and nothing this module logs carries a debug-protocol parameter:
the tool log records method names, message ids and outcomes only.

Where the password may go is checked against the entry's allowed ORIGINS (scheme, host and port,
exactly), twice, before anything is typed:

1. the tab's address, read from the browser (Page.getFrameTree), which page scripts cannot alter;
2. where the form will send it: the submit button's formaction or the form's action.

Both the form lookup and the fill run in an ISOLATED script world created for this login, which shares
the page's document but not its JavaScript objects. A script the page - or an agent driving the same
tab - put in place cannot change what `form.action` or the value setter mean to this code. The fields
are handled through object handles the browser destroys if the tab navigates, and the submit re-checks
that the form still sends to the checked address, so a change made in between stops the submit.

Once a password has been typed, whatever the outcome - logged in, refused, failed, or a crash - every
password field in the tab is emptied, including hidden ones, and the tab's back and forward history is
reset, so the login page cannot be brought back from the browser's back-forward cache with the password
still in it.

Not covered, stated plainly: JavaScript already running in the page receives the password, because the
site needs it - so a listener an agent attached to the page before calling login can copy it. The same
holds for a form that sends with JavaScript instead of a form action. Also not covered: login forms
inside cross-origin frames, two-step verification and captchas (reported as a verification stop for the
owner to finish by hand), and a hostile process running as this same user that binds the profile's
debug port in place of the real browser.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Dict, List, Optional

from . import filelog
from .errors import CcSecretsError
from .redact import SCRUBBER
from .store import Entry, origin_allowed, origin_of

OUTCOME_LOGGED_IN = "logged in"
OUTCOME_FAILED = "failed"
OUTCOME_VERIFICATION = "verification"
OUTCOME_REFUSED = "refused"

POLL_SECONDS = 0.5
SETTLED_POLLS = 3
WORLD_NAME = "cc-secrets"

_VISIBLE = """
const visible = e => {
  if (e.disabled || e.readOnly) return false;
  const s = getComputedStyle(e);
  if (s.visibility === 'hidden' || s.display === 'none') return false;
  return e.getClientRects().length > 0;
};
"""

_FIND = "(function(which) {" + _VISIBLE + """
  const password = Array.from(document.querySelectorAll('input[type=password]')).find(visible) || null;
  if (which === 'password') return password;
  const scope = (password && password.form) || document;
  const textual = Array.from(scope.querySelectorAll('input'))
    .filter(e => ['text', 'email', 'tel'].includes(e.type) && visible(e));
  if (!textual.length) return null;
  const byAutocomplete = textual.find(e => /username|email/i.test(e.autocomplete || ''));
  if (byAutocomplete) return byAutocomplete;
  const byName = textual.find(e => /user|email|login|account/i.test((e.name || '') + ' ' + (e.id || '')));
  if (byName) return byName;
  if (password) {
    const before = textual.filter(e => e.compareDocumentPosition(password) & Node.DOCUMENT_POSITION_FOLLOWING);
    if (before.length) return before[before.length - 1];
  }
  return textual.length === 1 ? textual[0] : null;
})"""

_STATE = "(() => {" + _VISIBLE + """
  const find = """ + _FIND + """;
  const password = !!find('password');
  const username = !!find('username');
  const otp = !!document.querySelector('input[autocomplete="one-time-code"]');
  const captcha = Array.from(document.querySelectorAll('iframe'))
    .some(f => /captcha|turnstile|challenges\\.cloudflare/i.test(f.src || ''));
  const text = ((document.body && document.body.innerText) || '').slice(0, 20000);
  const words = /(verification code|two-step|2-step|two-factor|2fa|authenticator|security code|one-time (pass)?code|verify it'?s you|captcha)/i.test(text);
  return {password, username, verification: otp || captcha || (!password && !username && words)};
})()"""

# Where the form holding `this` will send: the submit button's formaction, else the form's action. An
# empty string means there is no form and the page would send with its own JavaScript.
_TARGET = """
  const form = this.form;
  const button = form ? form.querySelector('button[type=submit], input[type=submit], button:not([type])') : null;
  const submitter = (button && button.form === form) ? button : null;
  const target = !form ? '' : ((submitter && submitter.hasAttribute('formaction')) ? submitter.formAction : form.action);
"""

_SUBMIT_TARGET = "function() {" + _TARGET + "  return target;\n}"

_SET_VALUE = """function(value) {
  this.focus();
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
  setter.call(this, value);
  this.dispatchEvent(new Event('input', {bubbles: true}));
  this.dispatchEvent(new Event('change', {bubbles: true}));
  return this.value.length === value.length;
}"""

_SUBMIT = "function(expected) {" + _TARGET + """
  if (target !== expected) return 'changed';
  if (!form) { this.focus(); return 'enter'; }
  if (typeof form.requestSubmit === 'function') {
    if (submitter) form.requestSubmit(submitter); else form.requestSubmit();
  } else if (submitter) { submitter.click(); } else { form.submit(); }
  return 'form';
}"""

_CLEAR_PASSWORDS = """(() => {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
  let cleared = 0;
  document.querySelectorAll('input[type=password]').forEach(e => {
    if (e.value) { setter.call(e, ''); e.dispatchEvent(new Event('input', {bubbles: true})); cleared++; }
  });
  return cleared;
})()"""


@dataclass
class LoginResult:
    outcome: str
    reason: str
    host: str = ""


class CdpError(CcSecretsError, RuntimeError):
    """A debug-protocol call failed. The message never includes call parameters."""


class CdpConnection:
    """One debug-protocol connection to one tab."""

    def __init__(self, websocket_url: str, timeout_seconds: float = 15) -> None:
        from websockets.sync.client import connect

        self._timeout = timeout_seconds
        self._next_id = 0
        self._ws = connect(websocket_url, open_timeout=timeout_seconds, max_size=None)
        filelog.write("[cdp] connected")

    def call(self, method: str, params: Optional[Dict] = None) -> Dict:
        self._next_id += 1
        message_id = self._next_id
        filelog.write(f"[cdp] -> {method} id={message_id}")
        self._ws.send(json.dumps({"id": message_id, "method": method, "params": params or {}}))
        deadline = time.monotonic() + self._timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise CdpError(f"{method} did not answer within {self._timeout:.0f} seconds")
            try:
                raw = self._ws.recv(timeout=remaining)
            except TimeoutError as exc:
                raise CdpError(f"{method} did not answer within {self._timeout:.0f} seconds") from exc
            reply = json.loads(raw)
            if reply.get("id") != message_id:
                continue
            if "error" in reply:
                filelog.write(f"[cdp] <- {method} id={message_id} error")
                raise CdpError(f"{method} failed: {reply['error'].get('message', 'unknown error')}")
            filelog.write(f"[cdp] <- {method} id={message_id} ok")
            return reply.get("result", {})

    def close(self) -> None:
        self._ws.close()
        filelog.write("[cdp] closed")


def list_page_targets(port: int) -> List[Dict]:
    """The browser's open tabs, from its loopback debug endpoint."""
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/list", timeout=10) as response:
            targets = json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, ConnectionError) as exc:
        raise CdpError(f"Nothing is answering on debug port {port}; the browser is not running.") from exc
    return [t for t in targets if t.get("type") == "page" and t.get("webSocketDebuggerUrl")]


class _Tab:
    """The login steps, against one connected tab, in an isolated script world."""

    def __init__(self, conn: CdpConnection, entry: Entry) -> None:
        self._conn = conn
        self._entry = entry
        self._world: Optional[tuple] = None
        self.typed = False

    def _frame(self) -> Dict:
        return self._conn.call("Page.getFrameTree")["frameTree"]["frame"]

    def main_frame_url(self) -> str:
        return self._frame().get("url", "")

    def main_frame_unreachable(self) -> bool:
        """True when the tab shows the browser's own error page (the site did not answer)."""
        frame = self._frame()
        return bool(frame.get("unreachableUrl")) or frame.get("url", "").startswith("chrome-error:")

    def _context(self) -> int:
        """The isolated world for the document now in the tab, created once per document."""
        frame = self._frame()
        key = (frame.get("id"), frame.get("loaderId"))
        if self._world is None or self._world[0] != key:
            reply = self._conn.call("Page.createIsolatedWorld", {"frameId": frame["id"], "worldName": WORLD_NAME})
            self._world = (key, reply["executionContextId"])
        return self._world[1]

    def _evaluate(self, expression: str, by_value: bool) -> Dict:
        return self._conn.call("Runtime.evaluate", {"expression": expression, "contextId": self._context(),
                                                    "returnByValue": by_value})

    def state(self) -> Optional[Dict]:
        """The form state, or None while the page is between documents."""
        try:
            reply = self._evaluate(_STATE, True)
        except CdpError:
            self._world = None
            return None
        if "exceptionDetails" in reply:
            return None
        return reply["result"].get("value")

    def find(self, which: str) -> Optional[str]:
        reply = self._evaluate(f"{_FIND}('{which}')", False)
        if "exceptionDetails" in reply:
            raise CdpError(f"Locating the {which} field raised an error in the page.")
        return reply["result"].get("objectId")

    def submit_target(self, object_id: str) -> str:
        reply = self._conn.call("Runtime.callFunctionOn", {
            "objectId": object_id, "functionDeclaration": _SUBMIT_TARGET, "returnByValue": True})
        if "exceptionDetails" in reply:
            raise CdpError("Reading where the form sends raised an error in the page.")
        return str(reply["result"].get("value") or "")

    def fill(self, object_id: str, value: str, what: str) -> None:
        reply = self._conn.call("Runtime.callFunctionOn", {
            "objectId": object_id, "functionDeclaration": _SET_VALUE,
            "arguments": [{"value": value}], "returnByValue": True})
        if "exceptionDetails" in reply or reply["result"].get("value") is not True:
            raise CdpError(f"The {what} field did not accept the value.")

    def submit(self, object_id: str, expected_target: str) -> bool:
        """Submit, unless the form no longer sends to `expected_target`. Returns False when it did not."""
        reply = self._conn.call("Runtime.callFunctionOn", {
            "objectId": object_id, "functionDeclaration": _SUBMIT,
            "arguments": [{"value": expected_target}], "returnByValue": True})
        if "exceptionDetails" in reply:
            raise CdpError("Submitting the form raised an error in the page.")
        how = reply["result"].get("value")
        if how == "changed":
            return False
        if how == "enter":
            for kind in ("rawKeyDown", "char", "keyUp"):
                params = {"type": kind, "key": "Enter", "code": "Enter", "windowsVirtualKeyCode": 13}
                if kind == "char":
                    params["text"] = "\r"
                self._conn.call("Input.dispatchKeyEvent", params)
        return True

    def clean_up(self) -> None:
        """Empty every password field in the tab and reset its history. Each step is tried on its own and
        a failure is logged by type, because this also runs while an error is on its way out."""
        try:
            reply = self._evaluate(_CLEAR_PASSWORDS, True)
            filelog.write(f"[login] cleared {reply.get('result', {}).get('value')} password field(s)")
        except Exception as exc:
            filelog.write(f"[login] clearing password fields FAILED: {type(exc).__name__}")
        try:
            self._conn.call("Page.resetNavigationHistory", {})
            filelog.write("[login] reset the tab's navigation history")
        except Exception as exc:
            filelog.write(f"[login] resetting navigation history FAILED: {type(exc).__name__}")


def _wait(tab: _Tab, deadline: float, done) -> Optional[Dict]:
    """Poll the page until `done(state)` is true; returns that state, or None at the deadline."""
    while time.monotonic() < deadline:
        time.sleep(POLL_SECONDS)
        state = tab.state()
        if state is not None and done(state):
            return state
    return None


def _drive(tab: _Tab, entry: Entry, secret: str, timeout_seconds: float) -> LoginResult:
    deadline = time.monotonic() + timeout_seconds
    allowed = entry.allowed_domains
    username_filled = False
    for _ in range(3):
        state = tab.state()
        if state is None:
            state = _wait(tab, deadline, lambda s: True)
            if state is None:
                return LoginResult(OUTCOME_FAILED, "The page did not finish loading.")
        if state["verification"] and not state["password"]:
            return LoginResult(OUTCOME_VERIFICATION, "The page is asking for a verification step. "
                               "Finish the login by hand in that browser profile.", origin_of(tab.main_frame_url()))
        if not state["password"] and not state["username"]:
            return LoginResult(OUTCOME_FAILED, "There is no login form on the page.", origin_of(tab.main_frame_url()))

        password_id = tab.find("password") if state["password"] else None
        username_id = None
        if state["username"] and not username_filled:
            if entry.username:
                username_id = tab.find("username")
            elif password_id is None:
                return LoginResult(OUTCOME_FAILED, f"The page asks for a username first and entry '{entry.name}' has none.")
        field = password_id or username_id
        if field is None:
            return LoginResult(OUTCOME_FAILED, "The page shows a login field that could not be identified.")

        # Check 1: the tab's own address, read from the browser.
        url = tab.main_frame_url()
        if not origin_allowed(url, allowed):
            return LoginResult(OUTCOME_REFUSED, f"The tab is on '{origin_of(url)}', which is not an allowed address "
                               f"for '{entry.name}' (allowed: {', '.join(allowed)}). Nothing was typed.", origin_of(url))
        # Check 2: where the form sends what is typed into it.
        target = tab.submit_target(field)
        if target and not origin_allowed(target, allowed):
            return LoginResult(OUTCOME_REFUSED, f"The form on this page sends to '{origin_of(target)}', which is not "
                               f"an allowed address for '{entry.name}' (allowed: {', '.join(allowed)}). "
                               "Nothing was typed.", origin_of(url))

        if username_id:
            tab.fill(username_id, entry.username, "username")
            username_filled = True
        if password_id:
            tab.typed = True
            tab.fill(password_id, secret, "password")
            if not tab.submit(password_id, target):
                return LoginResult(OUTCOME_FAILED, "The form's destination changed after it was checked, so it was "
                                   "not submitted.", origin_of(url))
            return _await_outcome(tab, deadline, origin_of(url))
        if not tab.submit(username_id, target):
            return LoginResult(OUTCOME_FAILED, "The form's destination changed after it was checked, so it was "
                               "not submitted.", origin_of(url))
        after = _wait(tab, deadline, lambda s: s["password"] or s["verification"])
        if after is None:
            return LoginResult(OUTCOME_FAILED, "No password field appeared after the username was submitted.", origin_of(url))
    return LoginResult(OUTCOME_FAILED, "The login did not reach a password field in three steps.")


def _await_outcome(tab: _Tab, deadline: float, origin: str) -> LoginResult:
    settled = 0
    while time.monotonic() < deadline:
        time.sleep(POLL_SECONDS)
        state = tab.state()
        if state is None:
            settled = 0
            continue
        if state["verification"]:
            return LoginResult(OUTCOME_VERIFICATION, "The site asked for a verification step after the password. "
                               "Finish the login by hand in that browser profile.", origin_of(tab.main_frame_url()))
        if state["password"]:
            settled = 0
            continue
        # A form that vanished because the browser is showing its own error page is not a login.
        if tab.main_frame_unreachable():
            return LoginResult(OUTCOME_FAILED, "The site did not answer after the form was submitted.", origin)
        settled += 1
        if settled >= SETTLED_POLLS:
            return LoginResult(OUTCOME_LOGGED_IN, "The login form was submitted and has gone.", origin_of(tab.main_frame_url()))
    return LoginResult(OUTCOME_FAILED, "The login form is still showing after submitting: the site rejected "
                       "the credentials or needs something else.", origin)


def login(entry: Entry, port: int, timeout_seconds: float = 30) -> LoginResult:
    """Fill and submit the login form of the entry's site in the browser on `port`."""
    filelog.write(f"[login] start: entry={entry.name}, port={port}")
    if not entry.allowed_domains:
        return LoginResult(OUTCOME_REFUSED, f"Entry '{entry.name}' has no allowed addresses, so it cannot be typed into any page.")
    secret = entry.secret.reveal()
    SCRUBBER.add(secret, entry.username)

    targets = list_page_targets(port)
    matching = [t for t in targets if origin_allowed(t.get("url", ""), entry.allowed_domains)]
    if not matching:
        origins = sorted({origin_of(t.get("url", "")) for t in targets})
        return LoginResult(OUTCOME_REFUSED, f"No open tab is on an allowed address for '{entry.name}' (allowed: "
                           f"{', '.join(entry.allowed_domains)}; open tabs: {', '.join(origins) or 'none'}). "
                           "Open the login page in that browser first. Nothing was typed.")

    conn = CdpConnection(matching[0]["webSocketDebuggerUrl"])
    tab = _Tab(conn, entry)
    try:
        result = _drive(tab, entry, secret, timeout_seconds)
        filelog.write(f"[login] done: entry={entry.name}, outcome={result.outcome}, origin={result.host}")
        return result
    finally:
        if tab.typed:
            tab.clean_up()
        conn.close()
