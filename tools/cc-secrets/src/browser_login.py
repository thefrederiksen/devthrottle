"""Filling a login form in a Director-owned browser over its debug port.

The secret goes from this process straight to the page, over the browser's loopback debug connection.
It never passes through the agent, and nothing this module logs carries a debug-protocol parameter:
the tool log records method names, message ids and outcomes only.

Before anything is typed, three things are checked against the entry's allowed ORIGINS (scheme, host and
port, exactly):

1. the tab's address, read from the browser (Page.getFrameTree), which page scripts cannot alter;
2. where the form will send it: the submit button's formaction, else the form's action;
3. HOW the form will send it: only POST is accepted. A GET form would put the password in the page
   address and the browser history, where nothing afterwards can remove it.

Those checks are not enough on their own, because the browser reads the form's method and action AFTER the
page's submit handlers have run, so a handler could change either one after a check made beforehand. The
submission is therefore bound to what was checked (review of pull request 2891):

- a listener in this login's own isolated script world, at the window in the BUBBLE phase - which runs
  after the page's own handlers - re-reads where and how the form is about to send, and CANCELS the
  submission in that event if either has changed;
- afterwards the tab's address and history are checked for the secret, and cleared if it is there.

That listener is not enough on its own either: a page handler that calls `stopPropagation()` keeps the
event from ever reaching the window, and a site that answers the POST with a 307 or 308 redirect makes the
browser send the same body to wherever the redirect points (review of pull request 2891 at 73d8c536). So
from just before the password is typed until the tab has been made safe, every request the tab makes is
PAUSED by the browser before it is sent, and judged by `blocked_request_reason`. The policy: the password
may travel only in a POST to an allowed origin - in its body or in a header, both of which go to that site
and neither of which is kept in the address or the history - and never in any request's address. A request
whose address carries the password, or that is not a POST to an allowed origin and has a header or a body
carrying the password - or a body the browser does not show - is failed before it leaves the browser, and
the login is refused. A redirect is a new request, so
it is judged again at each hop.

That holds WHILE cc-secrets is connected, and only then. The browser pauses a request for as long as the
connection that asked for the pause is there to answer it; if that connection drops with a request paused -
cc-secrets stopped, killed or crashed mid-login - Chrome sends the request on without a judgement. Measured
against real Chrome: a paused request answered with a block never arrived, and the same request with the
connection closed instead did arrive (review of pull request 2891 at 186bea1d). Nothing on the debug
connection can close that gap, because everything set up over it ends with it. What keeps the gap small is
that each paused request is answered as soon as it arrives. Measured against real Chrome: the handler that stops the event (for the
method and for the destination) and the 307 to another origin all sent the password before, and none did
after.

Holding the form's `action` and `method` unwritable was tried and REMOVED: a property defined on a node in
an isolated world is only visible in that world, so the page's own scripts still see the original setter
and still change the form. Measured against real Chrome: with only that hold in place all three of the
review's cases still leaked, and with only the cancelling listener none of them did.

The form lookup, the checks and the fill run in an ISOLATED script world, which shares the page's
document but not its JavaScript objects, so a script the page - or an agent driving the same tab - put in
place cannot change what `form.action`, `form.method` or the value setter mean to this code.

Once a password has been typed, whatever the outcome - logged in, refused, failed, or a crash - the tab is
made safe, and that is CONFIRMED, not assumed: every password field is emptied (hidden ones included), the
tab's back and forward history is reset, and then the tab is read back to confirm that no password field
holds a value, that one history entry is left, and that neither the address nor the history carries the
secret. If that cannot be confirmed on the login's own connection - for example because the connection
dropped - it is done again over a fresh connection to the same tab. If it still cannot be confirmed, the
tab is closed. If the tab cannot be closed either, the login fails with a message telling the person to
close it.

Not covered, stated plainly: JavaScript already running in the page receives the password, because the
site needs it - so a listener an agent attached to the page before calling login can copy it, and a page
can transform it before sending (a hash or a reversal no longer looks like the password). The request guard
sees the requests of this tab's own frames: a service worker, a cross-origin frame running in another
process, a WebSocket message, or another tab are outside it. And a header the page's own script sets is
judged only as the browser REPORTS it, which is lossy for bytes outside ASCII: measured in real Chrome, a
header whose value held an accented letter was reported with that letter and the two characters after it
missing, while the full value went out on the wire. So a page script that puts a non-ASCII password, as is,
into a header of a request to another site is not caught (found testing the review of pull request 2891 at
fbe4293e). Base64 of it - an authorization header - is plain ASCII and IS caught. If the debug connection drops while a request
is paused, the browser sends it on (see above). Also not covered: login forms inside cross-origin frames, two-step verification and captchas (reported as a
verification stop for the owner to finish by hand), and a hostile process running as this same user that
binds the profile's debug port in place of the real browser.
"""

