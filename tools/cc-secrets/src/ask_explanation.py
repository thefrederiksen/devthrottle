"""Why a session is asking for a secret: the explanation `cc-secrets ask` requires, the panel the window draws from it,
and the guard on the owner's "Not now" reply.

The owner's rulings (9 November 2026), recorded as given:

- A request without the explanation is REFUSED before the window opens, and the agent is told exactly which parts are
  missing. Otherwise agents keep skipping it and the bare pop-up comes back. For a password every part is required:
  what it is for, whether the account exists already or must be created first (and then how), where the owner finds
  it, what the session will do with it, and what happens if the owner says not now. For a setting, which is not
  secret, only what it is for is required.
- Cancel becomes "Not now" with a short reply box, and the owner's words go back to the asking session as the
  answer of its own command. A reply never carries a secret: one that holds what was typed in the password box, or
  any stored password, is refused in the window and never leaves it.

Everything here is plain data and plain checks, so it is tested without a screen; entry_window only draws it.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import List, Optional, Tuple

from .errors import InputError
from .redact import SCRUBBER
from .store import MIN_SECRET_LENGTH

ACCOUNT_EXISTING = "existing"
ACCOUNT_NEW = "new"
ACCOUNTS = (ACCOUNT_EXISTING, ACCOUNT_NEW)

REPLY_MAX_LENGTH = 500
NO_REPLY = "none - the owner closed the window without saying why"

# The missing-part lines, one per option, each saying what to put in it - the refusal is the agent's instructions.
WHY_REASON = '--reason "..."         what it is for: the job it unblocks, in words the owner recognises'
WHY_ACCOUNT = "--account existing|new  whether the owner already has this account, or must create it first"
WHY_WHERE = '--where-to-find "..."  where the owner finds the existing password (a password manager, an admin page)'
WHY_STEPS = '--create-step "..."    one per step the owner follows to create the account (repeat the option)'
WHY_WILL_DO = '--will-do "..."        exactly what the session will do with it, and what it will not do'
WHY_IF_NOT_NOW = '--if-not-now "..."     what happens if the owner says not now'

REFUSAL_EXAMPLE = ('cc-secrets ask info-smtp-password --username info@example.com --uses run '
                   '--reason "Send the replies you approved from info@example.com" --account existing '
                   '--where-to-find "Your password manager, or reset it in the Microsoft 365 admin centre" '
                   '--will-do "Sign in to the mail server only to send an approved reply; it will not read the mailbox" '
                   '--if-not-now "The replies stay queued and nothing is sent"')


@dataclass(frozen=True)
class AskingSession:
    """Who is asking, as the owner sees it: the session's number and name, the machine and mission it runs under,
    and the Gateway's own address for opening it. Never an internal id."""
    title: str
    where: str = ""
    view_url: str = ""


@dataclass(frozen=True)
class AskExplanation:
    """What the agent said about the request. `account` is ACCOUNT_EXISTING, ACCOUNT_NEW, or "" for a setting that
    did not say."""
    reason: str
    account: str = ""
    account_detail: str = ""
    where_to_find: str = ""
    create_steps: Tuple[str, ...] = ()
    will_do: str = ""
    if_not_now: str = ""
    session: AskingSession = field(default_factory=lambda: AskingSession(title=""))


@dataclass(frozen=True)
class PanelSection:
    """One section of the "Why you are being asked" panel. `badge` is drawn as a coloured label before the text;
    `steps` as a numbered list after it; `link` as an "Open session" link."""
    heading: str
    text: str = ""
    muted: str = ""
    badge: str = ""
    badge_kind: str = ""
    steps: Tuple[str, ...] = ()
    link: str = ""


def _blank(value: Optional[str]) -> bool:
    return value is None or not value.strip()


def missing_parts(setting: bool, reason: Optional[str], account: Optional[str], where_to_find: Optional[str],
                  create_steps: List[str], will_do: Optional[str], if_not_now: Optional[str]) -> List[str]:
    """Every part of the explanation the request is missing, as the lines of the refusal. Empty when complete."""
    missing = []
    if _blank(reason):
        missing.append(WHY_REASON)
    if setting:
        return missing
    if _blank(account):
        missing.append(WHY_ACCOUNT)
    elif account.strip() == ACCOUNT_EXISTING and _blank(where_to_find):
        missing.append(WHY_WHERE)
    elif account.strip() == ACCOUNT_NEW and not [s for s in create_steps if s.strip()]:
        missing.append(WHY_STEPS)
    if _blank(will_do):
        missing.append(WHY_WILL_DO)
    if _blank(if_not_now):
        missing.append(WHY_IF_NOT_NOW)
    return missing


