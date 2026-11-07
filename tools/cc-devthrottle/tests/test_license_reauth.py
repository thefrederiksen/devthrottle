"""Signing a browser profile in to claude.ai again from the account's own mailbox (`license reauth`).

No test opens a browser, reaches claude.ai or reads a real mailbox. The browser is a fake that RUNS the very scripts
browser-harness would run, against a made-up sign-in page that behaves the way the real one did by hand on 6 November
2026 - including the page that keeps another account in its email field. The mailbox is a fake with mail dated as
the test says, and the mail tools are fakes that print what cc-gmail and cc-outlook print.
"""

from __future__ import annotations

import contextlib
import datetime as dt
import io
import json
import logging
import sys
import time
import traceback
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import license_reauth, license_signin  # noqa: E402
from src.cli import app  # noqa: E402
from src.license_reauth import ReauthError  # noqa: E402
from src.license_signin import SignInError  # noqa: E402

ALICE = "alice@example.com"
BOB = "bob@example.com"
LINK = "https://claude.ai/magic-link#never-shown-link-aaaaaaaaaaaa:alice"
OLD_LINK = "https://claude.ai/magic-link#an-older-request-bbbbbbbbbbb:alice"
CODE = "482913"
OLD_CODE = "111111"
START = dt.datetime(2026, 11, 6, 20, 0, 0, tzinfo=dt.timezone.utc)
runner = CliRunner()

# Where the fake page lays out each thing a script clicks.
EMAIL_AT, CONTINUE_AT, CODE_AT, VERIFY_AT = (100, 100), (100, 200), (100, 300), (100, 400)


@pytest.fixture
def clock(monkeypatch):
    """Time moves only when something sleeps: the scripts' time module and the reauth's own clock alike."""
    moment = [1000.0]
    monkeypatch.setattr(time, "sleep", lambda seconds: moment.__setitem__(0, moment[0] + seconds))
    monkeypatch.setattr(time, "time", lambda: moment[0])
    return lambda: START + dt.timedelta(seconds=moment[0] - 1000.0)


class SignInPage:
    """claude.ai's sign-in flow in one browser profile, as the fake browser shows it.

    ``remembered`` is the account the page keeps in its email field: with ``sticky`` it ignores being cleared and
    typed over (the trap of 6 November). ``codes`` maps each sign-in link to the code its page shows."""

    def __init__(self, remembered=None, sticky=False, codes=None, takes_code=True):
        self.value = remembered or ""
        self.sticky = sticky
        self.selected = False
        self.requested = False
        self.verified = False
        self.typed = None
        self.codes = codes if codes is not None else {LINK: CODE}
        self.takes_code = takes_code