from __future__ import annotations

import base64
import json
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional, Tuple
from urllib.parse import unquote, unquote_plus

from . import filelog
from .errors import CcSecretsError
from .redact import SCRUBBER, Scrubber
from .store import Entry, origin_allowed, origin_of

OUTCOME_LOGGED_IN = "logged in"
OUTCOME_FAILED = "failed"
OUTCOME_VERIFICATION = "verification"
OUTCOME_REFUSED = "refused"

SUBMITTED = "form"
SUBMITTED_BY_ENTER = "enter"
CHANGED_BEFORE_SUBMIT = "changed"
CANCELLED_IN_SUBMIT_HANDLER = "aborted"

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

# Where and how the form holding `this` will send: the submit button's formaction and formmethod, else the
# form's action and method. `form.method` is always "get", "post" or "dialog" (a missing method is "get").
_READ_SUBMISSION = """
  const form = this.form;
  const readSubmission = () => {
    const button = form ? form.querySelector('button[type=submit], input[type=submit], button:not([type])') : null;
    const submitter = (button && button.form === form) ? button : null;
    return {
      target: !form ? '' : ((submitter && submitter.hasAttribute('formaction')) ? submitter.formAction : form.action),
      method: !form ? '' : ((submitter && submitter.hasAttribute('formmethod')) ? submitter.formMethod : form.method),
      submitter: submitter,
    };
  };
"""

_SUBMISSION = "function() {" + _READ_SUBMISSION + """
  const now = readSubmission();
  return {target: now.target, method: now.method};
}"""

_SET_VALUE = """function(value) {
  this.focus();
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
  setter.call(this, value);
  this.dispatchEvent(new Event('input', {bubbles: true}));
  this.dispatchEvent(new Event('change', {bubbles: true}));
  return this.value.length === value.length;
}"""

_SUBMIT = "function(expectedTarget, expectedMethod) {" + _READ_SUBMISSION + """
  const first = readSubmission();
  if (first.target !== expectedTarget || first.method !== expectedMethod) return 'changed';
  if (!form) { this.focus(); return 'enter'; }

  let cancelled = false;
  // The page's own handlers run at the form; this one runs after them, in the bubble phase at the window,
  // so it sees whatever they changed - and cancels the submission there.
  const guard = event => {
    const now = readSubmission();
    if (now.target !== expectedTarget || now.method !== expectedMethod) {
      cancelled = true;
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  };
  window.addEventListener('submit', guard, false);

  try {
    if (typeof form.requestSubmit === 'function') {
      if (first.submitter) form.requestSubmit(first.submitter); else form.requestSubmit();
    } else if (first.submitter) { first.submitter.click(); } else { form.submit(); }
  } finally {
    window.removeEventListener('submit', guard, false);
  }
  return cancelled ? 'aborted' : 'form';
}"""

_CLEAR_PASSWORDS = """(() => {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
  let cleared = 0;
  document.querySelectorAll('input[type=password]').forEach(e => {
    if (e.value) { setter.call(e, ''); e.dispatchEvent(new Event('input', {bubbles: true})); cleared++; }
  });
  return cleared;
})()"""

_PASSWORD_LEFT = "Array.from(document.querySelectorAll('input[type=password]')).some(e => e.value.length > 0)"
_ADDRESS = "location.href"


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
        self._handlers: Dict[str, Callable[[Dict], None]] = {}
        self._ws = connect(websocket_url, open_timeout=timeout_seconds, max_size=None)
        filelog.write("[cdp] connected")

    def on(self, event: str, handler: Optional[Callable[[Dict], None]]) -> None:
        """Handle `event` whenever it arrives - while a call waits for its answer, or while pumping."""
        if handler is None:
            self._handlers.pop(event, None)
        else:
            self._handlers[event] = handler

    def _dispatch(self, message: Dict) -> None:
        handler = self._handlers.get(message.get("method", ""))
        if handler is not None:
            handler(message.get("params", {}))

    def send(self, method: str, params: Optional[Dict] = None) -> None:
        """Send a call without waiting for its answer (an event handler cannot wait inside another call)."""
        self._next_id += 1
        filelog.write(f"[cdp] -> {method} id={self._next_id} (not awaited)")
        self._ws.send(json.dumps({"id": self._next_id, "method": method, "params": params or {}}))

    def pump(self, seconds: float) -> None:
        """Wait `seconds`, handling every event that arrives meanwhile."""
        deadline = time.monotonic() + seconds
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return
            try:
                raw = self._ws.recv(timeout=remaining)
            except TimeoutError:
                return
            message = json.loads(raw)
            if "id" not in message:
                self._dispatch(message)

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
            if "id" not in reply:
                self._dispatch(reply)
                continue
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


