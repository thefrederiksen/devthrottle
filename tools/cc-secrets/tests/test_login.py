"""The login decisions - which tab, the address and method checks, the steps, the outcome, the clean-up -
against a scripted tab. Driving real Chrome is proven by tests/live_leak_check.py, not here."""

import pytest

from conftest import add_entry
from src import browser_login
from src.browser_login import (CdpError, OUTCOME_FAILED, OUTCOME_LOGGED_IN, OUTCOME_REFUSED,
                               OUTCOME_VERIFICATION, _drive)
from src.errors import CcSecretsError


def page(url="https://127.0.0.1/login", password=False, username=False, verification=False, unreachable=False,
         target="https://127.0.0.1/session", method="post", target_after_fill=None, method_after_fill=None):
    return {"url": url, "password": password, "username": username, "verification": verification,
            "unreachable": unreachable, "target": target, "method": method,
            "target_after_fill": target_after_fill, "method_after_fill": method_after_fill}


class FakeTab:
    """A tab that moves to its next page on every submit. `confirmations` scripts what each clean-up reports."""

    def __init__(self, pages, fail_fill=None, fail_with=None, confirmations=(True,)):
        self.pages = [dict(p) for p in pages]
        self.index = 0
        self.filled = []
        self.submitted = 0
        self.cleaned = 0
        self.typed = False
        self.fail_fill = fail_fill
        self.fail_with = fail_with
        self.confirmations = list(confirmations)

    @property
    def current(self):
        return self.pages[self.index]

    def state(self):
        return {k: self.current[k] for k in ("password", "username", "verification")}

    def main_frame_url(self):
        return self.current["url"]

    def main_frame_unreachable(self):
        return self.current["unreachable"]

    def find(self, which):
        return f"{which}-{self.index}" if self.current[which] else None

    def submission(self, object_id):
        return self.current["target"], self.current["method"]

    def submit_target(self, object_id):
        return self.current["target"]

    def fill(self, object_id, value, what):
        if what == "password" and self.fail_with is not None:
            raise self.fail_with
        if what == "password" and self.fail_fill:
            raise CdpError(self.fail_fill.format(value=value))
        self.filled.append((what, value, self.index))
        if self.current["target_after_fill"]:
            self.current["target"] = self.current["target_after_fill"]
        if self.current["method_after_fill"]:
            self.current["method"] = self.current["method_after_fill"]

    def submit(self, object_id, expected_target, expected_method="post"):
        if self.current["target"] != expected_target or self.current["method"] != expected_method:
            return False
        self.submitted += 1
        self.index = min(self.index + 1, len(self.pages) - 1)
        return True

    def clean_up_confirmed(self):
        self.cleaned += 1
        return self.confirmations.pop(0) if self.confirmations else True

    clean_up = clean_up_confirmed


@pytest.fixture(autouse=True)
def fast_polls(monkeypatch):
    monkeypatch.setattr(browser_login, "POLL_SECONDS", 0.01)


@pytest.fixture
def entry(store):
    add_entry(store, domains=("https://127.0.0.1", "https://*.example.com"))
    return store.get("devlinux")


def test_OnePageForm_FillsBoth_SubmitsOnce_LogsIn(entry):
    tab = FakeTab([page(password=True, username=True), page(url="https://127.0.0.1/welcome")])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_LOGGED_IN
    assert [(w, i) for w, _, i in tab.filled] == [("username", 0), ("password", 0)]
    assert tab.submitted == 1


def test_UsernameThenPasswordPages_LogsIn(entry):
    tab = FakeTab([page(username=True), page(password=True), page()])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_LOGGED_IN
    assert [(w, i) for w, _, i in tab.filled] == [("username", 0), ("password", 1)]


@pytest.mark.parametrize("url", [
    "https://evil.test/login",
    "https://127.0.0.1:8443/login",   # another port of the allowed host
    "http://127.0.0.1/login",         # http when https is allowed
])
def test_TabOnAnotherOrigin_RefusedAndNothingTyped(entry, url):
    tab = FakeTab([page(url=url, password=True, username=True, target=url)])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_REFUSED
    assert tab.filled == []


@pytest.mark.parametrize("target", [
    "http://127.0.0.1:9999/steal",     # an agent pointed form.action at its own server
    "https://127.0.0.1:8443/session",  # another port
    "https://evil.test/session",
])
def test_FormSendsToAnotherOrigin_RefusedAndNothingTyped(entry, target):
    tab = FakeTab([page(password=True, username=True, target=target)])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_REFUSED
    assert "sends to" in result.reason
    assert tab.filled == [] and tab.typed is False