class FakeBrowser:
    """A browser-harness that runs the script it is given against ``page``. It fails the test on any helper or
    JavaScript it does not know, and records every tab, click and key."""

    def __init__(self, page):
        self.page = page
        self.tabs = {}
        self.current = None
        self.next_tab = 0
        self.clicks = []
        self.keys = []
        self.closed = []
        self.runs = 0
        self.printed = []

    def run(self, script, env, timeout):
        self.runs += 1
        out = io.StringIO()
        try:
            with contextlib.redirect_stdout(out):
                exec(compile(script, "<browser step>", "exec"), self._helpers())
        except Exception:
            raise SignInError(license_signin.APPROVAL_FAILED, "browser-harness failed (exit 1): "
                              + license_signin.harness_reason(traceback.format_exc())) from None
        self.printed.append(out.getvalue())
        return out.getvalue()

    def _url(self):
        return self.tabs.get(self.current, "")

    def _on_login(self):
        return self._url().startswith("https://claude.ai/login")

    def _helpers(self):
        page = self.page

        def new_tab(url):
            self.next_tab += 1
            target = f"tab-{self.next_tab}"
            self.tabs[target] = url
            self.current = target
            return target

        def current_tab():
            return {"targetId": self.current, "url": self._url()}

        def switch_tab(target):
            assert target in self.tabs, f"switched to a tab that is not open: {target}"
            self.current = target

        def cdp(method, **params):
            if method == "Target.closeTarget":
                self.closed.append(params["targetId"])
                self.tabs.pop(params["targetId"], None)
            elif method == "Emulation.setFocusEmulationEnabled":
                pass
            elif method == "Input.insertText":
                assert self._on_login(), "typed outside the sign-in page"
                if not page.sticky:
                    page.value += params["text"]
            else:
                raise AssertionError(f"the script sent a CDP command this fake does not allow: {method}")

        def press_key(key):
            self.keys.append(key)
            if key == "Backspace" and page.selected and not page.sticky:
                page.value = ""
            page.selected = False

        def type_text(text):
            assert self._on_login() and page.requested, "typed the code outside the code field"
            page.typed = text

        def click_at_xy(x, y):
            self.clicks.append((x, y))
            if (x, y) == CONTINUE_AT:
                page.requested = True
            elif (x, y) == VERIFY_AT and page.takes_code and page.typed is not None:
                page.verified = page.typed == page.codes.get(page.link_opened)

        def js(expression):
            if expression == "document.readyState":
                return "complete"
            if "getBoundingClientRect" in expression:
                login = self._on_login()
                if "Continue with email" in expression:
                    return list(CONTINUE_AT) if login and not page.requested else None
                if "Verify email address" in expression:
                    return list(VERIFY_AT) if login and page.requested and not page.verified else None
                if "getElementById('email')" in expression:
                    return list(EMAIL_AT) if login and not page.requested else None
                if "getElementById('code')" in expression:
                    return list(CODE_AT) if login and page.requested and not page.verified else None
            if expression == "document.getElementById('email').select()":
                page.selected = True
                return None
            if "getElementById('email')||{}).value" in expression:
                return page.value
            if expression == "!!document.getElementById('code')":
                return page.requested and not page.verified
            if "innerText" in expression:
                url = self._url()
                if url.startswith("https://claude.ai/magic-link#"):
                    page.link_opened = url
                    shown = page.codes.get(url)
                    return f"Your verification code is {shown}" if shown else "This link has expired"
                return "Sign in"
            raise AssertionError(f"the script ran JavaScript this fake does not know: {expression[:80]}")

        return {"new_tab": new_tab, "current_tab": current_tab, "switch_tab": switch_tab, "cdp": cdp,
                "press_key": press_key, "type_text": type_text, "click_at_xy": click_at_xy, "js": js,
                "page_info": lambda: {"url": self._url()}}


SignInPage.link_opened = None


class FakeMailbox:
    """Sign-in mails: (arrives after this many seconds of reading, dated, link). It answers the way a real mailbox
    does: the newest mail dated at or after the time asked about, once that mail has arrived."""

    def __init__(self, clock, mails):
        self._clock = clock
        self._mails = mails
        self.asked = []

    def sign_in_link(self, account, since):
        self.asked.append((account, since))
        arrived = [(dated, link) for after, dated, link in self._mails
                   if self._clock() >= START + dt.timedelta(seconds=after) and dated >= since]
        return max(arrived)[1] if arrived else None


class FakeProfile:
    id = "alice-chrome"
    name = "Alice Chrome"

    def __init__(self):
        self.stopped = 0

    def environment(self):
        return {"BU_NAME": "alice-chrome", "BU_CDP_URL": "http://127.0.0.1:1"}

    def stop(self):
        self.stopped += 1


def _confirmed(state="can-approve"):
    return lambda profile, account: license_signin.ApproverCheck(
        "alice-chrome", "Alice Chrome", account, account, state, f"sentence for {state}")


