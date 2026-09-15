"""Filling a login form in a Director-owned browser over its debug port.

The secret goes from this process straight to the page, over the browser's loopback debug connection.
It never passes through the agent, and nothing this module logs carries a debug-protocol parameter:
the tool log records method names, message ids and outcomes only.

The domain check is read from the BROWSER, not from the page. The tab's address comes from
Page.getFrameTree, which page scripts cannot alter, and it is checked after the form fields are located
and before anything is typed. The fields are then filled through object handles that belong to the
checked document: if the tab navigates in between, the browser destroys those handles and the fill
fails instead of typing into the new page.

When the login does not complete, any password field still holding a value is emptied, so a later
snapshot, screenshot or script cannot read what was typed.

Not covered, stated plainly: a login form inside a cross-origin frame, two-step verification and
captchas (reported as a verification stop for the owner to finish by hand), and a hostile process
running as this same user that binds the profile's debug port in place of the real browser.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Dict, List, Optional
from urllib.parse import urlsplit

from . import filelog
from .redact import SCRUBBER
from .store import Entry, host_allowed

OUTCOME_LOGGED_IN = "logged in"
OUTCOME_FAILED = "failed"
OUTCOME_VERIFICATION = "verification"
OUTCOME_REFUSED = "refused"

POLL_SECONDS = 0.5
SETTLED_POLLS = 3

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

_SET_VALUE = """function(value) {
  this.focus();
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
  setter.call(this, value);
  this.dispatchEvent(new Event('input', {bubbles: true}));
  this.dispatchEvent(new Event('change', {bubbles: true}));
  return this.value.length === value.length;
}"""

_SUBMIT = """function() {
  const form = this.form;
  if (!form) { this.focus(); return 'enter'; }
  const button = form.querySelector('button[type=submit], input[type=submit], button:not([type])');
  if (typeof form.requestSubmit === 'function') {
    if (button && button.form === form) form.requestSubmit(button); else form.requestSubmit();
  } else if (button) { button.click(); } else { form.submit(); }
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


class CdpError(RuntimeError):
    """A debug-protocol call failed. The message never includes call parameters."""


def host_of(url: str) -> str:
    return (urlsplit(url).hostname or "").lower()


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
    """The login steps, against one connected tab."""

    def __init__(self, conn: CdpConnection, entry: Entry) -> None:
        self._conn = conn
        self._entry = entry

    def main_frame_host(self) -> str:
        tree = self._conn.call("Page.getFrameTree")
        return host_of(tree["frameTree"]["frame"]["url"])

    def main_frame_unreachable(self) -> bool:
        """True when the tab shows the browser's own error page (the site did not answer)."""
        frame = self._conn.call("Page.getFrameTree")["frameTree"]["frame"]
        return bool(frame.get("unreachableUrl")) or frame.get("url", "").startswith("chrome-error:")

    def state(self) -> Optional[Dict]:
        """The form state, or None while the page is between documents."""
        try:
            reply = self._conn.call("Runtime.evaluate", {"expression": _STATE, "returnByValue": True})
        except CdpError:
            return None
        if "exceptionDetails" in reply:
            return None
        return reply["result"].get("value")

    def find(self, which: str) -> Optional[str]:
        reply = self._conn.call("Runtime.evaluate",
                                {"expression": f"{_FIND}('{which}')", "returnByValue": False})
        if "exceptionDetails" in reply:
            raise CdpError(f"Locating the {which} field raised an error in the page.")
        return reply["result"].get("objectId")

    def fill(self, object_id: str, value: str, what: str) -> None:
        reply = self._conn.call("Runtime.callFunctionOn", {
            "objectId": object_id, "functionDeclaration": _SET_VALUE,
            "arguments": [{"value": value}], "returnByValue": True})
        if "exceptionDetails" in reply or reply["result"].get("value") is not True:
            raise CdpError(f"The {what} field did not accept the value.")

    def submit(self, object_id: str) -> None:
        reply = self._conn.call("Runtime.callFunctionOn", {
            "objectId": object_id, "functionDeclaration": _SUBMIT, "returnByValue": True})
        if "exceptionDetails" in reply:
            raise CdpError("Submitting the form raised an error in the page.")
        if reply["result"].get("value") == "enter":
            for kind in ("rawKeyDown", "char", "keyUp"):
                params = {"type": kind, "key": "Enter", "code": "Enter", "windowsVirtualKeyCode": 13}
                if kind == "char":
                    params["text"] = "\r"
                self._conn.call("Input.dispatchKeyEvent", params)

    def clear_passwords(self) -> None:
        try:
            reply = self._conn.call("Runtime.evaluate", {"expression": _CLEAR_PASSWORDS, "returnByValue": True})
            filelog.write(f"[login] cleared {reply.get('result', {}).get('value')} password field(s)")
        except CdpError as exc:
            filelog.write(f"[login] clearing password fields FAILED: {exc}")


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
    username_filled = False
    for _ in range(3):
        state = tab.state()
        if state is None:
            state = _wait(tab, deadline, lambda s: True)
            if state is None:
                return LoginResult(OUTCOME_FAILED, "The page did not finish loading.")
        if state["verification"] and not state["password"]:
            return LoginResult(OUTCOME_VERIFICATION, "The page is asking for a verification step. "
                               "Finish the login by hand in that browser profile.", tab.main_frame_host())
        if not state["password"] and not state["username"]:
            return LoginResult(OUTCOME_FAILED, "There is no login form on the page.", tab.main_frame_host())

        password_id = tab.find("password") if state["password"] else None
        username_id = None
        if state["username"] and not username_filled:
            if not entry.username and password_id is None:
                return LoginResult(OUTCOME_FAILED, f"The page asks for a username first and entry '{entry.name}' has none.")
            if entry.username:
                username_id = tab.find("username")

        # The domain check, after the fields are located and before anything is typed.
        host = tab.main_frame_host()
        if not host_allowed(host, entry.allowed_domains):
            return LoginResult(OUTCOME_REFUSED, f"The tab is on '{host}', which is not an allowed domain for "
                               f"'{entry.name}' (allowed: {', '.join(entry.allowed_domains)}). Nothing was typed.", host)

        if username_id:
            tab.fill(username_id, entry.username, "username")
            username_filled = True
        if password_id:
            tab.fill(password_id, secret, "password")
            tab.submit(password_id)
            return _await_outcome(tab, deadline, host)
        if username_id is None:
            return LoginResult(OUTCOME_FAILED, "The page shows a username field that could not be identified.", host)
        tab.submit(username_id)
        after = _wait(tab, deadline, lambda s: s["password"] or s["verification"])
        if after is None:
            return LoginResult(OUTCOME_FAILED, "No password field appeared after the username was submitted.", host)
    return LoginResult(OUTCOME_FAILED, "The login did not reach a password field in three steps.")


def _await_outcome(tab: _Tab, deadline: float, host: str) -> LoginResult:
    settled = 0
    while time.monotonic() < deadline:
        time.sleep(POLL_SECONDS)
        state = tab.state()
        if state is None:
            settled = 0
            continue
        if state["verification"]:
            return LoginResult(OUTCOME_VERIFICATION, "The site asked for a verification step after the password. "
                               "Finish the login by hand in that browser profile.", tab.main_frame_host())
        if state["password"]:
            settled = 0
            continue
        # A form that vanished because the browser is showing its own error page is not a login.
        if tab.main_frame_unreachable():
            return LoginResult(OUTCOME_FAILED, "The site did not answer after the form was submitted.", host)
        settled += 1
        if settled >= SETTLED_POLLS:
            return LoginResult(OUTCOME_LOGGED_IN, "The login form was submitted and has gone.", tab.main_frame_host())
    return LoginResult(OUTCOME_FAILED, "The login form is still showing after submitting: the site rejected "
                       "the credentials or needs something else.", host)


def login(entry: Entry, port: int, timeout_seconds: float = 30) -> LoginResult:
    """Fill and submit the login form of the entry's site in the browser on `port`."""
    filelog.write(f"[login] start: entry={entry.name}, port={port}")
    if not entry.allowed_domains:
        return LoginResult(OUTCOME_REFUSED, f"Entry '{entry.name}' has no allowed domains, so it cannot be typed into any page.")
    secret = entry.secret.reveal()
    SCRUBBER.add(secret, entry.username)

    targets = list_page_targets(port)
    matching = [t for t in targets if host_allowed(host_of(t.get("url", "")), entry.allowed_domains)]
    if not matching:
        hosts = sorted({host_of(t.get("url", "")) or t.get("url", "")[:30] for t in targets})
        return LoginResult(OUTCOME_REFUSED, f"No open tab is on an allowed domain for '{entry.name}' (allowed: "
                           f"{', '.join(entry.allowed_domains)}; open tabs: {', '.join(hosts) or 'none'}). "
                           "Open the login page in that browser first. Nothing was typed.")

    conn = CdpConnection(matching[0]["webSocketDebuggerUrl"])
    tab = _Tab(conn, entry)
    try:
        result = _drive(tab, entry, secret, timeout_seconds)
        if result.outcome != OUTCOME_LOGGED_IN:
            tab.clear_passwords()
        filelog.write(f"[login] done: entry={entry.name}, outcome={result.outcome}, host={result.host}")
        return result
    except CdpError:
        tab.clear_passwords()
        raise
    finally:
        conn.close()
