"""Signing a browser profile in to claude.ai AGAIN, from the account's own mailbox (``license reauth``).

For the mission "Licenses - Always Ready, Loud When Not", step 4. claude.ai regularly asks a profile that is signed in
for a fresh sign-in on the approval page (``needs-fresh-sign-in``), and until now only a person fixed that. The owner's
ruling: "Yes, automate it. The fleet asks claude.ai for the sign-in code, reads it from the account's own mailbox
(Gmail for some accounts, Outlook for others) and types it in."

THE FLOW, as done by hand on 17 and 21 October and 6 November 2026, in four steps. Each failure says its step.

1. ``request``: open ``claude.ai/login?reauth=1&email=<account>`` in the account's own profile, CLEAR the email
   field (select its text and delete it - Ctrl+A did not clear it), type the account, and refuse unless the field
   holds exactly that account. THE TRAP, seen 6 November: the page pre-filled a DIFFERENT account the browser
   remembered and ignored ``email=``, so the code went to the wrong mailbox. Then press "Continue with email". The
   time is noted BEFORE the page is opened, so any mail dated earlier answers an earlier request.
2. ``mail``: read the newest "Your secure link to Claude" mail that arrived after that time and is ADDRESSED TO the
   account, from the mailbox the machine's owner configured for it (``license set-mailbox``; never in this
   repository), and take the ``claude.ai/magic-link#...`` address from the mail's body as the mail tool answers it in
   JSON - whole, never from text a console printed, which folds it at 80 columns and cuts the address.
3. ``code``: open the link in a SECOND tab of the same profile, read the 6-digit code it shows, type it into the
   first tab's verification field and press "Verify email address" with real clicks (focus emulation on, or the click
   does nothing). Nothing is evaluated while the page navigates after that click.
4. ``confirm``: run the approver check (``license_signin.check_approver``): only ``can-approve`` is success.

NOTHING SECRET IS SHOWN. The link and the code are never printed, logged or put in an error: the link travels from
the mailbox step into the browser step's script and nowhere else, and the code never leaves the browser step.

NO FALLBACK. Any failure stops the run, names the step, and the caller reports it (the Director reports the approver
as still needing a fresh sign-in, which raises the license alarm on the Gateway).
"""

from __future__ import annotations

import datetime as _dt
import email.utils
import html
import json
import logging
import os
import re
import shutil
import subprocess
import time
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional, Protocol

from . import license_signin
from .license_signin import (
    BrowserProfile,
    LicenseStore,
    RunHarness,
    SignInError,
    _NAVIGATION_SAFE,
    _result,
    _run_harness,
    director_browser,
)

log = logging.getLogger("cc_devthrottle.license_reauth")

#: The owner's account-to-mailbox map, in the key store's folder, beside the account-to-profile map.
MAILBOXES_FILE = "sign-in-mailboxes.json"

#: The mailbox kinds this tool reads: Gmail through cc-gmail, Outlook through cc-outlook.
GMAIL = "gmail"
OUTLOOK = "outlook"
MAIL_KINDS = (GMAIL, OUTLOOK)

# The steps of the flow, named in every failure.
STEP_REQUEST = "request"
STEP_MAIL = "mail"
STEP_CODE = "code"
STEP_CONFIRM = "confirm"
REAUTH_STEPS = (STEP_REQUEST, STEP_MAIL, STEP_CODE, STEP_CONFIRM)

# Why a re-sign-in failed. Each needs a different fix, so they are never folded into one.
NO_MAILBOX = "no-mailbox"                          # no mailbox is set for the account on this machine
PREFILLED_OTHER_ACCOUNT = "prefilled-other-account"  # the email field would not hold the account: nothing was sent
REAUTH_FAILED = "reauth-failed"                    # a step did not do what it must; the step and why say which

#: How long the mail may take to arrive, and how often the mailbox is read meanwhile.
MAIL_WAIT_SECONDS = 150
MAIL_POLL_SECONDS = 5
#: How long a mail tool may take for one list or read.
MAIL_TOOL_SECONDS = 60
#: How long one browser step may take.
STEP_SECONDS = 180

SECURE_LINK_SUBJECT = "secure link to Claude"
#: The link runs to the first whitespace, quote, "<" or backslash: never cut at a character a list of allowed ones
#: forgot (the review of pull request 4716, finding 4).
MAGIC_LINK = re.compile(r"https://claude\.ai/magic-link#[^\s\"'<>\\]+")