def _reauth(browser, mailbox, clock, check=None, profile=None):
    return license_reauth.reauth(ALICE, "Alice Chrome", mailbox, open_profile=lambda p: profile or FakeProfile(),
                                 run_harness=browser.run, check=check or _confirmed(), now=clock,
                                 sleep=time.sleep)


# --- the flow ------------------------------------------------------------------------------------------------------


def test_reauth_MailArrives_TypesItsCodeIntoTheFirstTab_AndConfirmsWithTheCheck(clock, caplog):
    caplog.set_level(logging.DEBUG)
    page = SignInPage(remembered=ALICE)
    browser = FakeBrowser(page)
    mailbox = FakeMailbox(clock, [(10, START + dt.timedelta(seconds=3), LINK)])
    profile = FakeProfile()

    result = _reauth(browser, mailbox, clock, profile=profile)

    assert (result.account_email, result.browser_profile, result.check.state) == (ALICE, "alice-chrome", "can-approve")
    assert page.typed == CODE and page.verified
    assert browser.tabs == {}, "both tabs are closed"
    assert profile.stopped == 1
    assert mailbox.asked[0] == (ALICE, START), "the mail must be dated after the time noted before the page opened"
    shown = "".join(browser.printed) + caplog.text + json.dumps(result.to_json())
    assert LINK not in shown and CODE not in shown, "the link and the code are never printed or logged"


def test_reauth_TheFieldIsClearedByDeletingItsText_ThenTheAccountIsTyped(clock):
    # The page pre-fills an account the browser remembers; it is selected and deleted, never typed after.
    page = SignInPage(remembered=BOB)
    browser = FakeBrowser(page)

    _reauth(browser, FakeMailbox(clock, [(0, START, LINK)]), clock)

    assert page.value == ALICE
    assert browser.keys[:2] == ["Backspace", "Escape"]


def test_reauth_ThePagePrefillsAnotherAccountAndKeepsIt_RefusesBeforePressingAnything(clock):
    page = SignInPage(remembered=BOB, sticky=True)
    browser = FakeBrowser(page)
    mailbox = FakeMailbox(clock, [(0, START, LINK)])

    with pytest.raises(ReauthError) as refused:
        _reauth(browser, mailbox, clock)

    assert (refused.value.kind, refused.value.step) == (license_reauth.PREFILLED_OTHER_ACCOUNT, "request")
    assert not page.requested and CONTINUE_AT not in browser.clicks, "no code was asked for"
    assert mailbox.asked == [], "no mailbox is read"
    assert BOB not in str(refused.value), "the other account is never named"
    assert browser.tabs == {}


def test_reauth_AMailThatArrivedBeforeTheRequest_IsIgnored_AndTheNewOneIsUsed(clock):
    page = SignInPage(remembered=ALICE, codes={LINK: CODE, OLD_LINK: OLD_CODE})
    mailbox = FakeMailbox(clock, [(0, START - dt.timedelta(minutes=5), OLD_LINK),
                                  (30, START + dt.timedelta(seconds=20), LINK)])

    _reauth(FakeBrowser(page), mailbox, clock)

    assert page.typed == CODE, "the code of the mail that answers this request"
    assert len(mailbox.asked) > 1, "the old mail was there at once and was not taken"


def test_reauth_OnlyAMailFromBeforeTheRequest_FailsAtTheMailStep_AndTypesNothing(clock):
    page = SignInPage(remembered=ALICE, codes={OLD_LINK: OLD_CODE})
    mailbox = FakeMailbox(clock, [(0, START - dt.timedelta(seconds=1), OLD_LINK)])

    with pytest.raises(ReauthError) as failed:
        _reauth(FakeBrowser(page), mailbox, clock)

    assert (failed.value.kind, failed.value.step) == (license_reauth.REAUTH_FAILED, "mail")
    assert "no sign-in mail" in str(failed.value)
    assert page.typed is None