@pytest.mark.parametrize("method", ["get", "dialog", ""])
def test_FormThatDoesNotPost_RefusedBeforeAnythingIsTyped(entry, method):
    # The review's probe: <form action="/welcome"> has no method, so it sends by GET and the password would
    # land in the page address and the browser history.
    tab = FakeTab([page(password=True, username=True, target="https://127.0.0.1/welcome", method=method),
                   page(url="https://127.0.0.1/welcome?username=leak-user&password=typed")])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_REFUSED
    assert "POST" in result.reason
    assert tab.filled == [] and tab.submitted == 0 and tab.typed is False


def test_UsernameStepThatDoesNotPost_IsRefusedToo(entry):
    tab = FakeTab([page(username=True, method="get"), page(password=True)])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_REFUSED
    assert tab.filled == []


def test_PageWithoutForm_IsFilledOnThePageOriginCheck(entry):
    tab = FakeTab([page(password=True, username=True, target="", method=""), page(url="https://127.0.0.1/app")])

    assert _drive(tab, entry, entry.secret.reveal(), 2).outcome == OUTCOME_LOGGED_IN


def test_FormTargetChangesAfterTheCheck_NotSubmitted(entry):
    tab = FakeTab([page(password=True, username=True, target_after_fill="http://127.0.0.1:9999/steal")])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_FAILED
    assert "changed" in result.reason
    assert tab.submitted == 0 and tab.typed is True


def test_FormMethodChangesToGetAfterTheCheck_NotSubmitted(entry):
    tab = FakeTab([page(password=True, username=True, method_after_fill="get")])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_FAILED
    assert tab.submitted == 0


def test_SecondStepMovesToDisallowedOrigin_PasswordNeverTyped(entry):
    tab = FakeTab([page(username=True), page(url="https://login.evil.test/pw", password=True,
                                             target="https://login.evil.test/session")])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_REFUSED
    assert [w for w, _, _ in tab.filled] == ["username"]


def test_WildcardSubdomain_IsAllowed(entry):
    tab = FakeTab([page(url="https://login.example.com/", password=True, username=True,
                        target="https://login.example.com/session"), page(url="https://app.example.com/")])

    assert _drive(tab, entry, entry.secret.reveal(), 2).outcome == OUTCOME_LOGGED_IN


def test_VerificationAfterPassword_ReportsVerification(entry):
    tab = FakeTab([page(password=True, username=True), page(verification=True)])

    assert _drive(tab, entry, entry.secret.reveal(), 2).outcome == OUTCOME_VERIFICATION


def test_FormStillShowing_Fails(entry):
    tab = FakeTab([page(password=True, username=True)])

    result = _drive(tab, entry, entry.secret.reveal(), 0.3)

    assert result.outcome == OUTCOME_FAILED
    assert "still showing" in result.reason


def test_SubmitLandsOnBrowserErrorPage_FailsInsteadOfLoggedIn(entry):
    tab = FakeTab([page(password=True, username=True), page(unreachable=True)])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_FAILED
    assert "did not answer" in result.reason


def test_NoLoginForm_Fails(entry):
    assert _drive(FakeTab([page()]), entry, entry.secret.reveal(), 1).outcome == OUTCOME_FAILED


LOGIN_TAB = [{"id": "T1", "url": "https://127.0.0.1/login", "webSocketDebuggerUrl": "ws://login"}]


def _patch_browser(monkeypatch, targets, tab, close_succeeds=True, fresh_connection_fails=False):
    connections, closed = [], []
    monkeypatch.setattr(browser_login, "list_page_targets", lambda port: targets)

    class FakeConnection:
        def __init__(self, url):
            if connections and fresh_connection_fails:
                raise ConnectionRefusedError("the tab is gone")
            connections.append(url)

        def close(self):
            pass

    def close_tab(port, target_id):
        closed.append(target_id)
        return close_succeeds

    monkeypatch.setattr(browser_login, "CdpConnection", FakeConnection)
    monkeypatch.setattr(browser_login, "_Tab", lambda conn, entry: tab)
    monkeypatch.setattr(browser_login, "_close_tab", close_tab, raising=False)
    return connections, closed


def test_Login_PicksTheTabOnTheAllowedOrigin(entry, monkeypatch):
    tab = FakeTab([page(password=True, username=True), page(url="https://127.0.0.1/welcome")])
    targets = [{"url": "http://127.0.0.1/login", "webSocketDebuggerUrl": "ws://wrong-scheme"},
               {"url": "https://127.0.0.1:8443/login", "webSocketDebuggerUrl": "ws://wrong-port"}] + LOGIN_TAB
    connections, _ = _patch_browser(monkeypatch, targets, tab)

    result = browser_login.login(entry, 9310, 2)

    assert result.outcome == OUTCOME_LOGGED_IN
    assert connections == ["ws://login"]


