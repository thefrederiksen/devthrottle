"""The approval-page reading shared by `license approve` and the daily approver check (`license check-approver`).

No test opens a browser, runs `claude auth login`, or reaches claude.ai. The pages are copies of ones recorded live,
with made-up names (fixtures/license_approval_pages.json). The browser is a fake that RUNS the very script
browser-harness would run, against such a page: so these tests prove the script itself, not a description of it, and
the fake fails the test on any click or typing it was not allowed.
"""

from __future__ import annotations

import contextlib
import io
import json
import sys
import time
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import license_signin  # noqa: E402
from src.cli import app  # noqa: E402
from src.license_signin import SignInError  # noqa: E402

ALICE = "alice@example.com"
BOB = "bob@example.com"
URL = "https://claude.ai/oauth/authorize?code=true&client_id=x&state=s1&login_hint=alice%40example.com"
CODE = "AUTHCODE-never-shown-aaaaaaaaaaaa#STATE-never-shown-bbbbbbbbbbbbb"
PAGES = json.loads((Path(__file__).parent / "fixtures" / "license_approval_pages.json").read_text(encoding="utf-8"))[
    "pages"]
runner = CliRunner()


def _first_json(text):
    # Click below 8.2 mixes standard error into stdout, so the JSON is found, not assumed.
    answer, _ = json.JSONDecoder().raw_decode(text[text.index("{"):])
    return answer


@pytest.fixture(autouse=True)
def no_waiting(monkeypatch):
    """The scripts sleep while a page settles; here time moves only when they sleep."""
    clock = [1000.0]
    monkeypatch.setattr(time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds))
    monkeypatch.setattr(time, "time", lambda: clock[0])


class FakeBrowser:
    """A browser-harness that runs the script it is given against recorded pages.

    ``frames`` are the pages the tab shows in turn, one per read, the last one staying; a click on Authorize, where
    it is allowed, turns the page into the one that shows the code. ``account`` is what claude.ai's account endpoint
    answers. Any click when ``clicks_allowed`` is False fails the test, and so does any helper this fake does not
    define - typing, key presses, file uploads - because the script would raise NameError."""

    def __init__(self, *frames, account=None, clicks_allowed=False, fails_with=None):
        self.frames = [PAGES[f] if isinstance(f, str) else f for f in frames]
        self.account = account
        self.clicks_allowed = clicks_allowed
        self.fails_with = fails_with
        self.reads = 0
        self.clicks = []
        self.opened = []
        self.closed = []
        self.focus = []
        self.runs = 0
        self.code_shown = False

    def _page(self):
        return self.frames[min(max(self.reads - 1, 0), len(self.frames) - 1)]

    def run(self, script, env, timeout):
        self.runs += 1
        if self.fails_with is not None:
            raise self.fails_with
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            exec(compile(script, "<browser step>", "exec"), self._helpers())
        return out.getvalue()

    def _helpers(self):
        def new_tab(url):
            self.opened.append(url)

        def js(expression):
            if "/api/account" in expression:
                return self.account
            if "querySelectorAll('button')" in expression:
                # Each look for the button starts one read: the tab moves on to its next page.
                self.reads += 1
                page = self._page()
                return None if self.code_shown or not page["authorize"] else [600, 400, 1200, 800]
            if "input,textarea" in expression:
                return [CODE] if self.code_shown else []
            if "visibilityState" in expression:
                return ["visible", True]
            if "innerText" in expression:
                return "Authentication code " + CODE if self.code_shown else self._page()["text"]
            raise AssertionError(f"the script ran JavaScript this fake does not know: {expression[:80]}")

        def page_info():
            return {"url": self._page()["address"]}

        def cdp(method, **params):
            if method == "Target.closeTarget":
                self.closed.append(params["targetId"])
            elif method == "Emulation.setFocusEmulationEnabled":
                self.focus.append(params["enabled"])
            else:
                raise AssertionError(f"the script sent a CDP command this fake does not allow: {method}")

        def click_at_xy(x, y):
            if not self.clicks_allowed:
                raise AssertionError(f"the script clicked at ({x}, {y}) on a page it must only read")
            self.clicks.append((x, y))
            self.code_shown = True

        return {"new_tab": new_tab, "current_tab": lambda: {"targetId": "tab-1"}, "wait_for_load": lambda: None,
                "js": js, "page_info": page_info, "cdp": cdp, "click_at_xy": click_at_xy}


class FakeProfile:
    """The Director's profile with identifier "work-chrome", shown as "Work Chrome"."""

    id = "work-chrome"
    name = "Work Chrome"

    def __init__(self):
        self.stopped = 0

    def environment(self):
        return {"BU_NAME": "work", "BU_CDP_URL": "http://127.0.0.1:1"}

    def stop(self):
        self.stopped += 1