def test_reauth_TheCodeIsNotTaken_FailsAtTheCodeStep(clock):
    page = SignInPage(remembered=ALICE, takes_code=False)

    with pytest.raises(ReauthError) as failed:
        _reauth(FakeBrowser(page), FakeMailbox(clock, [(0, START, LINK)]), clock)

    assert failed.value.step == "code" and "still asked for the code" in str(failed.value)


def test_reauth_TheLinkPageShowsNoCode_FailsAtTheCodeStep_AndTypesNothing(clock):
    page = SignInPage(remembered=ALICE, codes={})

    with pytest.raises(ReauthError) as failed:
        _reauth(FakeBrowser(page), FakeMailbox(clock, [(0, START, LINK)]), clock)

    assert failed.value.step == "code" and "no 6-digit code" in str(failed.value)
    assert page.typed is None


def test_reauth_TheCheckAfterwardsCannotApprove_FailsAtTheConfirmStep(clock):
    with pytest.raises(ReauthError) as failed:
        _reauth(FakeBrowser(SignInPage(remembered=ALICE)), FakeMailbox(clock, [(0, START, LINK)]), clock,
                check=_confirmed("needs-fresh-sign-in"))

    assert failed.value.step == "confirm" and "needs-fresh-sign-in" in str(failed.value)


def test_reauth_TheBrowserStepCrashes_SaysTheStepAndTheReason_AndPutsTheProfileBack(clock):
    browser = FakeBrowser(SignInPage(remembered=ALICE))
    browser.run = lambda script, env, timeout: (_ for _ in ()).throw(
        SignInError(license_signin.APPROVAL_FAILED, "browser-harness failed (exit 1): RuntimeError: boom"))
    profile = FakeProfile()

    with pytest.raises(ReauthError) as failed:
        _reauth(browser, FakeMailbox(clock, []), clock, profile=profile)

    assert failed.value.step == "request" and "RuntimeError: boom" in str(failed.value)
    assert profile.stopped == 1


def test_loginUrl_NamesTheAccountItAsksFor():
    assert license_reauth.login_url(" Alice@Example.com ") == \
        "https://claude.ai/login?reauth=1&email=alice%40example.com"


# --- the mailboxes -------------------------------------------------------------------------------------------------


class FakeTool:
    """cc-gmail or cc-outlook: answers each command by its verb and records what it was asked."""

    def __init__(self, answers):
        self.answers = answers
        self.asked = []

    def __call__(self, args, timeout):
        self.asked.append(args)
        if args[3] in self.answers:
            return self.answers[args[3]]
        raise AssertionError(f"the mail tool was asked something this fake does not answer: {args}")


def _gmail_row(message_id, subject, date, to=ALICE):
    return {"id": message_id, "thread_id": "t", "from": "Anthropic <no-reply@example.com>", "to": to,
            "subject": subject, "date": date, "labels": ["INBOX"]}


def test_gmailMailbox_TakesTheNewestSecureLinkMailForTheAccountSinceTheRequest():
    since = dt.datetime(2026, 11, 7, 1, 0, 0, tzinfo=dt.timezone.utc)
    rows = [
        _gmail_row("new", "Your secure link to Claude is here", "Fri, 06 Nov 2026 17:00:30 -0800"),
        _gmail_row("old", "Your secure link to Claude is here", "Fri, 06 Nov 2026 16:59:00 -0800"),
        _gmail_row("other", "Your secure link to Claude is here", "Fri, 06 Nov 2026 17:00:40 -0800", to=BOB),
        _gmail_row("news", "Changes to usage credits", "Fri, 06 Nov 2026 17:00:50 -0800"),
    ]
    tool = FakeTool({"list": json.dumps(rows), "read": json.dumps({"id": "new", "body": f"Sign in: {LINK}\n"})})

    link = license_reauth.GmailMailbox("alice-mail", tool).sign_in_link(ALICE, since)

    assert link == LINK
    assert tool.asked[1] == ["cc-gmail", "-a", "alice-mail", "read", "new", "--json"]