def test_Login_NoTabOnAllowedOrigin_RefusedWithoutConnecting(entry, monkeypatch):
    connections, _ = _patch_browser(monkeypatch, [{"url": "https://127.0.0.1:9999/login", "webSocketDebuggerUrl": "ws://x"}],
                                    FakeTab([page(password=True)]))

    result = browser_login.login(entry, 9310, 2)

    assert result.outcome == OUTCOME_REFUSED
    assert "9999" in result.reason
    assert connections == []


def test_Login_EntryWithoutAddresses_Refused(store, monkeypatch):
    add_entry(store, name="nowhere", domains=())
    connections, _ = _patch_browser(monkeypatch, [], FakeTab([page()]))

    assert browser_login.login(store.get("nowhere"), 9310, 2).outcome == OUTCOME_REFUSED
    assert connections == []


@pytest.mark.parametrize("pages,timeout,outcome", [
    ([page(password=True, username=True), page(url="https://127.0.0.1/welcome")], 2, OUTCOME_LOGGED_IN),
    ([page(password=True, username=True)], 0.3, OUTCOME_FAILED),
    ([page(password=True, username=True), page(verification=True)], 2, OUTCOME_VERIFICATION),
    ([page(password=True, username=True, target_after_fill="http://127.0.0.1:9999/steal")], 2, OUTCOME_FAILED),
])
def test_Login_AnyOutcomeAfterTyping_CleansUpTheTab(entry, monkeypatch, pages, timeout, outcome):
    tab = FakeTab(pages)
    _patch_browser(monkeypatch, LOGIN_TAB, tab)

    result = browser_login.login(entry, 9310, timeout)

    assert result.outcome == outcome
    assert tab.cleaned == 1


def test_Login_RefusedBeforeTyping_LeavesTheTabAlone(entry, monkeypatch):
    tab = FakeTab([page(password=True, username=True, target="https://evil.test/steal")])
    _patch_browser(monkeypatch, LOGIN_TAB, tab)

    assert browser_login.login(entry, 9310, 2).outcome == OUTCOME_REFUSED
    assert tab.cleaned == 0


def test_Login_CleanUpNotConfirmedOnTheLoginConnection_IsRedoneOnAFreshConnection(entry, monkeypatch):
    # The review's case: the login connection drops after the password was delivered, so clean-up on it
    # cannot complete. It must be done again over a new connection to the same tab.
    tab = FakeTab([page(password=True, username=True), page(url="https://127.0.0.1/welcome")], confirmations=[False, True])
    connections, closed = _patch_browser(monkeypatch, LOGIN_TAB, tab)

    result = browser_login.login(entry, 9310, 2)

    assert result.outcome == OUTCOME_LOGGED_IN
    assert connections == ["ws://login", "ws://login"]
    assert tab.cleaned == 2 and closed == []


def test_Login_CleanUpNeverConfirmed_ClosesTheTab(entry, monkeypatch):
    tab = FakeTab([page(password=True, username=True), page(url="https://127.0.0.1/welcome")], confirmations=[False, False])
    _, closed = _patch_browser(monkeypatch, LOGIN_TAB, tab)

    browser_login.login(entry, 9310, 2)

    assert closed == ["T1"]


def test_Login_FreshConnectionCannotOpen_ClosesTheTab(entry, monkeypatch):
    tab = FakeTab([page(password=True, username=True), page(url="https://127.0.0.1/welcome")], confirmations=[False])
    _, closed = _patch_browser(monkeypatch, LOGIN_TAB, tab, fresh_connection_fails=True)

    browser_login.login(entry, 9310, 2)

    assert closed == ["T1"]


def test_Login_TabCannotBeMadeSafe_FailsTellingThePersonToCloseIt(entry, monkeypatch):
    tab = FakeTab([page(password=True, username=True), page(url="https://127.0.0.1/welcome")], confirmations=[False, False])
    _patch_browser(monkeypatch, LOGIN_TAB, tab, close_succeeds=False)

    with pytest.raises(CcSecretsError, match="Close that browser tab"):
        browser_login.login(entry, 9310, 2)


@pytest.mark.parametrize("failure", [CdpError("page changed"), ConnectionResetError("the tab closed")])
def test_Login_CrashWhileTypingPassword_CleansUpAndRaises(entry, monkeypatch, failure):
    tab = FakeTab([page(password=True, username=True)], fail_with=failure)
    _patch_browser(monkeypatch, LOGIN_TAB, tab)

    with pytest.raises(type(failure)):
        browser_login.login(entry, 9310, 2)
    assert tab.cleaned == 1
