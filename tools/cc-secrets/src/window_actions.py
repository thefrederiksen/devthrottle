"""What the cc-secrets windows DO, kept apart from how they are drawn, so all of it is tested without a screen.

Three screens share this: the list (`cc-secrets ui`), the add / edit form opened from it, and the pop-up an agent
opens with `cc-secrets ask`. Every save goes through `saving.save_entry`, the same code as `cc-secrets add`. Every
reveal (the eye) and every save and delete is written to the audit log - never the secret.

The owner's rulings for the list (2026-10-09), recorded as given: agents may open the list, so they can put it on
screen for the owner, but no command, argument or interface reveals a secret through it - only a click on the eye.
A revealed secret stays shown until the eye is clicked again. There is no Copy button. Like the rest of
cc-secrets, this guards against accidental exposure, not against a program on the same desktop that clicks the
eye itself.
"""

from __future__ import annotations

import traceback
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Dict, Iterable, List, Optional, Set

from . import filelog
from .audit import AuditLog, OwnerApproval
from .errors import CcSecretsError, describe
from .saving import save_entry
from .store import KIND_SETTING, USES, Entry, SecretStore, validate_name

MODE_ADD = "add"
MODE_EDIT = "edit"
MODE_ASK = "ask"
MODES = (MODE_ADD, MODE_EDIT, MODE_ASK)

ASK_RECORD = "typed by the owner in the cc-secrets ask window"
WINDOW_RECORD = "done by the owner in the cc-secrets window"

# Audit commands that mean an entry was USED, for the list's "Last used" column.
_USE_COMMANDS = ("run", "login", "get")


@dataclass(frozen=True)
class FormRequest:
    """What a form opens with. Deliberately without a secret: the form's secret box always starts empty."""
    mode: str
    name: str = ""
    kind_setting: bool = False
    username: str = ""
    notes: str = ""
    agents_may_use: bool = True
    uses: List[str] = field(default_factory=lambda: list(USES))
    domains: str = ""
    asked_by: str = ""
    reason: str = ""
    exists: bool = False


@dataclass(frozen=True)
class FormInput:
    """What the owner submitted. `secret` is the typed value, empty meaning 'keep the current one' when editing."""
    name: str
    kind_setting: bool
    username: str
    secret: str
    notes: str
    agents_may_use: bool
    uses: List[str]
    domains: str


@dataclass(frozen=True)
class Row:
    """One line of the list. A secret's value is never in a row - only the eye fetches it."""
    name: str
    kind: str
    username: str
    setting_value: str
    agents: str
    last_used: str
    notes: str

    @property
    def is_setting(self) -> bool:
        return self.kind == KIND_SETTING


def replace_warning(name: str) -> str:
    return f"An entry called {name} already exists. Save REPLACES it."


def relative_time(iso: str, now: datetime) -> str:
    """'2 min ago', 'yesterday', '23 days ago' - or 'never' for no time."""
    if not iso:
        return "never"
    seconds = max(0, int((now - datetime.fromisoformat(iso)).total_seconds()))
    if seconds < 60:
        return "just now"
    if seconds < 3600:
        return f"{seconds // 60} min ago"
    if seconds < 86400:
        return f"{seconds // 3600} h ago"
    days = seconds // 86400
    return "yesterday" if days == 1 else f"{days} days ago"


def last_used(lines: Iterable[Dict[str, str]]) -> Dict[str, str]:
    """{entry: the time of its most recent successful use} from audit lines, oldest first."""
    found: Dict[str, str] = {}
    for line in lines:
        command = str(line.get("command", ""))
        if line.get("outcome") == "ok" and command.split(" ", 1)[0] in _USE_COMMANDS:
            found[str(line.get("entry", ""))] = str(line.get("time", ""))
    return found


def build_rows(entries: Iterable[Entry], used: Dict[str, str], now: datetime) -> List[Row]:
    return [Row(name=e.name, kind=e.kind, username=e.username,
                setting_value=e.secret.reveal() if e.is_setting else "",
                agents="yes" if e.agents_may_use else "no",
                last_used=relative_time(used.get(e.name, ""), now), notes=e.notes)
            for e in sorted(entries, key=lambda e: e.name)]


def filter_rows(rows: List[Row], query: str) -> List[Row]:
    """Rows whose name, user name or notes contain the query, ignoring case. A setting's value is matched too;
    a secret's never is, so typing part of a password cannot find - and so confirm - which entry holds it."""
    wanted = query.strip().lower()
    if not wanted:
        return list(rows)
    return [r for r in rows if wanted in " ".join((r.name, r.username, r.notes, r.setting_value)).lower()]