def test_gmailMailbox_TheJsonBodyCutsTheLink_ItIsTakenFromTheRawMail():
    since = dt.datetime(2026, 11, 7, 1, 0, 0, tzinfo=dt.timezone.utc)
    rows = [_gmail_row("new", "Your secure link to Claude is here", "Fri, 06 Nov 2026 17:00:30 -0800")]
    asked = []

    def tool(args, timeout):
        asked.append(args[-1])
        if args[3] == "list":
            return json.dumps(rows)
        return json.dumps({"body": "Sign in: https://claude.ai/magic-"}) if args[-1] == "--json" else f"raw {LINK}"

    assert license_reauth.GmailMailbox("alice-mail", tool).sign_in_link(ALICE, since) == LINK
    assert asked == ["--json", "--json", "--raw"]


def test_gmailMailbox_NothingSinceTheRequest_IsNone_AndNothingIsRead():
    since = dt.datetime(2026, 11, 7, 2, 0, 0, tzinfo=dt.timezone.utc)
    rows = [_gmail_row("old", "Your secure link to Claude is here", "Fri, 06 Nov 2026 17:00:30 -0800")]
    tool = FakeTool({"list": json.dumps(rows)})

    assert license_reauth.GmailMailbox("alice-mail", tool).sign_in_link(ALICE, since) is None
    assert len(tool.asked) == 1


OUTLOOK_LIST = """
Messages in inbox (mailbox)

[*]
AAMkAFAKEFAKEFAKEMADEUPMESSAGEIDFORTESTSONLYxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
M8FakeSecondLineOfTheIdForTestsOnly_AAAArAAA=
    From: Claude Team
    Subject: Changes to usage credits and your auto-reload
    Date: 2026-11-06T16:57:36-05:00

[*]
AAMkAFAKEFAKEFAKEMADEUPMESSAGEIDFORTESTSONLYxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
M8FakeSecondLineOfTheIdForTestsOnly_AAAAqAAA=
    From: Anthropic
    Subject: Your secure link to Claude is here | 2026-11-06 16:45:56
    Date: 2026-11-06T16:45:59-05:00
"""


def test_outlookMailbox_ReadsTheWrappedIdOfTheSecureLinkMail_AndItsRawBody():
    since = dt.datetime(2026, 11, 6, 21, 45, 0, tzinfo=dt.timezone.utc)
    tool = FakeTool({"list": OUTLOOK_LIST, "read": f"<a href=\"{LINK}\">Sign in</a>"})

    assert license_reauth.OutlookMailbox("alice-mail", tool).sign_in_link(ALICE, since) == LINK
    assert tool.asked[1] == ["cc-outlook", "-a", "alice-mail", "read",
                             "AAMkAFAKEFAKEFAKEMADEUPMESSAGEIDFORTESTSONLYxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxM8FakeSecondLineOfTheIdForTestsOnly_AAAAqAAA=",
                             "--raw"]


def test_outlookMailbox_TheSecureLinkMailIsFromBeforeTheRequest_IsNone():
    since = dt.datetime(2026, 11, 6, 21, 46, 0, tzinfo=dt.timezone.utc)
    tool = FakeTool({"list": OUTLOOK_LIST})

    assert license_reauth.OutlookMailbox("alice-mail", tool).sign_in_link(ALICE, since) is None


def test_mailbox_TheMailHoldsNoLink_FailsAtTheMailStep():
    since = dt.datetime(2026, 11, 6, 21, 45, 0, tzinfo=dt.timezone.utc)
    tool = FakeTool({"list": OUTLOOK_LIST, "read": "no link here"})

    with pytest.raises(ReauthError) as failed:
        license_reauth.OutlookMailbox("alice-mail", tool).sign_in_link(ALICE, since)

    assert failed.value.step == "mail"


# --- the configuration and the command -----------------------------------------------------------------------------


