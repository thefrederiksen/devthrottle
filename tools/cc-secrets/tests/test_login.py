"""The login decisions - which tab, the domain check, the steps, the outcome, the clean-up - against a
scripted tab. Driving real Chrome is proven by tests/live_leak_check.py, not here."""

import pytest

from conftest import add_entry
from src import browser_login
from src.browser_login import (CdpError, OUTCOME_FAILED, OUTCOME_LOGGED_IN, OUTCOME_REFUSED,
                               OUTCOME_VERIFICATION, _drive)


def page(host="127.0.0.1", password=False, username=False, verification=False, unreachable=False):
    return {"host": host, "password": password, "username": username, "verification": verification,
            "unreachable": unreachable}


class FakeTab:
    """A tab that moves to its next page on every submit."""

    def __init__(self, pages, fail_fill=None):
        self.pages = pages
        self.index = 0
        self.filled = []
        self.submitted = 0
        self.cleared = 0
        self.fail_fill = fail_fill

    @property
    def current(self):
        return self.pages[self.index]

    def state(self):
        return {k: self.current[k] for k in ("password", "username", "verification")}

    def main_frame_host(self):
        return self.current["host"]

    def main_frame_unreachable(self):
        return self.current["unreachable"]

    def find(self, which):
        return f"{which}-{self.index}" if self.current[which] else None

    def fill(self, object_id, value, what):
        if self.fail_fill:
            raise CdpError(self.fail_fill.format(value=value))
        self.filled.append((what, value, self.index))

    def submit(self, object_id):
        self.submitted += 1
        self.index = min(self.index + 1, len(self.pages) - 1)

    def clear_passwords(self):
        self.cleared += 1


@pytest.fixture(autouse=True)
def fast_polls(monkeypatch):
    monkeypatch.setattr(browser_login, "POLL_SECONDS", 0.01)


@pytest.fixture
def entry(store):
    add_entry(store, domains=("127.0.0.1", "*.example.com"))
    return store.get("devlinux")


def test_OnePageForm_FillsBoth_SubmitsOnce_LogsIn(entry):
    tab = FakeTab([page(password=True, username=True), page()])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_LOGGED_IN
    assert [(w, i) for w, _, i in tab.filled] == [("username", 0), ("password", 0)]
    assert tab.submitted == 1


def test_UsernameThenPasswordPages_LogsIn(entry):
    tab = FakeTab([page(username=True), page(password=True), page()])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_LOGGED_IN
    assert [(w, i) for w, _, i in tab.filled] == [("username", 0), ("password", 1)]


def test_PageOnDisallowedHost_RefusedAndNothingTyped(entry):
    tab = FakeTab([page(host="evil.test", password=True, username=True)])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_REFUSED
    assert "evil.test" in result.reason
    assert tab.filled == []


def test_SecondStepMovesToDisallowedHost_PasswordNeverTyped(entry):
    tab = FakeTab([page(username=True), page(host="login.evil.test", password=True)])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_REFUSED
    assert [w for w, _, _ in tab.filled] == ["username"]


def test_WildcardSubdomain_IsAllowed(entry):
    tab = FakeTab([page(host="login.example.com", password=True, username=True), page(host="app.example.com")])

    assert _drive(tab, entry, entry.secret.reveal(), 2).outcome == OUTCOME_LOGGED_IN


def test_VerificationAfterPassword_ReportsVerification(entry):
    tab = FakeTab([page(password=True, username=True), page(verification=True)])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_VERIFICATION


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
    tab = FakeTab([page()])

    assert _drive(tab, entry, entry.secret.reveal(), 1).outcome == OUTCOME_FAILED


def _patch_browser(monkeypatch, targets, tab):
    connections = []
    monkeypatch.setattr(browser_login, "list_page_targets", lambda port: targets)

    class FakeConnection:
        def __init__(self, url):
            connections.append(url)

        def close(self):
            pass

    monkeypatch.setattr(browser_login, "CdpConnection", FakeConnection)
    monkeypatch.setattr(browser_login, "_Tab", lambda conn, entry: tab)
    return connections


def test_Login_PicksTheTabOnTheAllowedDomain(entry, monkeypatch):
    tab = FakeTab([page(password=True, username=True), page()])
    targets = [{"url": "https://news.test/", "webSocketDebuggerUrl": "ws://news"},
               {"url": "http://127.0.0.1:5000/login", "webSocketDebuggerUrl": "ws://login"}]
    connections = _patch_browser(monkeypatch, targets, tab)

    result = browser_login.login(entry, 9310, 2)

    assert result.outcome == OUTCOME_LOGGED_IN
    assert connections == ["ws://login"]
    assert tab.cleared == 0


def test_Login_NoTabOnAllowedDomain_RefusedWithoutConnecting(entry, monkeypatch):
    tab = FakeTab([page(password=True)])
    connections = _patch_browser(monkeypatch, [{"url": "http://localhost:5000/login", "webSocketDebuggerUrl": "ws://x"}], tab)

    result = browser_login.login(entry, 9310, 2)

    assert result.outcome == OUTCOME_REFUSED
    assert "localhost" in result.reason
    assert connections == []


def test_Login_EntryWithoutDomains_Refused(store, monkeypatch):
    add_entry(store, name="nodomains", domains=())
    connections = _patch_browser(monkeypatch, [], FakeTab([page()]))

    result = browser_login.login(store.get("nodomains"), 9310, 2)

    assert result.outcome == OUTCOME_REFUSED
    assert connections == []


def test_Login_NotLoggedIn_ClearsPasswordFields(entry, monkeypatch):
    tab = FakeTab([page(password=True, username=True)])
    _patch_browser(monkeypatch, [{"url": "http://127.0.0.1/login", "webSocketDebuggerUrl": "ws://login"}], tab)

    result = browser_login.login(entry, 9310, 0.3)

    assert result.outcome == OUTCOME_FAILED
    assert tab.cleared == 1


def test_Login_BrowserErrorWhileFilling_ClearsAndRaises(entry, monkeypatch):
    tab = FakeTab([page(password=True, username=True)], fail_fill="page changed")
    _patch_browser(monkeypatch, [{"url": "http://127.0.0.1/login", "webSocketDebuggerUrl": "ws://login"}], tab)

    with pytest.raises(CdpError):
        browser_login.login(entry, 9310, 2)
    assert tab.cleared == 1