def _request_body(request: Dict) -> Optional[bytes]:
    """The request body as the browser reported it; None when it has a body the report does not include."""
    entries = request.get("postDataEntries")
    if entries:
        try:
            return b"".join(base64.b64decode(e.get("bytes", "")) for e in entries)
        except (ValueError, TypeError):
            return None
    if isinstance(request.get("postData"), str):
        return request["postData"].encode("utf-8")
    return None if request.get("hasPostData") else b""


def blocked_request_reason(request: Dict, forms: Scrubber, allowed: List[str]) -> Optional[str]:
    """Why a request made while the password is in the page must not be sent, or None when it may go.

    The password may travel only in a POST to an allowed origin (in its body or a header - both go to that site
    and neither is kept in the address or history), and never in any request's address. A request is blocked
    when its address carries the password (in any form the scrubber knows, percent-decoded too), or when it is
    not a POST to an allowed origin and a header carries the password, or its body carries it - or it has a body
    the browser did not show. Every request is judged on its own, so a redirect is judged again at each hop.
    """
    url = str(request.get("url", ""))
    method = str(request.get("method", "GET")).upper()
    where = origin_of(url) or "an unknown address"
    if any(forms.contains(text) for text in (url, unquote(url), unquote_plus(url))):
        return f"a {method} request to {where} that carried the password in its address"
    if method == "POST" and origin_allowed(url, allowed):
        return None
    # A page script can put it in a header of its own (review of pull request 2891 at 186bea1d).
    headers = request.get("headers") or {}
    for name, value in headers.items():
        if forms.contains(f"{name}: {value}") or forms.contains(str(value)):
            return f"a {method} request to {where} that carried the password in its '{name}' header"
    body = _request_body(request)
    if body is None:
        return f"a {method} request to {where} with a body the browser did not show, which may have held the password"
    if body and forms.scrub_bytes(body) != body:
        return f"a {method} request to {where} that carried the password"
    return None