class ReauthError(SignInError):
    """A re-sign-in did not happen. ``step`` is one of REAUTH_STEPS (None before the first); the message never
    carries the link or the code."""

    def __init__(self, kind: str, step: Optional[str], message: str, next_steps: Optional[List[str]] = None):
        super().__init__(kind, message, next_steps)
        self.step = step


# ---------------------------------------------------------------------------------------------------
# Which mailbox reads which account's mail - the owner's configuration, on the machine
# ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class MailboxSetting:
    """``kind`` is one of MAIL_KINDS; ``mail_account`` is the name the mail tool's ``-a`` takes for that mailbox."""

    kind: str
    mail_account: str


def _mailboxes_path(store: LicenseStore) -> str:
    return os.path.join(store.store_dir, MAILBOXES_FILE)


def load_mailboxes(store: LicenseStore) -> Dict[str, MailboxSetting]:
    """The owner's map of account email (lower case) to the mailbox that receives its sign-in mail."""
    path = _mailboxes_path(store)
    if not os.path.isfile(path):
        return {}
    try:
        with open(path, encoding="utf-8") as handle:
            data = json.loads(handle.read())
    except ValueError:
        raise ReauthError(NO_MAILBOX, None, f"{path} is not valid JSON", [f"Open {path} and repair it"]) from None
    accounts = data.get("accounts") if isinstance(data, dict) else None
    if not isinstance(accounts, dict):
        raise ReauthError(NO_MAILBOX, None, f"{path} has no \"accounts\" object", [f"Open {path} and repair it"])
    found: Dict[str, MailboxSetting] = {}
    for account, value in accounts.items():
        kind = value.get("kind") if isinstance(value, dict) else None
        name = value.get("mailAccount") if isinstance(value, dict) else None
        if kind not in MAIL_KINDS or not isinstance(name, str) or not name.strip():
            raise ReauthError(NO_MAILBOX, None, f"{path} holds an entry that is not a mailbox (kind {kind!r})",
                              [f"Open {path} and repair it"])
        found[str(account).strip().lower()] = MailboxSetting(kind, name.strip())
    return found


def set_mailbox(store: LicenseStore, account: str, kind: str, mail_account: str) -> str:
    """Record that ``account``'s sign-in mail is read from the ``kind`` mailbox named ``mail_account``. Returns the
    file written."""
    if kind not in MAIL_KINDS:
        raise ReauthError(NO_MAILBOX, None, f"the mailbox kind must be one of {', '.join(MAIL_KINDS)}, not {kind!r}")
    if not mail_account.strip():
        raise ReauthError(NO_MAILBOX, None, "the mail account name is empty")
    current = {a: {"kind": m.kind, "mailAccount": m.mail_account} for a, m in load_mailboxes(store).items()}
    current[account.strip().lower()] = {"kind": kind, "mailAccount": mail_account.strip()}
    path = _mailboxes_path(store)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    temp = f"{path}.{os.getpid()}.tmp"
    with open(temp, "w", encoding="utf-8") as handle:
        handle.write(json.dumps({"accounts": dict(sorted(current.items()))}, indent=2))
    os.replace(temp, path)
    return path


def mailbox_for(store: LicenseStore, account: str) -> MailboxSetting:
    setting = load_mailboxes(store).get(account.strip().lower())
    if setting is None:
        raise ReauthError(
            NO_MAILBOX, None, f"no mailbox is set to receive {account}'s sign-in mail on this machine.",
            [f"cc-devthrottle license set-mailbox {account} --kind gmail|outlook --mail-account <name>"])
    return setting


# ---------------------------------------------------------------------------------------------------
# Reading the sign-in mail - one seam, faked in the tests
# ---------------------------------------------------------------------------------------------------


class Mailbox(Protocol):
    def sign_in_link(self, account: str, since: _dt.datetime) -> Optional[str]:
        """The magic link in the newest "Your secure link to Claude" mail dated at or after ``since``, or None when
        none has arrived. Raises ReauthError (step mail) when the mailbox cannot be read."""
        ...


RunTool = Callable[[List[str], float], str]