class FakeLogin:
    """`claude auth login` into a folder. It writes a file there, as the real one does, so a folder left behind shows."""

    def __init__(self, folder, email, url_error=None):
        self.folder, self.email = Path(folder), email
        (self.folder / ".claude.json").write_text("{}", encoding="utf-8")
        self.url_error = url_error
        self.cancelled = 0
        self.finished = False

    def authorize_url(self, timeout_seconds):
        if self.url_error:
            raise SignInError(license_signin.LOGIN_FAILED, self.url_error)
        return "https://claude.ai/oauth/authorize?code=true&client_id=x&state=s1&login_hint=" + \
            self.email.replace("@", "%40")

    def finish(self, code, timeout_seconds):
        raise AssertionError("the check must never complete a login")

    def cancel(self):
        self.cancelled += 1


class Machine:
    """One check's fakes, and what each was asked to do."""

    def __init__(self, tmp_path, browser, url_error=None, open_error=None):
        self.browser = browser
        self.profile = FakeProfile()
        self.logins = []
        self.folders = []
        self._tmp = tmp_path
        self._url_error = url_error
        self._open_error = open_error

    def make_folder(self):
        folder = self._tmp / f"check-{len(self.folders)}"
        folder.mkdir()
        self.folders.append(folder)
        return str(folder)

    def start_login(self, folder, email):
        login = FakeLogin(folder, email, self._url_error)
        self.logins.append(login)
        return login

    def open_profile(self, name):
        if self._open_error is not None:
            raise self._open_error
        return self.profile

    def check(self, email=ALICE, profile="Work Chrome"):
        return license_signin.check_approver(profile, email, open_profile=self.open_profile,
                                             run_harness=self.browser.run, start_login=self.start_login,
                                             make_folder=self.make_folder)

    def assert_left_nothing(self):
        assert all(login.cancelled >= 1 for login in self.logins), "every throwaway login is cancelled"
        assert not any(login.finished for login in self.logins)
        assert all(not folder.exists() for folder in self.folders), "every temporary folder is deleted"
        if self._open_error is None:
            assert self.profile.stopped == 1, "the profile is put back as it was found"


# --- classify_page: every state, from a recorded page -----------------------------------------------------------


@pytest.mark.parametrize("page, state, shown", [
    ("consent", "can-approve", ALICE),
    ("consent-other-account", "wrong-account", BOB),
    ("signed-out", "signed-out", None),
    ("reauth-on-authorize", "needs-fresh-sign-in", ALICE),
    ("reauth-login", "needs-fresh-sign-in", ALICE),
    ("reauth-on-authorize-other-account", "wrong-account", BOB),
    ("consent-before-account-line", "check-failed", None),
    ("unknown", "check-failed", None),
])
def test_classifyPage_RecordedPage_SaysWhatItShows(page, state, shown):
    recorded = PAGES[page]

    answer = license_signin.classify_page(recorded["text"], recorded["address"], recorded["authorize"], ALICE)

    assert answer == (state, shown)


def test_classifyPage_AccountCaseAndTrailingFullStop_AreNotADifferentAccount():
    text = PAGES["consent"]["text"].replace("Logged in as alice@example.com", "Logged in as Alice@Example.com.")

    assert license_signin.classify_page(text, PAGES["consent"]["address"], True, ALICE)[0] == "can-approve"


def test_classifyPage_TheBrowserStepRunsTheSameFunction():
    # The approval and the check must never disagree about a page, so neither carries its own copy of the rules.
    for body in (license_signin._APPROVE_SCRIPT, license_signin._CHECK_SCRIPT):
        script = license_signin._page_script(body, URL, ALICE)
        assert script.startswith("def classify_page(")
        assert r"Logged in as\s" not in body and "recent sign-in" not in body, "the page rules live only in classify_page"


# --- check_approver ------------------------------------------------------------------------------------------------


@pytest.mark.parametrize("page, state, shown", [
    ("consent", "can-approve", ALICE),
    ("consent-other-account", "wrong-account", BOB),
    ("signed-out", "signed-out", None),
    ("reauth-on-authorize", "needs-fresh-sign-in", ALICE),
    ("reauth-login", "needs-fresh-sign-in", ALICE),
    ("unknown", "check-failed", None),
])
def test_checkApprover_RealApprovalPage_AnswersItsState_ClicksNothing_AndLeavesNothing(tmp_path, page, state, shown):
    machine = Machine(tmp_path, FakeBrowser(page))

    check = machine.check()

    assert (check.browser_profile, check.browser_profile_name, check.account_email, check.state,
            check.shown_account) == ("work-chrome", "Work Chrome", ALICE, state, shown)
    assert machine.browser.clicks == []
    assert machine.browser.opened == [machine.logins[0].authorize_url(0)], "the page is the throwaway login's own"
    assert machine.browser.closed == ["tab-1"] and machine.browser.focus == [True, False]
    assert [login.email for login in machine.logins] == [ALICE]
    machine.assert_left_nothing()