def log_failure(where: str, exc: BaseException) -> None:
    """Log where it failed: the type and the stack of code lines, never a message that is not cc-secrets' own."""
    stack = "".join(traceback.format_list(traceback.extract_tb(exc.__traceback__)))
    message = f": {exc}" if isinstance(exc, CcSecretsError) else ""
    filelog.write(f"[window] {where} FAILED: {type(exc).__name__}{message}\n{stack}")


class WindowActions:
    """Save, reveal and delete for one window, each audited with the session that opened it (if any)."""

    def __init__(self, store: SecretStore, audit: AuditLog, approval: OwnerApproval, env_name: str = "",
                 detail: str = "") -> None:
        self._store = store
        self._audit = audit
        self._approval = approval
        self._env_name = env_name
        self._detail = detail
        self._told_replaces: Set[str] = set()
        self.saved_name: Optional[str] = None

    def told_about(self, name: str) -> None:
        """The form already shows that saving `name` replaces it."""
        self._told_replaces.add(name)

    def rows(self) -> List[Row]:
        return build_rows(self._store.entries(), last_used(self._audit.read(10 ** 9)), datetime.now(timezone.utc))

    def edit_request(self, name: str) -> FormRequest:
        """The edit form for an entry: everything but its secret, which only the eye fetches."""
        entry = self._store.get(name)
        if entry is None:
            raise CcSecretsError(f"There is no entry named '{name}'.")
        return FormRequest(mode=MODE_EDIT, name=entry.name, kind_setting=entry.is_setting, username=entry.username,
                           notes=entry.notes, agents_may_use=entry.agents_may_use, uses=list(entry.uses),
                           domains=", ".join(entry.allowed_domains), exists=True)

    def reveal(self, name: str) -> str:
        """The secret, for the eye. Audited first: a reveal that cannot be recorded does not happen."""
        entry = self._store.get(name)
        if entry is None:
            raise CcSecretsError(f"There is no entry named '{name}'.")
        self._audit.record(name, "ui reveal", "ok", "shown in the cc-secrets window", self._approval)
        filelog.write(f"[WindowActions] reveal: name={name}")
        return entry.secret.reveal()

    def delete(self, name: str) -> None:
        prepared = self._audit.prepare(name, "ui delete", "ok", WINDOW_RECORD, self._approval)
        if not self._store.remove(name):
            raise CcSecretsError(f"There is no entry named '{name}'.")
        self._audit.write_prepared([prepared])

    def submit(self, form: FormInput, mode: str, edit_name: str = "") -> Optional[str]:
        """Save the form. None when saved; otherwise the message for the form, which stays open so what was typed
        is not lost. This is the form's Save handler, so every error ends here as a message."""
        try:
            return self._submit(form, mode, edit_name)
        except CcSecretsError as exc:
            # Not logged: these messages echo what was typed ("'<text>' is not a valid entry name"), and a password
            # typed into the wrong box by reflex must not land in the tool log. It is shown in the form only.
            return f"Not saved: {describe(exc)}"
        except Exception as exc:
            log_failure("submit", exc)
            return f"Not saved: {describe(exc)}"

    def _submit(self, form: FormInput, mode: str, edit_name: str) -> Optional[str]:
        if mode not in MODES:
            raise ValueError(f"unknown form mode {mode!r}")
        validate_name(form.name)
        command = "ask" if mode == MODE_ASK else f"ui {mode}"
        detail = self._detail
        secret = form.secret
        env_name = self._env_name
        if mode == MODE_EDIT:
            current = self._store.get(edit_name)
            if current is None:
                return f"'{edit_name}' no longer exists. Close this and add it again."
            env_name = current.env_name
            kept = not secret or secret == current.secret.reveal()
            if kept and not current.is_setting and form.kind_setting:
                # A setting is printed by get and never hidden from output, so turning a stored password into one
                # would publish it. The new kind needs its value typed.
                return ("A secret cannot become a setting with its current value: a setting is printed by "
                        "'cc-secrets get' and never hidden. Type the setting's value, or keep the kind Secret.")
            secret = secret or current.secret.reveal()
            changes = ["value kept" if kept else "value changed"]
            if current.is_setting != form.kind_setting:
                changes.append(f"kind changed to {'setting' if form.kind_setting else 'secret'}")
            detail = "; ".join([WINDOW_RECORD] + changes)
        else:
            existing = self._store.get(form.name)
            if existing is not None and form.name not in self._told_replaces:
                self._told_replaces.add(form.name)
                return replace_warning(form.name) + " Press Save again to replace it."
            if existing is not None and not env_name:
                env_name = existing.env_name  # replacing keeps the variable run supplies it in
        domains = [d.strip() for d in form.domains.split(",") if d.strip()]
        save_entry(self._store, self._audit, form.name, form.username, secret, domains, form.notes,
                   form.agents_may_use, form.uses, env_name, form.kind_setting, command, detail, self._approval)
        self.saved_name = form.name
        return None
