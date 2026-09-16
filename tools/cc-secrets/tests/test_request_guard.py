"""The request guard that holds the password to a POST to an allowed origin, judged at the moment the browser is
about to send each request - so a page's own script, or a redirect, cannot carry it anywhere else (review of
pull request 2891 at 73d8c536). Real Chrome is covered by the probe in tests/live_leak_check.py; these tests
pin the decision and the wiring."""

import base64
import json
from urllib.parse import quote, quote_plus

import pytest

from conftest import add_entry
from src import browser_login
from src.browser_login import CdpConnection, OUTCOME_LOGGED_IN, OUTCOME_REFUSED, _Tab, blocked_request_reason
from src.redact import Scrubber

SECRET = "Pw-guard+Test/9 42"
ALLOWED = ["https://127.0.0.1"]


@pytest.fixture
def forms():
    scrubber = Scrubber()
    scrubber.add(SECRET)
    return scrubber


def form_body(password=SECRET):
    return "username=leak-user&password=" + quote_plus(password)


def test_PostToAllowedOrigin_WithThePassword_IsSent(forms):
    request = {"url": "https://127.0.0.1/session", "method": "POST", "hasPostData": True, "postData": form_body()}

    assert blocked_request_reason(request, forms, ALLOWED) is None


def test_GetCarryingThePasswordInItsAddress_IsBlocked_EvenToTheAllowedOrigin(forms):
    # The review's case: a submit handler switched the form to GET and stopped the event reaching the window.
    request = {"url": "https://127.0.0.1/session?" + form_body(), "method": "GET"}

    assert "in its address" in blocked_request_reason(request, forms, ALLOWED)


def test_PostToAllowedOrigin_WithThePasswordInItsAddress_IsBlocked(forms):
    request = {"url": "https://127.0.0.1/session?p=" + quote(SECRET, safe=""), "method": "POST",
               "hasPostData": True, "postData": "x=1"}

    assert blocked_request_reason(request, forms, ALLOWED) is not None


def test_PostToAnotherOrigin_WithThePassword_IsBlocked(forms):
    # The review's case: a submit handler changed the form's action and stopped the event reaching the window.
    request = {"url": "https://127.0.0.1:8443/receive", "method": "POST", "hasPostData": True, "postData": form_body()}

    assert "127.0.0.1:8443" in blocked_request_reason(request, forms, ALLOWED)


def test_PostToAnotherOrigin_WithThePasswordInBodyEntriesOnly_IsBlocked(forms):
    request = {"url": "https://other.test/receive", "method": "POST", "hasPostData": True,
               "postDataEntries": [{"bytes": base64.b64encode(form_body().encode()).decode()}]}

    assert blocked_request_reason(request, forms, ALLOWED) is not None


def test_RedirectedPostToAnotherOrigin_WhoseBodyIsNotShown_IsBlocked(forms):
    # The review's case: the allowed site answers 307 and the browser resends the POST. A body the browser does
    # not show may be the password, so it is blocked rather than trusted.
    request = {"url": "https://other.test/receive", "method": "POST", "hasPostData": True}

    assert "did not show" in blocked_request_reason(request, forms, ALLOWED)


def test_PostToAnotherOrigin_WithoutThePassword_IsSent(forms):
    request = {"url": "https://analytics.test/collect", "method": "POST", "hasPostData": True, "postData": "event=view"}

    assert blocked_request_reason(request, forms, ALLOWED) is None


def test_GetToAnotherOrigin_WithoutThePassword_IsSent(forms):
    # A 303 after a successful login, to a single sign-on page on another site, carries no password.
    assert blocked_request_reason({"url": "https://sso.test/landing", "method": "GET"}, forms, ALLOWED) is None


def test_PasswordInAHeaderToAnotherOrigin_IsBlocked(forms):
    # The review's case (186bea1d): a submit handler sends the password in a header of its own, by GET.
    request = {"url": "https://other.test/collect", "method": "GET", "headers": {"X-Password": SECRET}}

    assert "'X-Password' header" in blocked_request_reason(request, forms, ALLOWED)


def test_BasicAuthorizationHeaderToAnotherOrigin_IsBlocked():
    forms = Scrubber()
    forms.add(SECRET, "leak-user")
    token = base64.b64encode(f"leak-user:{SECRET}".encode()).decode()
    request = {"url": "https://other.test/api", "method": "GET", "headers": {"Authorization": "Basic " + token}}

    assert blocked_request_reason(request, forms, ALLOWED) is not None


