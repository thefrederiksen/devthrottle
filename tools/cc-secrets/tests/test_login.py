"""The login decisions - which tab, the address checks, the steps, the outcome, the clean-up - against a
scripted tab. Driving real Chrome is proven by tests/live_leak_check.py, not here."""

import pytest

from conftest import add_entry
from src import browser_login
from src.browser_login import (CdpError, OUTCOME_FAILED, OUTCOME_LOGGED_IN, OUTCOME_REFUSED,
                               OUTCOME_VERIFICATION, _drive)


def page(url="https://127.0.0.1/login", password=False, username=False, verification=False, unreachable=False,
         target="https://127.0.0.1/session", target_after_fill=None):
    return {"url": url, "password": password, "username": username, "verification": verification,
            "unreachable": unreachable, "target": target, "target_after_fill": target_after_fill}


class FakeTab:
    """A tab that moves to its next page on every submit."""

    def __init__(self, pages, fail_fill=None, fail_with=None):
        self.pages = [dict(p) for p in pages]
        self.index = 0
        self.filled = []
        self.submitted = 0
        self.cleaned = 0
        self.typed = False
        self.fail_fill = fail_fill
        self.fail_with = fail_with

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

    def submit(self, object_id, expected_target):
        if self.current["target"] != expected_target:
            return False
        self.submitted += 1
        self.index = min(self.index + 1, len(self.pages) - 1)
        return True

    def clean_up(self):
        self.cleaned += 1


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


def test_PageWithoutFormTarget_IsFilledOnThePageOriginCheck(entry):
    tab = FakeTab([page(password=True, username=True, target=""), page(url="https://127.0.0.1/app")])

    assert _drive(tab, entry, entry.secret.reveal(), 2).outcome == OUTCOME_LOGGED_IN


def test_FormTargetChangesAfterTheCheck_NotSubmitted(entry):
    tab = FakeTab([page(password=True, username=True, target_after_fill="http://127.0.0.1:9999/steal")])

    result = _drive(tab, entry, entry.secret.reveal(), 2)

    assert result.outcome == OUTCOME_FAILED
    assert "destination changed" in result.reason
    assert tab.submitted == 0 and tab.typed is True


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


LOGIN_TAB = [{"url": "https://127.0.0.1/login", "webSocketDebuggerUrl": "ws://login"}]


def test_Login_PicksTheTabOnTheAllowedOrigin(entry, monkeypatch):
    tab = FakeTab([page(password=True, username=True), page(url="https://127.0.0.1/welcome")])
    targets = [{"url": "http://127.0.0.1/login", "webSocketDebuggerUrl": "ws://wrong-scheme"},
               {"url": "https://127.0.0.1:8443/login", "webSocketDebuggerUrl": "ws://wrong-port"}] + LOGIN_TAB
    connections = _patch_browser(monkeypatch, targets, tab)

    result = browser_login.login(entry, 9310, 2)

    assert result.outcome == OUTCOME_LOGGED_IN
    assert connections == ["ws://login"]


def test_Login_NoTabOnAllowedOrigin_RefusedWithoutConnecting(entry, monkeypatch):
    connections = _patch_browser(monkeypatch, [{"url": "https://127.0.0.1:9999/login", "webSocketDebuggerUrl": "ws://x"}],
                                 FakeTab([page(password=True)]))

    result = browser_login.login(entry, 9310, 2)

    assert result.outcome == OUTCOME_REFUSED
    assert "9999" in result.reason
    assert connections == []


def test_Login_EntryWithoutAddresses_Refused(store, monkeypatch):
    add_entry(store, name="nowhere", domains=())
    connections = _patch_browser(monkeypatch, [], FakeTab([page()]))

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


@pytest.mark.parametrize("failure", [CdpError("page changed"), ConnectionResetError("the tab closed")])
def test_Login_CrashWhileTypingPassword_CleansUpAndRaises(entry, monkeypatch, failure):
    tab = FakeTab([page(password=True, username=True)], fail_with=failure)
    _patch_browser(monkeypatch, LOGIN_TAB, tab)

    with pytest.raises(type(failure)):
        browser_login.login(entry, 9310, 2)
    assert tab.cleaned == 1