def _run_tool(args: List[str], timeout_seconds: float) -> str:
    exe = shutil.which(args[0])
    if not exe:
        raise ReauthError(REAUTH_FAILED, STEP_MAIL, f"{args[0]} is not on this machine's PATH.",
                          [f"{args[0]} --help"])
    try:
        done = subprocess.run([exe] + args[1:], capture_output=True, text=True, encoding="utf-8", errors="replace",
                              timeout=timeout_seconds)
    except subprocess.TimeoutExpired:
        raise ReauthError(REAUTH_FAILED, STEP_MAIL, f"{args[0]} {args[3]} did not answer in "
                                                    f"{timeout_seconds:g} seconds") from None
    if done.returncode != 0:
        # The mail tool's words name the mailbox problem (a sign-in that expired, a wrong account name); a mail body
        # is never in them, because a body is only printed by a read that succeeded.
        last = " / ".join(line.strip() for line in (done.stderr or done.stdout).splitlines()[-2:] if line.strip())
        raise ReauthError(REAUTH_FAILED, STEP_MAIL, f"{args[0]} {args[3]} failed (exit {done.returncode}): {last}",
                          [f"{args[0]} -a <mail account> list -n 3"])
    return done.stdout


def _parse_gmail_date(text: str) -> Optional[_dt.datetime]:
    try:
        moment = email.utils.parsedate_to_datetime(text)
    except (TypeError, ValueError):
        return None
    return moment if moment.tzinfo else None


def _parse_outlook_date(text: str) -> Optional[_dt.datetime]:
    try:
        moment = _dt.datetime.fromisoformat(text.strip().replace("Z", "+00:00"))
    except ValueError:
        return None
    return moment if moment.tzinfo else None


def _link_in(text: str) -> Optional[str]:
    found = MAGIC_LINK.search(text or "")
    # A link inside an HTML body is written with its "&" as "&amp;"; the browser must be given the link itself.
    return html.unescape(found.group(0)) if found else None


def _read_json(text: str, what: str) -> dict:
    try:
        answer = json.loads(text)
    except ValueError:
        raise ReauthError(REAUTH_FAILED, STEP_MAIL, f"{what} did not answer JSON") from None
    if not isinstance(answer, dict):
        raise ReauthError(REAUTH_FAILED, STEP_MAIL, f"{what} did not answer a JSON object")
    return answer


def _addressed_to(recipients: object, account: str) -> bool:
    """Whether ``recipients`` - a header string or a list of addresses - names ``account``."""
    listed = recipients if isinstance(recipients, list) else [recipients]
    return any(account.lower() in str(r).lower() for r in listed)


class GmailMailbox:
    """The account's Gmail, through ``cc-gmail -a <mail account>``: ``list --json``, then ``read --json`` of the
    newest secure-link mail addressed to the account. The JSON body is the decoded text part, whole; ``read --raw``
    is NOT read, because it prints that same body through a console that folds it at 80 columns and so cuts the link
    (the review of pull request 4716, finding 1)."""

    def __init__(self, mail_account: str, run_tool: RunTool = _run_tool):
        self._account = mail_account
        self._run = run_tool

    def sign_in_link(self, account: str, since: _dt.datetime) -> Optional[str]:
        out = self._run(["cc-gmail", "-a", self._account, "list", "-n", "10", "--json"], MAIL_TOOL_SECONDS)
        try:
            rows = json.loads(out)
        except ValueError:
            raise ReauthError(REAUTH_FAILED, STEP_MAIL, "cc-gmail list --json did not answer JSON") from None
        if not isinstance(rows, list):
            raise ReauthError(REAUTH_FAILED, STEP_MAIL, "cc-gmail list --json did not answer a list")
        candidates = []
        for row in rows:
            if not isinstance(row, dict) or SECURE_LINK_SUBJECT not in str(row.get("subject", "")):
                continue
            sent = _parse_gmail_date(str(row.get("date", "")))
            if sent is None or sent < since:
                continue
            if not _addressed_to(row.get("to", ""), account):
                continue
            candidates.append((sent, str(row.get("id", ""))))
        if not candidates:
            return None
        _, message_id = max(candidates)
        mail = _read_json(self._run(["cc-gmail", "-a", self._account, "read", message_id, "--json"],
                                    MAIL_TOOL_SECONDS), "cc-gmail read --json")
        link = _link_in(str(mail.get("body", "")))
        if link is None:
            raise ReauthError(REAUTH_FAILED, STEP_MAIL, "the newest sign-in mail holds no claude.ai sign-in link")
        return link