def test_checkApprover_ConsentPageStillLoading_WaitsForTheAccountLine(tmp_path):
    # Seen live: the Authorize button renders before "Logged in as". Read too early, that is not an answer.
    machine = Machine(tmp_path, FakeBrowser("consent-before-account-line", "consent-before-account-line", "consent"))

    assert machine.check().state == "can-approve"


def test_checkApprover_ASignInPageOnTheWay_IsNotTakenFromOneRead(tmp_path):
    # A negative answer needs two reads in a row; a page that passes through the sign-in page is not "signed out".
    machine = Machine(tmp_path, FakeBrowser("signed-out", "consent"))

    assert machine.check().state == "can-approve"


def test_checkApprover_NoEmail_ReadsTheSignedInAccountFirst_AndChecksThatOne(tmp_path):
    machine = Machine(tmp_path, FakeBrowser("consent", account={"status": 200, "email": "Alice@Example.com"}))

    check = machine.check(email=None)

    assert (check.account_email, check.state) == (ALICE, "can-approve")
    assert machine.browser.opened == ["https://claude.ai/", machine.logins[0].authorize_url(0)]
    assert machine.browser.closed == ["tab-1", "tab-1"], "the account tab is closed too"
    machine.assert_left_nothing()


@pytest.mark.parametrize("status", [401, 403])
def test_checkApprover_NoEmail_SignedOutProfile_IsSignedOut_AndNoLoginStarts(tmp_path, status):
    machine = Machine(tmp_path, FakeBrowser("consent", account={"status": status, "email": None}))

    check = machine.check(email=None)

    assert (check.account_email, check.shown_account, check.state) == (None, None, "signed-out")
    assert machine.logins == [] and machine.folders == []
    assert machine.profile.stopped == 1


def test_checkApprover_NoEmail_AccountEndpointSaysNeither_IsCheckFailed(tmp_path):
    machine = Machine(tmp_path, FakeBrowser("consent", account={"status": 500, "email": None}))

    check = machine.check(email=None)

    assert check.state == "check-failed" and "status 500" in check.detail
    assert machine.logins == []
    machine.assert_left_nothing()


def test_checkApprover_BrowserStepFails_IsCheckFailed_AndTheLoginAndFolderAreGone(tmp_path):
    browser = FakeBrowser(fails_with=SignInError(license_signin.APPROVAL_FAILED, "browser-harness failed (exit 1)"))
    machine = Machine(tmp_path, browser)

    check = machine.check()

    assert check.state == "check-failed" and "browser-harness failed" in check.detail
    assert len(machine.logins) == 1 and len(machine.folders) == 1
    machine.assert_left_nothing()


def test_checkApprover_UnexpectedCrash_StillCancelsTheLoginAndDeletesTheFolder(tmp_path):
    # Not a check result: the crash is raised. But nothing it opened may outlive it.
    machine = Machine(tmp_path, FakeBrowser(fails_with=RuntimeError("the browser went away")))

    with pytest.raises(RuntimeError):
        machine.check()

    machine.assert_left_nothing()


def test_checkApprover_LoginPrintsNoUrl_IsCheckFailed_AndNoBrowserStepRuns(tmp_path):
    machine = Machine(tmp_path, FakeBrowser("consent"), url_error="claude auth login printed no authorize URL")

    check = machine.check()

    assert check.state == "check-failed" and "no authorize URL" in check.detail
    assert machine.browser.runs == 0
    machine.assert_left_nothing()


def test_checkApprover_ProfileCannotBeStarted_IsCheckFailed_AndNoLoginStarts(tmp_path):
    error = SignInError(license_signin.APPROVAL_FAILED, 'no browser profile "work" on this machine')
    machine = Machine(tmp_path, FakeBrowser("consent"), open_error=error)

    check = machine.check()

    assert check.state == "check-failed" and "no browser profile" in check.detail
    assert (check.browser_profile, check.browser_profile_name) == ("Work Chrome", None), "only what was given"
    assert machine.logins == [] and machine.folders == []


@pytest.mark.parametrize("given", ["Work Chrome", "work-chrome"])
def test_checkApprover_GivenTheNameOrTheIdentifier_AnswersTheIdentifier_AndTheName(tmp_path, given):
    # Approver records match on the identifier exactly, so the check must never answer a display name there.
    machine = Machine(tmp_path, FakeBrowser("consent"))

    check = machine.check(profile=given)

    assert (check.browser_profile, check.browser_profile_name) == ("work-chrome", "Work Chrome")