def check_account(account: Optional[str]) -> None:
    """A value given to --account must be one the window can draw."""
    if not _blank(account) and account.strip() not in ACCOUNTS:
        raise InputError(f"--account must be '{ACCOUNT_EXISTING}' (the owner already has it) or '{ACCOUNT_NEW}' "
                         f"(the owner must create it first), not '{account}'.")


def refusal_message(missing: List[str], setting: bool) -> str:
    what = "a setting" if setting else "a password"
    lines = [f"ask refused: the owner is never shown a request for {what} without saying why. Nothing was opened. "
             "Add these and ask again:"]
    lines += [f"  {line}" for line in missing]
    lines.append(f"Example: {REFUSAL_EXAMPLE}")
    return "\n".join(lines)


def panel_sections(why: AskExplanation, setting: bool) -> List[PanelSection]:
    """The panel, top to bottom: who is asking, the account, what it is for, what the session will do, and what
    happens on not now. A part the agent did not give (only possible for a setting) is left out, not drawn empty."""
    sections = [PanelSection(heading="Who is asking", text=why.session.title, muted=why.session.where,
                             link=why.session.view_url)]
    if why.account == ACCOUNT_EXISTING:
        sections.append(PanelSection(heading="The account", badge="YOU ALREADY HAVE THIS ACCOUNT",
                                     badge_kind=ACCOUNT_EXISTING, text=why.account_detail.strip(),
                                     muted=f"Where to find it: {why.where_to_find.strip()}" if why.where_to_find.strip()
                                     else ""))
    elif why.account == ACCOUNT_NEW:
        sections.append(PanelSection(heading="The account", badge="NEW ACCOUNT - CREATE IT FIRST",
                                     badge_kind=ACCOUNT_NEW, text=why.account_detail.strip(),
                                     steps=tuple(s.strip() for s in why.create_steps if s.strip()),
                                     muted=f"Where to find it: {why.where_to_find.strip()}" if why.where_to_find.strip()
                                     else ""))
    elif why.account_detail.strip() or why.where_to_find.strip():
        sections.append(PanelSection(heading="The account" if not setting else "The setting",
                                     text=why.account_detail.strip(),
                                     muted=f"Where to find it: {why.where_to_find.strip()}" if why.where_to_find.strip()
                                     else ""))
    sections.append(PanelSection(heading="What it is for", text=why.reason.strip()))
    if why.will_do.strip():
        sections.append(PanelSection(heading="What the session will do with it", text=why.will_do.strip()))
    if why.if_not_now.strip():
        sections.append(PanelSection(heading="If you say not now", text=why.if_not_now.strip()))
    return sections


def reply_problem(reply: str, typed_secret: str) -> Optional[str]:
    """Why the owner's "Not now" reply cannot be sent, or None when it can. A reply holding what is typed in the
    password box, or any password this process has read from the store, never leaves the window."""
    if len(reply) > REPLY_MAX_LENGTH:
        return f"Keep the reply under {REPLY_MAX_LENGTH} characters; it is {len(reply)}."
    if len(typed_secret) >= MIN_SECRET_LENGTH and typed_secret in reply:
        return ("Not sent: the reply contains what is typed in the password box. The reply goes back to the "
                "session, so it must never hold a password.")
    if SCRUBBER.contains(reply):
        return ("Not sent: the reply contains a stored password. The reply goes back to the session, so it must "
                "never hold a password.")
    return None


def one_line(reply: str) -> str:
    """The reply as the single line the session reads: line breaks and runs of spaces become one space."""
    return " ".join(reply.split())


@dataclass(frozen=True)
class AskOutcome:
    """How the window ended. `reply` is the owner's "Not now" words, "" when they closed it without saying why."""
    saved: bool
    reply: str = ""