#: One message in ``cc-outlook list``: "[*]" (unread) or "[ ]" (read), its id - which the console wraps over lines,
#: and which can be followed by FLAG, ATT or ! - then From, Subject and Date.
_OUTLOOK_BLOCK = re.compile(
    r"\[[* ]\]\s*(?P<id>.*?)\s*From:.*?Subject:\s*(?P<subject>[^\n]*)\n\s*Date:\s*(?P<date>\S+)", re.S)
_OUTLOOK_INDICATORS = {"FLAG", "ATT", "!"}


def _outlook_id(text: str) -> str:
    """The message id from the text between the read marker and "From:": its wrapped pieces joined, without the
    indicators cc-outlook prints after it."""
    return "".join(piece for piece in text.split() if piece not in _OUTLOOK_INDICATORS)


class OutlookMailbox:
    """The account's Outlook, through ``cc-outlook -a <mail account>``: ``list``, then ``read --raw`` - the message as
    JSON - of each secure-link mail since the request, newest first, until one is addressed to the account. ``list``
    has no JSON form, so its blocks are read by their labels; a mail is taken whether it was read already or not
    (a phone can mark it read first)."""

    def __init__(self, mail_account: str, run_tool: RunTool = _run_tool):
        self._account = mail_account
        self._run = run_tool

    def sign_in_link(self, account: str, since: _dt.datetime) -> Optional[str]:
        out = self._run(["cc-outlook", "-a", self._account, "list", "-n", "10"], MAIL_TOOL_SECONDS)
        candidates = []
        for block in _OUTLOOK_BLOCK.finditer(out):
            if SECURE_LINK_SUBJECT not in block.group("subject"):
                continue
            sent = _parse_outlook_date(block.group("date"))
            if sent is None or sent < since:
                continue
            candidates.append((sent, _outlook_id(block.group("id"))))
        for _, message_id in sorted(candidates, reverse=True):
            mail = _read_json(self._run(["cc-outlook", "-a", self._account, "read", message_id, "--raw"],
                                        MAIL_TOOL_SECONDS), "cc-outlook read --raw")
            # A shared mailbox can receive another account's sign-in mail: its link would sign that account in here.
            if not _addressed_to(mail.get("to", []), account):
                log.info("reauth %s: a sign-in mail addressed to another account was passed over", account)
                continue
            link = _link_in(str(mail.get("body", "")))
            if link is None:
                raise ReauthError(REAUTH_FAILED, STEP_MAIL, "the newest sign-in mail holds no claude.ai sign-in link")
            return link
        return None


def open_mailbox(setting: MailboxSetting, run_tool: RunTool = _run_tool) -> Mailbox:
    return (GmailMailbox if setting.kind == GMAIL else OutlookMailbox)(setting.mail_account, run_tool)


# ---------------------------------------------------------------------------------------------------
# The two browser steps
# ---------------------------------------------------------------------------------------------------


#: What both steps define: the result line, a wait for one element's centre, and the click at it.
_STEP_HELPERS = r'''
import json, re, time
WAIT = %(wait)s

def finish(state, **extra):
    print("RESULT " + json.dumps(dict(state=state, **extra)))

def centre(expression):
    # The centre of the element ``expression`` finds, once it is laid out; None when it is not there in time.
    deadline = time.time() + WAIT
    while True:
        found = read(lambda: js("(()=>{const e=" + expression + ";if(!e||!e.offsetParent)return null;"
                                "const b=e.getBoundingClientRect();return [b.x+b.width/2,b.y+b.height/2];})()"))
        if found or time.time() >= deadline:
            return found
        time.sleep(1)

def button(label):
    return "[...document.querySelectorAll('button')].find(b=>b.innerText.trim()===" + json.dumps(label) + ")"
'''