def test_HeadersWithoutThePassword_AreSent(forms):
    request = {"url": "https://other.test/collect", "method": "GET",
               "headers": {"Accept": "*/*", "User-Agent": "Mozilla/5.0"}}

    assert blocked_request_reason(request, forms, ALLOWED) is None


def test_PasswordInAJsonBodyToAnotherOrigin_IsBlocked(forms):
    request = {"url": "https://other.test/api", "method": "PUT", "hasPostData": True,
               "postData": json.dumps({"password": SECRET})}

    assert blocked_request_reason(request, forms, ALLOWED) is not None


class FakeSocket:
    def __init__(self, incoming):
        self.incoming = [json.dumps(m) for m in incoming]
        self.sent = []

    def send(self, text):
        self.sent.append(json.loads(text))

    def recv(self, timeout=None):
        if not self.incoming:
            raise TimeoutError()
        return self.incoming.pop(0)

    def close(self):
        pass


def _connection(incoming):
    conn = CdpConnection.__new__(CdpConnection)
    conn._timeout = 1
    conn._next_id = 0
    conn._handlers = {}
    conn._ws = FakeSocket(incoming)
    return conn


def test_Call_HandlesAnEventThatArrivesBeforeItsAnswer(home):
    # home: a call writes the tool log, which lives in the secrets folder - a throwaway one here.
    conn = _connection([{"method": "Fetch.requestPaused", "params": {"requestId": "r1"}}, {"id": 1, "result": {"ok": 1}}])
    seen = []
    conn.on("Fetch.requestPaused", seen.append)

    assert conn.call("Runtime.evaluate") == {"ok": 1}
    assert seen == [{"requestId": "r1"}]


def test_Pump_HandlesEventsWhileWaiting():
    conn = _connection([{"method": "Fetch.requestPaused", "params": {"requestId": "r2"}}])
    seen = []
    conn.on("Fetch.requestPaused", seen.append)

    conn.pump(0.05)

    assert seen == [{"requestId": "r2"}]


class GuardConnection:
    """Records what the guard asks the browser to do with each paused request."""

    def __init__(self):
        self.handlers = {}
        self.calls = []
        self.sent = []

    def on(self, event, handler):
        if handler is None:
            self.handlers.pop(event, None)
        else:
            self.handlers[event] = handler

    def call(self, method, params=None):
        self.calls.append(method)
        return {}

    def send(self, method, params=None):
        self.sent.append((method, params))

    def pause(self, request):
        self.handlers["Fetch.requestPaused"]({"requestId": "r", "request": request})


@pytest.fixture
def entry(store):
    add_entry(store, secret=SECRET, domains=tuple(ALLOWED))
    return store.get("devlinux")


def test_Guard_FailsARequestThatCarriesThePassword_AndRecordsWhy(entry):
    conn = GuardConnection()
    tab = _Tab(conn, entry)

    tab.guard_requests(SECRET, ALLOWED)
    conn.pause({"url": "https://other.test/receive", "method": "POST", "hasPostData": True, "postData": form_body()})

    assert conn.calls == ["Fetch.enable"]
    assert conn.sent == [("Fetch.failRequest", {"requestId": "r", "errorReason": "BlockedByClient"})]
    assert len(tab.blocked) == 1 and SECRET not in tab.blocked[0]


def test_Guard_FailsAPausedRequestCarryingThePasswordInAHeader(entry):
    conn = GuardConnection()
    tab = _Tab(conn, entry)

    tab.guard_requests(SECRET, ALLOWED)
    conn.pause({"url": "https://other.test/collect", "method": "GET", "headers": {"X-Password": SECRET}})

    assert conn.sent == [("Fetch.failRequest", {"requestId": "r", "errorReason": "BlockedByClient"})]
    assert len(tab.blocked) == 1 and SECRET not in tab.blocked[0]


def test_Guard_RecognisesTheEntrysBasicAuthorizationHeader(entry):
    conn = GuardConnection()
    tab = _Tab(conn, entry)
    token = base64.b64encode(f"{entry.username}:{SECRET}".encode()).decode()

    tab.guard_requests(SECRET, ALLOWED)
    conn.pause({"url": "https://other.test/api", "method": "GET", "headers": {"Authorization": "Basic " + token}})

    assert conn.sent[0][0] == "Fetch.failRequest"