def test_checkApprover_DetailIsTheToolsOwnSentence_NeverPageText(tmp_path):
    machine = Machine(tmp_path, FakeBrowser("reauth-on-authorize"))

    check = machine.check()

    assert check.detail == ('The browser profile "Work Chrome" is signed in to claude.ai, but the approval page for '
                            'alice@example.com asks for a fresh sign-in.')


def test_temporaryLoginFolder_IsANewEmptyFolderInTheTemporaryArea():
    folder = Path(license_signin._temporary_login_folder())
    try:
        assert folder.is_dir() and list(folder.iterdir()) == []
        assert folder.name.startswith("cc-license-check-")
        assert Path(license_signin.tempfile.gettempdir()) in folder.parents
    finally:
        folder.rmdir()


# --- approve_in_browser reads the page the same way ----------------------------------------------------------------


def test_approve_ConsentPage_ClicksAuthorize_AndReturnsTheCode():
    browser = FakeBrowser("consent", clicks_allowed=True)
    profile = FakeProfile()

    code = license_signin.approve_in_browser(URL, ALICE, "work", open_profile=lambda p: profile, run_harness=browser.run)

    assert code == CODE
    assert len(browser.clicks) == 1
    assert profile.stopped == 1 and browser.closed == ["tab-1"]


@pytest.mark.parametrize("page, kind", [
    ("reauth-on-authorize", license_signin.NEEDS_FRESH_SIGN_IN),
    ("reauth-login", license_signin.NEEDS_FRESH_SIGN_IN),
    ("signed-out", license_signin.NOT_SIGNED_IN),
    ("consent-other-account", license_signin.WRONG_ACCOUNT),
    ("unknown", license_signin.APPROVAL_FAILED),
])
def test_approve_PageThatCannotApprove_IsRefusedWithItsOwnReason_AndClicksNothing(page, kind):
    browser = FakeBrowser(page, clicks_allowed=False)
    profile = FakeProfile()

    with pytest.raises(SignInError) as refused:
        license_signin.approve_in_browser(URL, ALICE, "work", open_profile=lambda p: profile, run_harness=browser.run)

    assert refused.value.kind == kind
    assert browser.clicks == []
    assert profile.stopped == 1


def test_approve_ReauthPage_SaysWhatToDo():
    browser = FakeBrowser("reauth-on-authorize")

    with pytest.raises(SignInError) as refused:
        license_signin.approve_in_browser(URL, ALICE, "work", open_profile=lambda p: FakeProfile(),
                                          run_harness=browser.run)

    assert "asks for a fresh sign-in" in str(refused.value)
    assert refused.value.next_steps == ['cc-devthrottle browser signin "work"']


# --- the command ---------------------------------------------------------------------------------------------------


def _answer(state, email=ALICE):
    return license_signin.ApproverCheck("work-chrome", "Work Chrome", email, email, state, f"sentence for {state}")


@pytest.mark.parametrize("state", ["can-approve", "wrong-account", "signed-out", "needs-fresh-sign-in"])
def test_cli_checkApproverJson_AnyStateRead_ExitsZero_WithTheFiveFields(monkeypatch, state):
    asked = []
    monkeypatch.setattr(license_signin, "check_approver", lambda p, e: asked.append((p, e)) or _answer(state))

    result = runner.invoke(app, ["license", "check-approver", "--browser", "work", "--email", ALICE, "--json"])

    assert result.exit_code == 0, result.output
    assert _first_json(result.stdout) == {"browserProfile": "work-chrome", "browserProfileName": "Work Chrome",
                                          "accountEmail": ALICE, "shownAccount": ALICE, "state": state,
                                          "detail": f"sentence for {state}"}
    assert asked == [("work", ALICE)]


def test_cli_checkApproverJson_CheckFailed_ExitsOne_AndStillAnswersJson(monkeypatch):
    monkeypatch.setattr(license_signin, "check_approver", lambda p, e: _answer("check-failed"))

    result = runner.invoke(app, ["license", "check-approver", "--browser", "work", "--json"])

    assert result.exit_code == 1
    assert _first_json(result.stdout)["state"] == "check-failed"


def test_cli_checkApprover_NoEmail_AsksForDiscovery(monkeypatch):
    asked = []
    monkeypatch.setattr(license_signin, "check_approver", lambda p, e: asked.append((p, e)) or _answer("signed-out"))

    result = runner.invoke(app, ["license", "check-approver", "--browser", "work"])

    assert result.exit_code == 0, result.output
    assert asked == [("work", None)]
    assert "signed-out: sentence for signed-out" in result.stdout
    assert 'cc-devthrottle browser signin "work"' in result.stdout