#: Step 1, request: the sign-in page in the account's profile, the email field cleared and typed, and only when it
#: holds exactly the account, "Continue with email". The tab stays open for step 3, which types the code into it.
_REQUEST_SCRIPT = _STEP_HELPERS + r'''
URL = %(url)s
EMAIL = %(email)s
new_tab(URL)
tab = current_tab()
keep = False
cdp("Emulation.setFocusEmulationEnabled", enabled=True)
try:
    settle_load()
    field = centre("document.getElementById('email')")
    if not field:
        finish("no-email-field")
    else:
        # Clear the field the way that works: select its text and delete it (Ctrl+A did not clear it, 6 November).
        click_at_xy(field[0], field[1])
        time.sleep(0.3)
        read(lambda: js("document.getElementById('email').select()"))
        time.sleep(0.2)
        press_key("Backspace")
        time.sleep(0.3)
        cdp("Input.insertText", text=EMAIL)
        time.sleep(0.5)
        press_key("Escape")
        held = read(lambda: js("(document.getElementById('email')||{}).value||''")) or ""
        if held.strip().lower() != EMAIL.lower():
            # The trap: another account the browser remembers. Nothing is pressed, so no code goes anywhere.
            finish("prefilled-other-account")
        else:
            go = centre(button("Continue with email"))
            if not go:
                finish("no-continue-button")
            else:
                click_at_xy(go[0], go[1])
                keep = True
                finish("requested", tab=tab["targetId"])
finally:
    cdp("Emulation.setFocusEmulationEnabled", enabled=False)
    if not keep:
        cdp("Target.closeTarget", targetId=tab["targetId"])
'''

#: Step 3, code: the link in a second tab of the same profile, its 6-digit code read there and typed into the first
#: tab's field, then "Verify email address". It prints the state only - never the code.
_CODE_SCRIPT = _STEP_HELPERS + r'''
LINK = %(link)s
LOGIN_TAB = %(tab)s
code = None
new_tab(LINK)
link_tab = current_tab()
cdp("Emulation.setFocusEmulationEnabled", enabled=True)
try:
    settle_load()
    deadline = time.time() + WAIT
    while not code and time.time() < deadline:
        text = read(lambda: js("document.body ? document.body.innerText : ''")) or ""
        found = re.search(r"\b(\d{6})\b", text)
        code = found.group(1) if found else None
        if not code:
            time.sleep(1)
finally:
    cdp("Emulation.setFocusEmulationEnabled", enabled=False)
    cdp("Target.closeTarget", targetId=link_tab["targetId"])
try:
    if not code:
        finish("no-code")
    else:
        switch_tab(LOGIN_TAB)
        cdp("Emulation.setFocusEmulationEnabled", enabled=True)
        field = centre("document.getElementById('code')")
        if not field:
            finish("no-code-field")
        else:
            click_at_xy(field[0], field[1])
            time.sleep(0.5)
            type_text(code)
            time.sleep(1)
            verify = centre(button("Verify email address"))
            if not verify:
                finish("no-verify-button")
            else:
                click_at_xy(verify[0], verify[1])
                # The page navigates now: evaluate nothing until it has had time to go, then wait for it to settle.
                time.sleep(10)
                settle_load()
                still_asking = read(lambda: js("!!document.getElementById('code')"))
                finish("code-not-taken" if still_asking else "verified")
finally:
    cdp("Emulation.setFocusEmulationEnabled", enabled=False)
    cdp("Target.closeTarget", targetId=LOGIN_TAB)
'''