def test_Guard_LetsAnAllowedRequestThrough_AndIsReleased(entry):
    conn = GuardConnection()
    tab = _Tab(conn, entry)

    tab.guard_requests(SECRET, ALLOWED)
    conn.pause({"url": "https://127.0.0.1/session", "method": "POST", "hasPostData": True, "postData": form_body()})
    tab.release_requests()

    assert conn.sent == [("Fetch.continueRequest", {"requestId": "r"})]
    assert tab.blocked == []
    assert conn.calls == ["Fetch.enable", "Fetch.disable"]
    assert "Fetch.requestPaused" not in conn.handlers


LOGIN_TAB = [{"id": "T1", "url": "https://127.0.0.1/login", "webSocketDebuggerUrl": "ws://login"}]


def _patch(monkeypatch, tab):
    class Connection:
        def __init__(self, url):
            pass

        def close(self):
            pass

    monkeypatch.setattr(browser_login, "POLL_SECONDS", 0.01)
    monkeypatch.setattr(browser_login, "list_page_targets", lambda port: LOGIN_TAB)
    monkeypatch.setattr(browser_login, "CdpConnection", Connection)
    monkeypatch.setattr(browser_login, "_Tab", lambda conn, entry: tab)


def test_Login_ABlockedRequest_MakesTheOutcomeRefused_EvenWhenTheFormWent(entry, monkeypatch):
    from test_login import FakeTab, page

    tab = FakeTab([page(password=True, username=True), page(url="https://127.0.0.1/welcome")],
                  blocks_on_submit=["a POST request to https://other.test that carried the password"])
    _patch(monkeypatch, tab)

    result = browser_login.login(entry, 9310, 2)

    assert result.outcome == OUTCOME_REFUSED
    assert "other.test" in result.reason
    assert tab.events[-1] == "guard off"


def test_Login_TheGuardIsOnBeforeThePasswordIsTyped(entry, monkeypatch):
    from test_login import FakeTab, page

    tab = FakeTab([page(password=True, username=True), page(url="https://127.0.0.1/welcome")])
    _patch(monkeypatch, tab)

    result = browser_login.login(entry, 9310, 2)

    assert result.outcome == OUTCOME_LOGGED_IN
    assert tab.events.index("guard on") < tab.events.index("fill password")
    assert tab.events[-1] == "guard off"


def test_Login_TheGuardIsReleased_WhenTheLoginRaises(entry, monkeypatch):
    from test_login import FakeTab, page

    tab = FakeTab([page(password=True, username=True)], fail_with=RuntimeError("dropped"))
    _patch(monkeypatch, tab)

    with pytest.raises(RuntimeError):
        browser_login.login(entry, 9310, 2)

    assert tab.events[-1] == "guard off"


# "Passwoerd42" with an a-umlaut and an o-umlaut, spelled with chr() so this file stays plain ASCII.
ACCENTED = "P" + chr(0xE4) + "ssw" + chr(0xF6) + "rd42"


@pytest.fixture
def accented_entry(store):
    add_entry(store, name="accented", secret=ACCENTED, username="user", domains=tuple(ALLOWED))
    return store.get("accented")


def test_Guard_FailsABasicAuthorizationHeaderEncodedAsIso88591(accented_entry):
    # The review's case (fbe4293e): btoa() and HTTP Basic use ISO-8859-1, so the header is not the UTF-8 Base64.
    conn = GuardConnection()
    tab = _Tab(conn, accented_entry)
    token = base64.b64encode(f"user:{ACCENTED}".encode("latin-1")).decode()
    assert token != base64.b64encode(f"user:{ACCENTED}".encode("utf-8")).decode(), "the case needs the two to differ"

    tab.guard_requests(ACCENTED, ALLOWED)
    conn.pause({"url": "https://other.test/api", "method": "GET", "headers": {"Authorization": "Basic " + token}})

    assert conn.sent[0][0] == "Fetch.failRequest"


def test_HeaderOnAPostToTheAllowedOrigin_IsSent(forms):
    # The policy: in a POST to an allowed origin the password may be in the body or a header - both go to that
    # site and neither is kept in the address or the history.
    request = {"url": "https://127.0.0.1/session", "method": "POST", "hasPostData": True, "postData": "",
               "headers": {"X-Password": SECRET}}

    assert blocked_request_reason(request, forms, ALLOWED) is None