def _close_quietly(conn) -> None:
    """Close a connection that may already be broken; a failure here is logged by type and changes nothing."""
    try:
        conn.close()
    except Exception as exc:
        filelog.write(f"[cdp] close FAILED: {type(exc).__name__}")


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
        self.blocked: List[str] = []
        self._guarding = False

    def wait(self, seconds: float) -> None:
        """Let time pass while still answering the browser's paused requests."""
        self._conn.pump(seconds)

    def guard_requests(self, secret: str, allowed: List[str]) -> None:
        """From now on, pause every request this tab makes and send only those the password may travel in.
        Only while this connection lasts: a request paused when it drops is sent on by the browser."""
        forms = Scrubber()
        # With the username, so "username:password" in base64 - a Basic authorization header - is recognised.
        forms.add(secret, self._entry.username)

        def on_paused(params: Dict) -> None:
            request_id = params.get("requestId")
            reason = blocked_request_reason(params.get("request", {}), forms, allowed)
            if reason is None:
                self._conn.send("Fetch.continueRequest", {"requestId": request_id})
                return
            self._conn.send("Fetch.failRequest", {"requestId": request_id, "errorReason": "BlockedByClient"})
            filelog.write(f"[login] blocked {reason}")
            self.blocked.append(reason)

        self._conn.on("Fetch.requestPaused", on_paused)
        self._conn.call("Fetch.enable", {"patterns": [{"urlPattern": "*", "requestStage": "Request"}]})
        self._guarding = True
        filelog.write("[login] request guard on")

    def release_requests(self) -> None:
        """Stop pausing requests. Logged by type and otherwise quiet: this runs while an error may be leaving."""
        if not self._guarding:
            return
        try:
            self._conn.call("Fetch.disable")
            filelog.write("[login] request guard off")
        except Exception as exc:
            filelog.write(f"[login] turning the request guard off FAILED: {type(exc).__name__}")
        finally:
            self._conn.on("Fetch.requestPaused", None)
            self._guarding = False

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

    def submission(self, object_id: str) -> Tuple[str, str]:
        """(where, how) the form holding the field will send: an absolute address and "get", "post" or "dialog".
        Both are empty when there is no form."""
        reply = self._conn.call("Runtime.callFunctionOn", {
            "objectId": object_id, "functionDeclaration": _SUBMISSION, "returnByValue": True})
        if "exceptionDetails" in reply:
            raise CdpError("Reading where and how the form sends raised an error in the page.")
        value = reply["result"].get("value")
        if not isinstance(value, dict):
            raise CdpError("Reading where and how the form sends returned something unexpected.")
        return str(value.get("target") or ""), str(value.get("method") or "").lower()

    def fill(self, object_id: str, value: str, what: str) -> None:
        reply = self._conn.call("Runtime.callFunctionOn", {
            "objectId": object_id, "functionDeclaration": _SET_VALUE,
            "arguments": [{"value": value}], "returnByValue": True})
        if "exceptionDetails" in reply or reply["result"].get("value") is not True:
            raise CdpError(f"The {what} field did not accept the value.")

    def submit(self, object_id: str, expected_target: str, expected_method: str) -> str:
        """Submit, held to `expected_target` and `expected_method`. Returns what happened: SUBMITTED,
        SUBMITTED_BY_ENTER, CHANGED_BEFORE_SUBMIT (nothing was sent), or CANCELLED_IN_SUBMIT_HANDLER
        (the page's own submit handler changed it, and the submission was cancelled in that event)."""
        reply = self._conn.call("Runtime.callFunctionOn", {
            "objectId": object_id, "functionDeclaration": _SUBMIT,
            "arguments": [{"value": expected_target}, {"value": expected_method}], "returnByValue": True})
        if "exceptionDetails" in reply:
            raise CdpError("Submitting the form raised an error in the page.")
        how = str(reply["result"].get("value") or "")
        if how == SUBMITTED_BY_ENTER:
            for kind in ("rawKeyDown", "char", "keyUp"):
                params = {"type": kind, "key": "Enter", "code": "Enter", "windowsVirtualKeyCode": 13}
                if kind == "char":
                    params["text"] = "\r"
                self._conn.call("Input.dispatchKeyEvent", params)
        return how

    def _address_and_history(self) -> Tuple[str, List[str]]:
        address = self._evaluate(_ADDRESS, True).get("result", {}).get("value") or ""
        entries = self._conn.call("Page.getNavigationHistory").get("entries", [])
        return str(address), [str(e.get("url", "")) for e in entries]

    def clean_up_confirmed(self) -> bool:
        """Empty every password field, reset the history, then read the tab back. True only when no password
        field holds a value, one history entry is left, and neither the address nor the history carries the
        secret. Any failure - including a dropped connection - is logged by type and answers False, because
        this also runs while an error is on its way out."""
        secret = self._entry.secret.reveal()
        try:
            cleared = self._evaluate(_CLEAR_PASSWORDS, True)
            filelog.write(f"[login] cleared {cleared.get('result', {}).get('value')} password field(s)")
            self._conn.call("Page.resetNavigationHistory", {})
            address, history = self._address_and_history()
            if secret in address or any(secret in url for url in history):
                # The page put the password in its own address (a GET submission it forced). The address is
                # the current history entry, so the only way to remove it is to leave the page.
                filelog.write("[login] the tab's address carried the password; leaving the page and clearing it")
                self._conn.call("Page.navigate", {"url": "about:blank"})
                self.wait(POLL_SECONDS)
                self._world = None
                self._conn.call("Page.resetNavigationHistory", {})
                address, history = self._address_and_history()
                if secret in address or any(secret in url for url in history):
                    filelog.write("[login] clean-up NOT confirmed: the address still carries the password")
                    return False
            left = self._evaluate(_PASSWORD_LEFT, True)
            if "exceptionDetails" in left or left.get("result", {}).get("value") is not False:
                filelog.write("[login] clean-up NOT confirmed: a password field still holds a value")
                return False
            if len(history) != 1:
                filelog.write(f"[login] clean-up NOT confirmed: {len(history)} history entries are left")
                return False
            filelog.write("[login] clean-up confirmed: no password field, no address or history entry holds it")
            return True
        except Exception as exc:
            filelog.write(f"[login] clean-up FAILED: {type(exc).__name__}")
            return False


def _close_tab(port: int, target_id: str) -> bool:
    """Close the tab and confirm it is gone. False when that cannot be confirmed."""
    if not target_id:
        return False
    try:
        urllib.request.urlopen(f"http://127.0.0.1:{port}/json/close/{target_id}", timeout=10).read()
    except Exception as exc:
        filelog.write(f"[login] closing the tab FAILED: {type(exc).__name__}")
    try:
        remaining = list_page_targets(port)
    except CdpError as exc:
        filelog.write(f"[login] could not list tabs to confirm the close: {type(exc).__name__}")
        return False
    return all(t.get("id") != target_id for t in remaining)