class _Store:
    def __init__(self, folder):
        self.store_dir = str(folder)


def test_setMailbox_ThenMailboxFor_ReadsItBack_ByAccountInAnyCase(tmp_path):
    store = _Store(tmp_path)

    license_reauth.set_mailbox(store, "Alice@Example.com", "outlook", "alice-mail")

    assert license_reauth.mailbox_for(store, ALICE) == license_reauth.MailboxSetting("outlook", "alice-mail")


def test_setMailbox_AKindThatIsNotAMailbox_IsRefused(tmp_path):
    with pytest.raises(ReauthError):
        license_reauth.set_mailbox(_Store(tmp_path), ALICE, "pigeon", "alice-mail")


def test_mailboxFor_NoneSet_IsNoMailbox_WithTheCommandThatSetsOne(tmp_path):
    with pytest.raises(ReauthError) as missing:
        license_reauth.mailbox_for(_Store(tmp_path), ALICE)

    assert missing.value.kind == license_reauth.NO_MAILBOX
    assert any("license set-mailbox" in step for step in missing.value.next_steps)


@pytest.fixture
def machine(tmp_path, monkeypatch):
    store_dir = tmp_path / "store"
    default_dir = tmp_path / "claude"
    default_dir.mkdir()
    return ["--store-dir", str(store_dir), "--default-config-dir", str(default_dir)]


def _run(machine, *args):
    return runner.invoke(app, ["license", *args, *machine])


def _first_json(text):
    answer, _ = json.JSONDecoder().raw_decode(text[text.index("{"):])
    return answer


def test_cli_reauth_NoMailboxSet_ExitsOne_AndAnswersTheReason(machine):
    assert _run(machine, "set-browser", ALICE, "Alice Chrome").exit_code == 0

    result = _run(machine, "reauth", ALICE, "--json")

    assert result.exit_code == 1
    assert _first_json(result.stdout) == {"accountEmail": ALICE, "result": "failed", "reason": "no-mailbox", "step": None}


def test_cli_reauth_Failed_NamesTheStep(machine, monkeypatch):
    assert _run(machine, "set-browser", ALICE, "Alice Chrome").exit_code == 0
    assert _run(machine, "set-mailbox", ALICE, "--kind", "gmail", "--mail-account", "alice-mail").exit_code == 0

    def failing(account, profile, mailbox):
        raise ReauthError(license_reauth.REAUTH_FAILED, "mail", "no sign-in mail arrived")

    monkeypatch.setattr(license_reauth, "reauth", failing)

    result = _run(machine, "reauth", ALICE, "--json")

    assert result.exit_code == 1
    assert _first_json(result.stdout) == {"accountEmail": ALICE, "result": "failed", "reason": "reauth-failed",
                                          "step": "mail"}
    assert "at the mail step" in result.output


def test_cli_reauth_SignedInAgain_AnswersTheCheck_AndNeitherLinkNorCode(machine, monkeypatch):
    assert _run(machine, "set-browser", ALICE, "Alice Chrome").exit_code == 0
    assert _run(machine, "set-mailbox", ALICE, "--kind", "outlook", "--mail-account", "alice-mail").exit_code == 0
    seen = []

    def signed_in(account, profile, mailbox):
        seen.append((account, profile, type(mailbox).__name__))
        return license_reauth.ReauthResult(account, "alice-chrome", "Alice Chrome",
                                           _confirmed()(profile, account))

    monkeypatch.setattr(license_reauth, "reauth", signed_in)

    result = _run(machine, "reauth", ALICE, "--json")

    assert result.exit_code == 0, result.output
    answer = _first_json(result.stdout)
    assert (answer["result"], answer["check"]["state"]) == ("signed-in-again", "can-approve")
    assert seen == [(ALICE, "Alice Chrome", "OutlookMailbox")]
    assert LINK not in result.output and CODE not in result.output
