"""The real _Tab clean-up, against a scripted debug connection: it must confirm what it did, and it must
not leave the password in the tab's address or history (review of pull request 2891 at 20ebfb56)."""

import pytest

from conftest import add_entry
from src import browser_login
from src.browser_login import _Tab


class ScriptedConnection:
    """Answers the debug-protocol calls _Tab makes, with a page whose state the test decides."""

    def __init__(self, address="https://127.0.0.1/welcome", password_left=False, history=None,
                 address_after_navigate="about:blank", fail_on=None):
        self.address = address
        self.password_left = password_left
        self.history = list(history if history is not None else [address])
        self.address_after_navigate = address_after_navigate
        self.fail_on = fail_on
        self.calls = []
        self.cleared = 0

    def call(self, method, params=None):
        params = params or {}
        expression = params.get("expression", "")
        self.calls.append(method if method != "Runtime.evaluate" else f"evaluate:{expression[:24]}")
        if self.fail_on and self.fail_on in (method, f"evaluate:{expression[:24]}"):
            raise ConnectionResetError("the connection dropped")
        if method == "Page.getFrameTree":
            return {"frameTree": {"frame": {"id": "F", "loaderId": "L", "url": self.address}}}
        if method == "Page.createIsolatedWorld":
            return {"executionContextId": 7}
        if method == "Page.resetNavigationHistory":
            self.history = [self.address]
            return {}
        if method == "Page.getNavigationHistory":
            return {"currentIndex": len(self.history) - 1, "entries": [{"url": u} for u in self.history]}
        if method == "Page.navigate":
            self.address = self.address_after_navigate
            self.history = [self.address]
            return {}
        if method == "Runtime.evaluate":
            if "setter.call(e, '')" in expression:
                self.cleared += 1
                self.password_left = False
                return {"result": {"value": 1}}
            if "some(e => e.value" in expression:
                return {"result": {"value": self.password_left}}
            # Written out rather than taken from the module, so this test means the same thing when run
            # against a version that does not read the address at all.
            if expression == "location.href":
                return {"result": {"value": self.address}}
        return {}

    def close(self):
        pass


@pytest.fixture
def entry(store):
    add_entry(store, domains=("https://127.0.0.1",))
    return store.get("devlinux")


@pytest.fixture(autouse=True)
def fast_polls(monkeypatch):
    monkeypatch.setattr(browser_login, "POLL_SECONDS", 0.01)


def test_CleanUp_OnAnOrdinaryPage_IsConfirmed(entry):
    conn = ScriptedConnection(password_left=True)

    assert _Tab(conn, entry).clean_up_confirmed() is True
    assert conn.cleared == 1


def test_CleanUp_PasswordFieldStillHoldsAValue_IsNotConfirmed(entry, monkeypatch):
    conn = ScriptedConnection()
    # A page that refuses to be cleared: the field still holds a value after clearing.
    monkeypatch.setattr(conn, "call", _keep_password(conn))

    assert _Tab(conn, entry).clean_up_confirmed() is False


def _keep_password(conn):
    original = ScriptedConnection.call

    def call(method, params=None):
        params = params or {}
        if method == "Runtime.evaluate" and "some(e => e.value" in params.get("expression", ""):
            return {"result": {"value": True}}
        return original(conn, method, params)
    return call


def test_CleanUp_SecretInTheAddress_LeavesThePageAndClearsIt(entry):
    # The review's case: a GET submission the page forced puts the password in the current address, which
    # is the one history entry a reset leaves behind.
    secret = entry.secret.reveal()
    conn = ScriptedConnection(address=f"https://127.0.0.1/welcome?password={secret}")

    confirmed = _Tab(conn, entry).clean_up_confirmed()

    assert confirmed is True
    assert "Page.navigate" in conn.calls
    assert secret not in conn.address
    assert all(secret not in url for url in conn.history)


def test_CleanUp_AddressCannotBeCleared_IsNotConfirmed(entry):
    secret = entry.secret.reveal()
    conn = ScriptedConnection(address=f"https://127.0.0.1/welcome?password={secret}",
                              address_after_navigate=f"https://127.0.0.1/still?password={secret}")

    assert _Tab(conn, entry).clean_up_confirmed() is False


def test_CleanUp_ConnectionDrops_IsNotConfirmed(entry):
    conn = ScriptedConnection(fail_on="Page.resetNavigationHistory")

    assert _Tab(conn, entry).clean_up_confirmed() is False