def _make_tab_safe(tab: _Tab, entry: Entry, target: Dict, port: int) -> None:
    """Make sure no typed password is left in the login tab, and raise when that cannot be made sure."""
    if tab.clean_up_confirmed():
        return
    filelog.write("[login] clean-up not confirmed on the login connection; trying a fresh connection to the tab")
    try:
        fresh = CdpConnection(target["webSocketDebuggerUrl"])
    except Exception as exc:
        filelog.write(f"[login] fresh connection FAILED: {type(exc).__name__}")
        fresh = None
    if fresh is not None:
        try:
            if _Tab(fresh, entry).clean_up_confirmed():
                filelog.write("[login] clean-up confirmed on a fresh connection")
                return
        finally:
            _close_quietly(fresh)
    filelog.write("[login] clean-up could not be confirmed; closing the login tab")
    if _close_tab(port, str(target.get("id") or "")):
        filelog.write("[login] the login tab was closed")
        return
    raise CcSecretsError("A typed password may still be in the login tab, and cc-secrets could neither clear it nor "
                         "close the tab. Close that browser tab now.")


def _wait(tab: _Tab, deadline: float, done) -> Optional[Dict]:
    """Poll the page until `done(state)` is true; returns that state, or None at the deadline."""
    while time.monotonic() < deadline:
        tab.wait(POLL_SECONDS)
        state = tab.state()
        if state is not None and done(state):
            return state
    return None


def _submission_stopped(status: str, origin: str) -> Optional[LoginResult]:
    """The result when a submission did not go ahead, or None when it did."""
    if status == CHANGED_BEFORE_SUBMIT:
        return LoginResult(OUTCOME_FAILED, "The form's destination or method changed after it was checked, so it "
                           "was not submitted.", origin)
    if status == CANCELLED_IN_SUBMIT_HANDLER:
        return LoginResult(OUTCOME_REFUSED, "The page's own submit handler changed where or how the form sends, so "
                           "the submission was cancelled in that event. Nothing was sent.", origin)
    if status not in (SUBMITTED, SUBMITTED_BY_ENTER):
        return LoginResult(OUTCOME_FAILED, f"The form was not submitted ({status or 'no answer'}).", origin)
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
        # Checks 2 and 3: where, and how, the form sends what is typed into it.
        target, method = tab.submission(field)
        if target and not origin_allowed(target, allowed):
            return LoginResult(OUTCOME_REFUSED, f"The form on this page sends to '{origin_of(target)}', which is not "
                               f"an allowed address for '{entry.name}' (allowed: {', '.join(allowed)}). "
                               "Nothing was typed.", origin_of(url))
        if target and method != "post":
            return LoginResult(OUTCOME_REFUSED, f"The form on this page sends by {method.upper() or 'an unknown method'}, "
                               "not POST. A GET form would put the password in the page address and the browser "
                               "history. Nothing was typed.", origin_of(url))

        if username_id:
            tab.fill(username_id, entry.username, "username")
            username_filled = True
        if password_id:
            tab.guard_requests(secret, allowed)
            tab.typed = True
            tab.fill(password_id, secret, "password")
            stopped = _submission_stopped(tab.submit(password_id, target, method), origin_of(url))
            if stopped is not None:
                return stopped
            return _await_outcome(tab, deadline, origin_of(url))
        stopped = _submission_stopped(tab.submit(username_id, target, method), origin_of(url))
        if stopped is not None:
            return stopped
        after = _wait(tab, deadline, lambda s: s["password"] or s["verification"])
        if after is None:
            return LoginResult(OUTCOME_FAILED, "No password field appeared after the username was submitted.", origin_of(url))
    return LoginResult(OUTCOME_FAILED, "The login did not reach a password field in three steps.")


def _await_outcome(tab: _Tab, deadline: float, origin: str) -> LoginResult:
    settled = 0
    while time.monotonic() < deadline:
        tab.wait(POLL_SECONDS)
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

    target = matching[0]
    conn = CdpConnection(target["webSocketDebuggerUrl"])
    tab = _Tab(conn, entry)
    try:
        result = _drive(tab, entry, secret, timeout_seconds)
    finally:
        try:
            if tab.typed:
                _make_tab_safe(tab, entry, target, port)
        finally:
            try:
                tab.release_requests()
            finally:
                _close_quietly(conn)
    if tab.blocked:
        result = LoginResult(OUTCOME_REFUSED, "The page tried to send the password where this entry does not allow "
                             f"it, and cc-secrets blocked it before it left the browser: {'; '.join(tab.blocked)}.",
                             result.host)
    filelog.write(f"[login] done: entry={entry.name}, outcome={result.outcome}, origin={result.host}")
    return result