def _step_script(body: str, **values: str) -> str:
    return _NAVIGATION_SAFE + body % dict({k: json.dumps(v) for k, v in values.items()},
                                          wait=license_signin.BROWSER_WAIT_SECONDS // 2)


def login_url(account: str) -> str:
    return "https://claude.ai/login?reauth=1&email=" + account.strip().lower().replace("@", "%40")


_REQUEST_STATES = {
    "no-email-field": "the sign-in page showed no email field",
    "no-continue-button": "the sign-in page showed no \"Continue with email\" button",
}
_CODE_STATES = {
    "no-code": "the sign-in link opened a page that showed no 6-digit code",
    "no-code-field": "the sign-in page showed no field for the code",
    "no-verify-button": "the sign-in page showed no \"Verify email address\" button",
    "code-not-taken": "claude.ai still asked for the code after it was typed in and verified",
}


# ---------------------------------------------------------------------------------------------------
# The re-sign-in
# ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class ReauthResult:
    account_email: str
    browser_profile: str
    browser_profile_name: Optional[str]
    check: license_signin.ApproverCheck

    def to_json(self) -> dict:
        return {"accountEmail": self.account_email, "browserProfile": self.browser_profile,
                "browserProfileName": self.browser_profile_name, "result": "signed-in-again",
                "check": self.check.to_json()}


def utc_now() -> _dt.datetime:
    return _dt.datetime.now(_dt.timezone.utc)


def reauth(account: str, profile: str, mailbox: Mailbox, *,
           open_profile: Callable[[str], BrowserProfile] = director_browser,
           run_harness: RunHarness = _run_harness,
           check: Callable[[str, str], license_signin.ApproverCheck] = license_signin.check_approver,
           now: Callable[[], _dt.datetime] = utc_now,
           sleep: Callable[[float], None] = time.sleep) -> ReauthResult:
    """Sign the browser profile ``profile`` in to claude.ai again as ``account``, from the account's mailbox, and
    confirm with the approver check. Raises ReauthError naming the step that failed; nothing is retried."""
    account = account.strip().lower()
    log.info("reauth %s in profile %s: starting", account, profile)
    browser = open_profile(profile)
    profile_id, profile_name = browser.id, browser.name
    try:
        env = browser.environment()
        asked = now()
        answer = _step(run_harness, _step_script(_REQUEST_SCRIPT, url=login_url(account), email=account), env,
                       STEP_REQUEST)
        state = answer.get("state")
        if state == PREFILLED_OTHER_ACCOUNT:
            raise ReauthError(
                PREFILLED_OTHER_ACCOUNT, STEP_REQUEST,
                f"the sign-in page in the browser profile \"{profile_name}\" would not hold {account} in its email "
                "field (it keeps another account the browser remembers), so nothing was sent.",
                [f"Open the browser profile \"{profile_name}\", sign out of claude.ai and sign in as {account} once"])
        if state != "requested" or not isinstance(answer.get("tab"), str):
            raise ReauthError(REAUTH_FAILED, STEP_REQUEST,
                              _REQUEST_STATES.get(state, f"the request step answered {state!r}"))
        log.info("reauth %s: the sign-in mail was asked for", account)
        link = _wait_for_link(mailbox, account, asked, now, sleep)
        answer = _step(run_harness, _step_script(_CODE_SCRIPT, link=link, tab=answer["tab"]), env, STEP_CODE,
                       secret=True)
        state = answer.get("state")
        if state != "verified":
            raise ReauthError(REAUTH_FAILED, STEP_CODE, _CODE_STATES.get(state, f"the code step answered {state!r}"))
        log.info("reauth %s: the code was taken", account)
    finally:
        browser.stop()
    confirmed = check(profile, account)
    if confirmed.state != license_signin.CAN_APPROVE:
        raise ReauthError(REAUTH_FAILED, STEP_CONFIRM,
                          f"after signing in again the approver check answered {confirmed.state}: {confirmed.detail}")
    log.info("reauth %s in profile %s: done, can-approve", account, profile_id)
    return ReauthResult(account, profile_id, profile_name, confirmed)


def _step(run_harness: RunHarness, script: str, env: Dict[str, str], step: str, secret: bool = False) -> dict:
    """One browser step's result. A ``secret`` step holds the sign-in link in its script: it is run with the harness's
    ``secret`` flag, and its failure is said in a fixed sentence - never the harness's words, which could quote the
    script (the review of pull request 4716, finding 5)."""
    try:
        out = run_harness(script, env, STEP_SECONDS, secret=True) if secret else run_harness(script, env, STEP_SECONDS)
        return _result(out)
    except ReauthError:
        raise
    except SignInError as ex:
        if secret:
            raise ReauthError(REAUTH_FAILED, step, "the browser step that types the code in failed; its output is not "
                                                   "shown because it holds the sign-in link",
                              ["browser-harness --doctor"]) from None
        raise ReauthError(REAUTH_FAILED, step, str(ex), ex.next_steps) from None


def _wait_for_link(mailbox: Mailbox, account: str, asked: _dt.datetime, now: Callable[[], _dt.datetime],
                   sleep: Callable[[float], None]) -> str:
    deadline = now() + _dt.timedelta(seconds=MAIL_WAIT_SECONDS)
    while True:
        link = mailbox.sign_in_link(account, asked)
        if link is not None:
            log.info("reauth %s: the sign-in mail arrived", account)
            return link
        if now() >= deadline:
            raise ReauthError(REAUTH_FAILED, STEP_MAIL, f"no sign-in mail for {account} arrived in "
                                                        f"{MAIL_WAIT_SECONDS} seconds")
        sleep(MAIL_POLL_SECONDS)
