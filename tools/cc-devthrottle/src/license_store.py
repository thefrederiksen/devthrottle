"""The license key store: which Claude accounts this machine holds, and the EXCHANGE that switches them.

WORDS. A *config folder* is what CLAUDE_CONFIG_DIR points at. The machine's *default config folder* holds the
*active* sign-in, which every session uses. Each *spare* account lives in a config folder of its own under the
store's root, ``<store>/spares/<slug of the email>/``. An account is identified by the email in the
``oauthAccount`` block of its folder's account file.

WHERE A SIGN-IN LIVES (checked against Claude Code 2.1.296 and the mission's prototype):

- The key. Windows and Linux: ``<folder>/.credentials.json``. macOS: a Keychain generic password whose service is
  ``Claude Code-credentials`` for the real default folder (CLAUDE_CONFIG_DIR unset) and
  ``Claude Code-credentials-<first 8 hex of sha256(folder)>`` for any other folder, where ``folder`` is the exact
  string CLAUDE_CONFIG_DIR is set to. Older Claude Code builds wrote the item under the macOS user name as its
  account, newer ones under ``claude-code-user``, so an item keeps the account it already has when it moves.
- The account block. ``~/.claude.json`` for the real default folder (in the HOME folder, not inside ``~/.claude``),
  ``<folder>/.claude.json`` for any other. ``claude auth status`` reads the account from this block, not from the
  key, so a switch rewrites both or the screen lies.

THE RULES THIS MODULE EXISTS TO KEEP:

- A sign-in key is NEVER copied and left in two places: two holders share one refresh token, and a refresh in one
  can cancel the other. Adding a spare MOVES the key out of the folder it was signed in to. A switch EXCHANGES:
  the active key goes to the old account's spare folder and the spare's key comes into the default folder.
- Each sign-in exists in exactly one place after every operation, including after a crash: a journal is written
  before the first change and advanced after each step, and the next run of any store method finishes the
  operation or rolls it back. A key is deleted only when the same key is proven present where it went, so a
  crash can leave a duplicate for recovery to remove but never lose a sign-in.
- No key value is ever printed, logged, put in an exception message, or written anywhere but its own store.
  ``HeldKey`` hides its value from ``repr`` and ``str``; the operations log carries emails and folders only.
- The machine's real default folder is refused unless ``allow_real_default`` is passed. Nothing in this release
  passes it, so no test, proof or command can touch the real login.

FOR OTHER MODULES (usage reading, spare refresh): ``LicenseStore.held_accounts()`` lists what is held;
``HeldAccount.read_key()`` gives a ``HeldKey`` whose ``access_token()`` is the Bearer value, and
``LicenseStore.store_refreshed_key()`` writes a refreshed key back into the same folder.
"""

from __future__ import annotations

import datetime as _dt
import hashlib
import hmac
import json
import os
import re
import subprocess
import sys
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional, Protocol, Sequence

CREDENTIALS_FILE = ".credentials.json"
ACCOUNT_FILE = ".claude.json"
KEYCHAIN_SERVICE = "Claude Code-credentials"
#: The account Claude Code 2.1.296 writes its Keychain items under. Used only for an item that has none to keep.
KEYCHAIN_DEFAULT_ACCOUNT = "claude-code-user"
#: `security` exits with this when the item does not exist.
KEYCHAIN_NOT_FOUND = 44
#: The longest line `security -i` reads.
KEYCHAIN_INTERACTIVE_LIMIT = 4096

ENV_DEFAULT_CONFIG_DIR = "CC_LICENSE_DEFAULT_CONFIG_DIR"
ENV_STORE_DIR = "CC_LICENSE_STORE_DIR"

JOURNAL_FILE = "journal.json"
LOG_FILE = "license-store.log"
LOCK_FILE = "store.lock"
SPARES_DIR = "spares"

ROLE_ACTIVE = "active"
ROLE_SPARE = "spare"


#: Why a switch was refused or failed, in the words of the Director and Gateway contract
#: (missions/license-switching/contract.md, section 3.4). The Director passes them on unchanged.
REASON_SWITCH_IN_PROGRESS = "switch-in-progress"
REASON_ACTIVE_ACCOUNT_CHANGED = "active-account-changed"
REASON_SPARE_NOT_HELD = "spare-not-held"
REASON_EXCHANGE_FAILED_RESTORED = "exchange-failed-restored"
REASON_EXCHANGE_FAILED_UNKNOWN = "exchange-failed-unknown"
REASON_VERIFY_FAILED = "verify-failed"


class LicenseStoreError(Exception):
    """An operation could not be done. The message names what failed and never carries a key value;
    ``next_steps`` names what to run or check next. ``reason_code`` is one of the REASON_ words when the
    failure is one the contract names, else None. ``previous_active`` is the active account's email read
    before a switch began, when the switch got that far."""

    def __init__(self, message: str, next_steps: Sequence[str], reason_code: Optional[str] = None):
        super().__init__(message)
        self.next_steps = list(next_steps)
        self.reason_code = reason_code
        self.previous_active: Optional[str] = None


class _StepFailed(Exception):
    """A journal step found the world not as it must be to go on (a source with no key, a key that is not
    proven held elsewhere). Nothing was changed by the step that raised it."""


# ---------------------------------------------------------------------------------------------------
# The key, held without showing it
# ---------------------------------------------------------------------------------------------------


class HeldKey:
    """A sign-in key read from its store. Its value is never shown: ``repr`` and ``str`` print a placeholder.

    ``keychain_account`` is the Keychain item's account on macOS (None for a file), kept so a moved item is
    written back under the same account Claude Code looks it up by.
    """

    __slots__ = ("_secret", "keychain_account")

    def __init__(self, secret: str, keychain_account: Optional[str] = None):
        if not secret or not secret.strip():
            raise ValueError("a held key cannot be empty")
        self._secret = secret
        self.keychain_account = keychain_account

    def __repr__(self) -> str:
        return "HeldKey(<hidden>)"

    __str__ = __repr__

    def reveal(self) -> str:
        """The raw value, for a key backend writing it to its own store. Never print or log it."""
        return self._secret

    def fingerprint(self) -> str:
        """A SHA-256 of the key, so a journal can name WHICH sign-in a step acts on without holding the key.
        Never reversible to the key; still never printed."""
        return hashlib.sha256(self._secret.encode("utf-8")).hexdigest()

    def same_as(self, other: Optional["HeldKey"]) -> bool:
        """True when ``other`` holds exactly this value."""
        return other is not None and hmac.compare_digest(self._secret.encode("utf-8"), other._secret.encode("utf-8"))

    def access_token(self) -> str:
        """The OAuth access token, for the Bearer header to api.anthropic.com and nowhere else."""
        try:
            token = json.loads(self._secret)["claudeAiOauth"]["accessToken"]
        except (ValueError, KeyError, TypeError):
            raise LicenseStoreError(
                "the sign-in key is not in the shape Claude Code writes (no claudeAiOauth.accessToken)",
                ["Sign the account in again with: claude auth login"],
            ) from None
        if not isinstance(token, str) or not token:
            raise LicenseStoreError(
                "the sign-in key has an empty access token",
                ["Sign the account in again with: claude auth login"],
            )
        return token


# ---------------------------------------------------------------------------------------------------
# Folders
# ---------------------------------------------------------------------------------------------------


def real_default_config_dir() -> Path:
    """The machine's real default config folder: ``~/.claude``."""
    return Path.home() / ".claude"


def _same_folder(a: str | os.PathLike, b: str | os.PathLike) -> bool:
    return os.path.normcase(os.path.realpath(os.fspath(a))) == os.path.normcase(os.path.realpath(os.fspath(b)))


def is_real_default(folder: str | os.PathLike) -> bool:
    """True when ``folder`` is the machine's real default config folder."""
    return _same_folder(folder, real_default_config_dir())


def account_file_for(folder: str) -> Path:
    """The account file (holding ``oauthAccount``) that Claude Code reads for ``folder``."""
    if is_real_default(folder):
        return Path.home() / ACCOUNT_FILE
    return Path(folder) / ACCOUNT_FILE


def canonical_folder(folder: str | os.PathLike, what: str) -> str:
    """``folder`` exactly as given, refused unless it is in the one canonical form: absolute, no trailing
    separator, no ``.`` or ``..`` segment, no doubled separator (on Windows, backslashes).

    Measured on the Mac with Claude Code 2.1.296: the Keychain service is a hash of the RAW CLAUDE_CONFIG_DIR
    string (after normal form C), so a relative path, a trailing slash, ``./`` or ``..`` each name a different
    item. Rewriting a folder here would name an item Claude Code never reads, and accepting several spellings
    of one folder would give it several names - so exactly one spelling is accepted and hashed as it is."""
    text = os.fspath(folder)
    # Never advise just retyping the folder another way: that names a different Keychain item from the one a
    # sign-in made under this spelling lives in. The way out is a sign-in made under the canonical spelling.
    if not text or not os.path.isabs(text):
        raise LicenseStoreError(
            f"{what} is {text!r}, a relative path. A relative CLAUDE_CONFIG_DIR is not supported for license "
            "switching: Claude Code names its sign-in by the folder exactly as typed, so the same folder written "
            "absolutely is a different sign-in.",
            ["Sign the account in again with CLAUDE_CONFIG_DIR set to an absolute folder, e.g. "
             "CLAUDE_CONFIG_DIR=/full/path/to/folder claude auth login, and pass that folder"],
        )
    canonical = os.path.normpath(text)
    if canonical == os.path.dirname(canonical):
        raise LicenseStoreError(f"{what} cannot be the root folder ({text!r})", ["Use a folder below the root"])
    if canonical != text:
        raise LicenseStoreError(
            f"{what} is {text!r}, which is not in its one canonical form (no trailing separator, no . or .. "
            "segment, no doubled separator). That spelling is not supported for license switching: Claude Code "
            "names its sign-in by the folder exactly as typed, so a sign-in made under it is not the one under "
            f"{canonical}.",
            [f"Sign the account in again with CLAUDE_CONFIG_DIR={canonical} claude auth login, and pass {canonical}"],
        )
    return text


def keychain_service_for(folder: str) -> str:
    """The Keychain service Claude Code uses for ``folder`` on macOS. ``folder`` must be the exact string
    CLAUDE_CONFIG_DIR is set to. Claude Code 2.1.296 hashes that string after Unicode normal form C and nothing
    else - its config folder is (CLAUDE_CONFIG_DIR ?? join(homedir(), ".claude")).normalize("NFC"), with no
    resolving - so this does exactly the same."""
    if is_real_default(folder):
        return KEYCHAIN_SERVICE
    digest = hashlib.sha256(unicodedata.normalize("NFC", folder).encode("utf-8")).hexdigest()
    return f"{KEYCHAIN_SERVICE}-{digest[:8]}"


def spare_slug(email: str) -> str:
    """The spare folder name for an account: its email, lower case, with anything unusual made a hyphen."""
    slug = re.sub(r"[^a-z0-9._-]+", "-", email.strip().lower()).strip("-.")
    if not slug:
        raise LicenseStoreError(f"cannot make a folder name from the email {email!r}", ["cc-devthrottle license list"])
    return slug


def _atomic_write_text(path: Path, text: str, private: bool) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    flags = os.O_WRONLY | os.O_CREAT | os.O_TRUNC | getattr(os, "O_BINARY", 0)
    fd = os.open(temp, flags, 0o600 if private else 0o644)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(text.encode("utf-8"))
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp, path)
    finally:
        if temp.exists():
            temp.unlink()
    if private and os.name != "nt":
        os.chmod(path, 0o600)


def _read_json_object(path: Path) -> Optional[dict]:
    if not path.is_file():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except ValueError:
        raise LicenseStoreError(f"{path} is not valid JSON", [f"Open {path} and repair it"]) from None
    if not isinstance(data, dict):
        raise LicenseStoreError(f"{path} does not hold a JSON object", [f"Open {path} and repair it"])
    return data


def read_oauth_account(folder: str) -> Optional[dict]:
    """The ``oauthAccount`` block of ``folder``'s account file, or None when there is none."""
    data = _read_json_object(account_file_for(folder))
    block = data.get("oauthAccount") if data else None
    return block if isinstance(block, dict) else None


def _email_of(block: Optional[dict]) -> Optional[str]:
    email = block.get("emailAddress") if block else None
    return email if isinstance(email, str) and email.strip() else None


def _write_oauth_account(folder: str, block: dict) -> None:
    path = account_file_for(folder)
    data = _read_json_object(path) or {}
    data["oauthAccount"] = block
    _atomic_write_text(path, json.dumps(data, indent=2), private=False)


def _clear_oauth_account(folder: str) -> None:
    path = account_file_for(folder)
    data = _read_json_object(path)
    if data is None or "oauthAccount" not in data:
        return
    del data["oauthAccount"]
    _atomic_write_text(path, json.dumps(data, indent=2), private=False)


# ---------------------------------------------------------------------------------------------------
# Key backends
# ---------------------------------------------------------------------------------------------------


class KeyBackend(Protocol):
    """Where a config folder's key is kept. ``name`` is ``file`` or ``keychain``."""

    name: str

    def location(self, folder: str) -> str: ...
    def exists(self, folder: str) -> bool: ...
    def read(self, folder: str) -> Optional[HeldKey]: ...
    def write(self, folder: str, key: HeldKey) -> None: ...
    def delete(self, folder: str) -> None: ...


class FileKeyBackend:
    """Windows and Linux: the key is ``<folder>/.credentials.json``."""

    name = "file"

    def location(self, folder: str) -> str:
        return str(Path(folder) / CREDENTIALS_FILE)

    def exists(self, folder: str) -> bool:
        return (Path(folder) / CREDENTIALS_FILE).is_file()

    def read(self, folder: str) -> Optional[HeldKey]:
        path = Path(folder) / CREDENTIALS_FILE
        if not path.is_file():
            return None
        text = path.read_text(encoding="utf-8")
        if not text.strip():
            raise LicenseStoreError(f"{path} is empty", ["Sign the account in again with: claude auth login"])
        return HeldKey(text)

    def write(self, folder: str, key: HeldKey) -> None:
        _atomic_write_text(Path(folder) / CREDENTIALS_FILE, key.reveal(), private=True)

    def delete(self, folder: str) -> None:
        path = Path(folder) / CREDENTIALS_FILE
        if path.exists():
            path.unlink()


@dataclass
class RunResult:
    """What a `security` call returned."""

    returncode: int
    stdout: str
    stderr: str


#: Runs `security` with these arguments and this standard input. Injectable so tests need no Mac.
SecurityRunner = Callable[[Sequence[str], Optional[str]], RunResult]


def _run_security(args: Sequence[str], stdin: Optional[str]) -> RunResult:
    proc = subprocess.run(["security", *args], input=stdin, capture_output=True, text=True, timeout=30)
    return RunResult(proc.returncode, proc.stdout or "", proc.stderr or "")


_ACCT_ATTRIBUTE = re.compile(r'"acct"<blob>="((?:[^"\\]|\\.)*)"')


class KeychainKeyBackend:
    """macOS: the key is a Keychain generic password, named as ``keychain_service_for`` says.

    A key value never reaches a command line, where any process could read it: it is written through
    ``security -i`` on standard input, hex encoded, the same way Claude Code writes it. The real default
    service is refused unless ``allow_real_default`` is passed.
    """

    name = "keychain"

    def __init__(self, runner: Optional[SecurityRunner] = None, allow_real_default: bool = False):
        self._run = runner or _run_security
        self._allow_real_default = allow_real_default

    def location(self, folder: str) -> str:
        return keychain_service_for(folder)

    def _service(self, folder: str) -> str:
        service = keychain_service_for(folder)
        if service == KEYCHAIN_SERVICE and not self._allow_real_default:
            raise LicenseStoreError(
                f"refusing to touch the Keychain item '{KEYCHAIN_SERVICE}': it is the machine's real Claude login",
                ["Pass an isolated folder with --default-config-dir"],
            )
        return service

    def _account(self, service: str) -> Optional[str]:
        result = self._run(["find-generic-password", "-s", service], None)
        if result.returncode == KEYCHAIN_NOT_FOUND:
            return None
        if result.returncode != 0:
            raise LicenseStoreError(
                f"the Keychain lookup of '{service}' failed (security exited {result.returncode})",
                ["security unlock-keychain", "cc-devthrottle license list"],
            )
        match = _ACCT_ATTRIBUTE.search(result.stdout)
        return match.group(1) if match else ""

    def exists(self, folder: str) -> bool:
        return self._account(self._service(folder)) is not None

    def read(self, folder: str) -> Optional[HeldKey]:
        service = self._service(folder)
        account = self._account(service)
        if account is None:
            return None
        result = self._run(["find-generic-password", "-s", service, "-w"], None)
        if result.returncode != 0:
            raise LicenseStoreError(
                f"the Keychain item '{service}' could not be read (security exited {result.returncode})",
                ["security unlock-keychain", "cc-devthrottle license list"],
            )
        secret = result.stdout.rstrip("\r\n")
        if not secret.strip():
            raise LicenseStoreError(f"the Keychain item '{service}' is empty", ["claude auth login"])
        return HeldKey(secret, keychain_account=account or None)

    def write(self, folder: str, key: HeldKey) -> None:
        service = self._service(folder)
        account = key.keychain_account or KEYCHAIN_DEFAULT_ACCOUNT
        existing = self._account(service)
        # An item is unique by service AND account: writing under a different account would leave two items
        # for one service, and which one Claude Code reads would be a guess. Same account: update in place.
        if existing is not None and existing != account:
            self._delete_service(service)
        payload = key.reveal().encode("utf-8").hex()
        command = f'add-generic-password -U -a "{account}" -s "{service}" -X "{payload}"\n'
        result = self._run(["-i"], command)
        if result.returncode != 0:
            raise LicenseStoreError(
                f"the Keychain item '{service}' could not be written (security exited {result.returncode})",
                ["security unlock-keychain", "cc-devthrottle license list"],
            )

    def _delete_service(self, service: str) -> None:
        for _ in range(16):
            result = self._run(["delete-generic-password", "-s", service], None)
            if result.returncode == KEYCHAIN_NOT_FOUND:
                return
            if result.returncode != 0:
                raise LicenseStoreError(
                    f"the Keychain item '{service}' could not be deleted (security exited {result.returncode})",
                    ["security unlock-keychain", "cc-devthrottle license list"],
                )
        raise LicenseStoreError(f"the Keychain still holds '{service}' after 16 deletes", ["cc-devthrottle license list"])

    def delete(self, folder: str) -> None:
        self._delete_service(self._service(folder))


def backend_for_platform(allow_real_default: bool = False) -> KeyBackend:
    """The Keychain on macOS, the credentials file everywhere else."""
    if sys.platform == "darwin":
        return KeychainKeyBackend(allow_real_default=allow_real_default)
    return FileKeyBackend()


# ---------------------------------------------------------------------------------------------------
# The store lock: one operation at a time, released by the system when a process dies
# ---------------------------------------------------------------------------------------------------


class _StoreLock:
    def __init__(self, path: Path):
        self._path = path
        self._handle = None
        self._depth = 0

    def __enter__(self) -> "_StoreLock":
        if self._depth == 0:
            self._path.parent.mkdir(parents=True, exist_ok=True)
            handle = open(self._path, "a+b")
            try:
                if os.name == "nt":
                    import msvcrt

                    handle.seek(0)
                    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl

                    fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError:
                handle.close()
                raise LicenseStoreError(
                    f"another license operation is running on this store (lock {self._path})",
                    ["Wait for it to finish, then run the command again"],
                    reason_code=REASON_SWITCH_IN_PROGRESS,
                ) from None
            self._handle = handle
        self._depth += 1
        return self

    def __exit__(self, *exc) -> None:
        self._depth -= 1
        if self._depth == 0 and self._handle is not None:
            if os.name == "nt":
                import msvcrt

                self._handle.seek(0)
                msvcrt.locking(self._handle.fileno(), msvcrt.LK_UNLCK, 1)
            self._handle.close()
            self._handle = None


# ---------------------------------------------------------------------------------------------------
# The store
# ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class HeldAccount:
    """One account this machine holds. Carries no key: ``read_key`` reads it when it is needed."""

    email: Optional[str]
    role: str
    config_dir: str
    account_file: str
    key_store: str
    key_location: str
    organization: Optional[str]
    _backend: KeyBackend = field(repr=False, compare=False)

    def read_key(self) -> HeldKey:
        """The account's key. Raises when the folder no longer holds one."""
        key = self._backend.read(self.config_dir)
        if key is None:
            raise LicenseStoreError(
                f"the folder {self.config_dir} no longer holds a sign-in key", ["cc-devthrottle license list"]
            )
        return key

    def to_json(self) -> dict:
        """Every field, for ``--json``. No key."""
        return {
            "email": self.email,
            "role": self.role,
            "organization": self.organization,
            "configDir": self.config_dir,
            "accountFile": self.account_file,
            "keyStore": self.key_store,
            "keyLocation": self.key_location,
        }


@dataclass(frozen=True)
class SwitchResult:
    """What a switch did. ``changed`` is False when the account was already active. ``exchange_milliseconds``
    times the exchange step alone (0 when it was not reached); the times are UTC."""

    active: str
    previous: Optional[str]
    previous_kept_in: Optional[str]
    changed: bool
    started_at: _dt.datetime
    finished_at: _dt.datetime
    exchange_milliseconds: int


Checkpoint = Callable[[str], None]


def _no_checkpoint(_label: str) -> None:
    return None


class LicenseStore:
    """The accounts one machine holds: the active one in the default config folder and the spares.

    ``default_config_dir`` and ``store_dir`` are always given by the caller. ``backend`` defaults to the
    platform's. ``checkpoint`` is called with a label before every change to a key, an account file or the
    journal; tests raise from it to simulate a crash at that exact point.
    """

    def __init__(
        self,
        default_config_dir: str | os.PathLike,
        store_dir: str | os.PathLike,
        backend: Optional[KeyBackend] = None,
        *,
        allow_real_default: bool = False,
        checkpoint: Optional[Checkpoint] = None,
    ):
        self.default_config_dir = canonical_folder(default_config_dir, "the default config folder")
        self.store_dir = canonical_folder(store_dir, "the store folder")
        if is_real_default(self.default_config_dir) and not allow_real_default:
            raise LicenseStoreError(
                f"refusing to use the machine's real default Claude folder ({self.default_config_dir}): "
                "this release only works on isolated folders",
                [f"Pass an isolated folder with --default-config-dir or {ENV_DEFAULT_CONFIG_DIR}"],
            )
        if _inside(self.default_config_dir, self.store_dir) or _inside(self.store_dir, self.default_config_dir):
            raise LicenseStoreError(
                "the store folder and the default config folder must not contain one another",
                ["Pass separate folders with --store-dir and --default-config-dir"],
            )
        self.backend = backend or backend_for_platform(allow_real_default)
        self._checkpoint = checkpoint or _no_checkpoint
        self._lock = _StoreLock(Path(self.store_dir) / LOCK_FILE)
        #: What the last recovery did, in one sentence, or None when there was nothing to recover.
        self.last_recovery: Optional[str] = None

    # ----- paths -----

    @property
    def spares_dir(self) -> str:
        return os.path.join(self.store_dir, SPARES_DIR)

    def spare_folder(self, email: str) -> str:
        return os.path.join(self.spares_dir, spare_slug(email))

    @property
    def _journal_path(self) -> Path:
        return Path(self.store_dir) / JOURNAL_FILE

    @property
    def _log_path(self) -> Path:
        return Path(self.store_dir) / LOG_FILE

    # ----- reading -----

    def _account_at(self, folder: str, role: str) -> HeldAccount:
        block = read_oauth_account(folder)
        org = block.get("organizationName") if block else None
        return HeldAccount(
            email=_email_of(block),
            role=role,
            config_dir=folder,
            account_file=str(account_file_for(folder)),
            key_store=self.backend.name,
            key_location=self.backend.location(folder),
            organization=org if isinstance(org, str) else None,
            _backend=self.backend,
        )

    def held_accounts(self) -> list[HeldAccount]:
        """Every account held: the active one first (when the default folder holds a key), then the spares by
        email. A spare folder with no key in it holds nothing and is not listed."""
        with self._lock:
            self.recover()
            return self._held_accounts()

    def _held_accounts(self) -> list[HeldAccount]:
        held: list[HeldAccount] = []
        if self.backend.exists(self.default_config_dir):
            held.append(self._account_at(self.default_config_dir, ROLE_ACTIVE))
        spares = []
        if os.path.isdir(self.spares_dir):
            for name in sorted(os.listdir(self.spares_dir)):
                folder = os.path.join(self.spares_dir, name)
                if os.path.isdir(folder) and self.backend.exists(folder):
                    spares.append(self._account_at(folder, ROLE_SPARE))
        spares.sort(key=lambda a: (a.email or "").lower())
        return held + spares

    def _find(self, email: str) -> Optional[HeldAccount]:
        wanted = email.strip().lower()
        for account in self._held_accounts():
            if account.email and account.email.lower() == wanted:
                return account
        return None

    # ----- the operations log -----

    def _log(self, event: str, **fields: object) -> None:
        record = {"time": _dt.datetime.now(_dt.timezone.utc).isoformat(timespec="seconds"), "event": event}
        record.update(fields)
        path = self._log_path
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(record) + "\n")

    # ----- the journal -----

    def _write_journal(self, journal: dict) -> None:
        self._checkpoint("journal")
        _atomic_write_text(self._journal_path, json.dumps(journal, indent=2), private=False)

    def _clear_journal(self) -> None:
        self._checkpoint("journal-clear")
        if self._journal_path.exists():
            self._journal_path.unlink()

    def _step_copy(self, source: str, target: str, replaces: Optional[str] = None) -> None:
        """Put ``source``'s key and account block into ``target``. Safe to run again after a crash part way.

        A copy never overwrites a sign-in it cannot prove is held elsewhere: ``target`` must be empty, already
        hold ``source``'s key (a step re-run after a crash), or hold the same key as ``replaces`` - the folder
        the journal says still holds what ``target`` had. Anything else (a login or a credential rewrite while
        the operation was interrupted) stops the step with nothing changed."""
        key = self.backend.read(source)
        block = read_oauth_account(source)
        if key is None:
            raise _StepFailed(f"{source} holds no sign-in key")
        if _email_of(block) is None:
            raise _StepFailed(f"{source} has no account block with an email")
        present = self.backend.read(target)
        if present is not None and not present.same_as(key):
            if replaces is None or not present.same_as(self.backend.read(replaces)):
                raise _StepFailed(
                    f"refusing to overwrite the key in {target}: it is not a sign-in this operation can prove is "
                    "held elsewhere"
                )
        os.makedirs(target, exist_ok=True)
        self._checkpoint("copy-key")
        self.backend.write(target, key)
        self._checkpoint("copy-account")
        _write_oauth_account(target, block)

    def _step_erase(self, folder: str, fingerprint: str, email: str) -> None:
        """Delete the ONE sign-in remove set out to delete - the key whose ``fingerprint`` the journal recorded -
        and its account block. Done already counts as done, so a crash between the two halves is finished by the
        next run. A different key now in the folder (a login after the crash) means the removal already
        happened; it is left alone, and so is an account block naming any account but ``email``."""
        key = self.backend.read(folder)
        if key is not None:
            if not hmac.compare_digest(key.fingerprint(), fingerprint):
                self._log("erase-left-alone", folder=folder, account=email,
                          reason="the folder now holds a different sign-in")
                return
            self._checkpoint("erase-key")
            self.backend.delete(folder)
        if (_email_of(read_oauth_account(folder)) or "").lower() == email.lower():
            self._checkpoint("erase-account")
            _clear_oauth_account(folder)

    def _step_drop(self, folder: str, kept_in: str) -> None:
        """Remove ``folder``'s key and account block, but only when ``kept_in`` holds the same key and account,
        so a sign-in is never deleted from its last place. Done already counts as done."""
        key = self.backend.read(folder)
        if key is None:
            # The key is gone already. Clear the account block only when it names the account that went to
            # kept_in: any other block belongs to a sign-in this step never touched.
            here, there = _email_of(read_oauth_account(folder)), _email_of(read_oauth_account(kept_in))
            if here is not None and there is not None and here.lower() == there.lower():
                self._checkpoint("drop-account")
                _clear_oauth_account(folder)
            return
        kept = self.backend.read(kept_in)
        if not key.same_as(kept):
            raise _StepFailed(f"refusing to delete the key in {folder}: {kept_in} does not hold the same key")
        here, there = _email_of(read_oauth_account(folder)), _email_of(read_oauth_account(kept_in))
        if here is not None and (there is None or here.lower() != there.lower()):
            raise _StepFailed(f"refusing to delete the key in {folder}: {kept_in} does not hold its account block")
        self._checkpoint("drop-key")
        self.backend.delete(folder)
        self._checkpoint("drop-account")
        _clear_oauth_account(folder)

    def _do_step(self, step: dict) -> None:
        if step["op"] == "copy":
            self._step_copy(step["from"], step["to"], step.get("replaces"))
        elif step["op"] == "drop":
            self._step_drop(step["folder"], step["keptIn"])
        elif step["op"] == "erase":
            self._step_erase(step["folder"], step["fingerprint"], step["account"])
        else:
            raise _StepFailed(f"the journal names an unknown step {step['op']!r}")

    def _run_journal(self, journal: dict) -> None:
        """Run the journal's steps from ``done`` on, recording each one as it completes. On a step that cannot
        go on, roll back the copies already made and raise."""
        steps = journal["steps"]
        while journal["done"] < len(steps):
            step = steps[journal["done"]]
            try:
                self._do_step(step)
            except _StepFailed as exc:
                self._roll_back(journal, str(exc))
                raise LicenseStoreError(
                    f"the {journal['operation']} of {journal['account']} could not finish and was rolled back: {exc}",
                    ["cc-devthrottle license list"],
                    reason_code=REASON_EXCHANGE_FAILED_RESTORED,
                ) from None
            journal["done"] += 1
            self._write_journal(journal)
        self._clear_journal()

    def _roll_back(self, journal: dict, reason: str) -> None:
        """Undo the steps already done, newest first. A step's undo is its own ``undo`` when the journal names
        one (bringing a spare into the default folder is undone by copying the old active sign-in back from
        where the first step put it), otherwise dropping the copy where its source still holds the same key.
        Progress is recorded, so a crash during the roll back resumes it. When an undo cannot be proven safe,
        stop with everything left in place and the journal kept."""
        if not journal.get("rollingBack"):
            journal["rollingBack"] = True
            journal["undone"] = 0
            journal["rollbackReason"] = reason
            self._write_journal(journal)
        done_steps = journal["steps"][: journal["done"]]
        for step in list(reversed(done_steps))[journal["undone"]:]:
            if step["op"] != "copy":
                self._log("rollback-impossible", operation=journal["operation"], account=journal["account"],
                          reason=reason)
                raise LicenseStoreError(
                    f"the {journal['operation']} of {journal['account']} stopped after a key had already been "
                    f"removed, and cannot be rolled back automatically ({reason}). Nothing more was changed; the "
                    f"journal is kept at {self._journal_path}.",
                    [f"Read {self._journal_path} and {self._log_path}", "cc-devthrottle license list"],
                    reason_code=REASON_EXCHANGE_FAILED_UNKNOWN,
                )
            undo = step.get("undo") or {"op": "drop", "folder": step["to"], "keptIn": step["from"]}
            try:
                self._do_step(undo)
                journal["undone"] += 1
                self._write_journal(journal)
            except _StepFailed as exc:
                self._log("rollback-impossible", operation=journal["operation"], account=journal["account"],
                          reason=str(exc))
                raise LicenseStoreError(
                    f"the {journal['operation']} of {journal['account']} could neither finish nor roll back "
                    f"({exc}). Nothing more was changed; the journal is kept at {self._journal_path}.",
                    [f"Read {self._journal_path} and {self._log_path}", "cc-devthrottle license list"],
                    reason_code=REASON_EXCHANGE_FAILED_UNKNOWN,
                ) from None
        self._clear_journal()
        self._log("rolled-back", operation=journal["operation"], account=journal["account"], reason=reason)

    def recover(self) -> Optional[str]:
        """Finish, or roll back, an operation a crash left unfinished. Every public method calls this first.
        Returns a sentence saying what was done (also kept in ``last_recovery``), or None."""
        with self._lock:
            if not self._journal_path.exists():
                return None
            journal = _read_json_object(self._journal_path)
            if not journal or not isinstance(journal.get("steps"), list) or not isinstance(journal.get("done"), int):
                raise LicenseStoreError(
                    f"the journal {self._journal_path} is not one this tool wrote; nothing was changed",
                    [f"Read {self._journal_path} and {self._log_path}"],
                    reason_code=REASON_EXCHANGE_FAILED_UNKNOWN,
                )
            self._log("recovery-started", operation=journal.get("operation"), account=journal.get("account"),
                      done=journal["done"], steps=len(journal["steps"]))
            try:
                if journal.get("rollingBack"):
                    if not isinstance(journal.get("undone"), int):
                        raise LicenseStoreError(
                            f"the journal {self._journal_path} is part way through a roll back but does not say how "
                            "far; nothing was changed",
                            [f"Read {self._journal_path} and {self._log_path}"],
                            reason_code=REASON_EXCHANGE_FAILED_UNKNOWN,
                        )
                    reason = str(journal.get("rollbackReason") or "an earlier failure")
                    self._roll_back(journal, reason)
                    raise LicenseStoreError(
                        f"the {journal.get('operation')} of {journal.get('account')} could not finish and was rolled "
                        f"back: {reason}",
                        ["cc-devthrottle license list"],
                        reason_code=REASON_EXCHANGE_FAILED_RESTORED,
                    )
                self._run_journal(journal)
            except LicenseStoreError as exc:
                self.last_recovery = f"an unfinished {journal.get('operation')} was found: {exc}"
                raise
            self._log("recovery-finished", operation=journal.get("operation"), account=journal.get("account"))
            self.last_recovery = (
                f"an unfinished {journal.get('operation')} of {journal.get('account')} was found and finished"
            )
            return self.last_recovery

    def _start(self, operation: str, account: str, steps: list[dict]) -> None:
        journal = {
            "operation": operation,
            "account": account,
            "startedAt": _dt.datetime.now(_dt.timezone.utc).isoformat(timespec="seconds"),
            "steps": steps,
            "done": 0,
        }
        self._log(f"{operation}-started", account=account, steps=steps)
        self._write_journal(journal)
        self._run_journal(journal)

    # ----- the operations -----

    def add(self, source_folder: str | os.PathLike) -> HeldAccount:
        """Take the sign-in from ``source_folder`` (a folder ``claude auth login`` was run into) as a spare. The
        key and account block MOVE: the source folder no longer holds them afterwards."""
        source = canonical_folder(source_folder, "the folder to add")
        with self._lock:
            self.recover()
            if is_real_default(source) or _same_folder(source, self.default_config_dir):
                raise LicenseStoreError(
                    f"{source} is the default config folder; a spare is added from a folder of its own",
                    ["CLAUDE_CONFIG_DIR=<new folder> claude auth login", "cc-devthrottle license add <new folder>"],
                )
            if _inside(source, self.store_dir):
                raise LicenseStoreError(
                    f"{source} is inside the store already", ["cc-devthrottle license list"]
                )
            if not self.backend.exists(source):
                raise LicenseStoreError(
                    f"{source} holds no sign-in key",
                    [f"Sign it in first: set CLAUDE_CONFIG_DIR={source} and run claude auth login"],
                )
            email = _email_of(read_oauth_account(source))
            if email is None:
                raise LicenseStoreError(
                    f"{account_file_for(source)} has no oauthAccount email, so the account is unknown",
                    [f"Sign it in again: set CLAUDE_CONFIG_DIR={source} and run claude auth login"],
                )
            held = self._find(email)
            if held is not None:
                raise LicenseStoreError(
                    f"{email} is already held here as the {held.role} account", ["cc-devthrottle license list"]
                )
            target = self.spare_folder(email)
            if self.backend.exists(target):
                raise LicenseStoreError(
                    f"the spare folder {target} already holds a key that is not {email}'s",
                    ["cc-devthrottle license list"],
                )
            self._start("add", email, [
                {"op": "copy", "from": source, "to": target},
                {"op": "drop", "folder": source, "keptIn": target},
            ])
            account = self._find(email)
            if account is None or account.role != ROLE_SPARE or self.backend.exists(source):
                self._log("add-verify-failed", account=email)
                raise LicenseStoreError(
                    f"the add of {email} finished but reading it back did not show it as a spare",
                    ["cc-devthrottle license list"],
                )
            self._log("add-finished", account=email, folder=target)
            return account

    def remove(self, email: str) -> HeldAccount:
        """Delete a spare's sign-in. The active account cannot be removed this way."""
        with self._lock:
            self.recover()
            account = self._find(email)
            if account is None:
                raise LicenseStoreError(f"{email} is not held here", ["cc-devthrottle license list"])
            if account.role != ROLE_SPARE:
                raise LicenseStoreError(
                    f"{email} is the active account; switch to another account first",
                    ["cc-devthrottle license switch <email>", "cc-devthrottle license list"],
                )
            # Journaled like every other change, so a crash between deleting the key and clearing the account
            # block is finished by the next run instead of leaving the block behind without its key.
            fingerprint = account.read_key().fingerprint()
            self._start("remove", account.email,
                        [{"op": "erase", "folder": account.config_dir, "fingerprint": fingerprint,
                          "account": account.email}])
            self._log("remove-finished", account=account.email)
            return account

    def active_account_email(self) -> Optional[str]:
        """The email of the account in the default folder, read now without the lock; None when it holds no key
        or no account block. For reporting what is there after a refusal - never a reason to act."""
        if not self.backend.exists(self.default_config_dir):
            return None
        return _email_of(read_oauth_account(self.default_config_dir))

    def switch(self, email: str, expect_active: Optional[str] = None) -> SwitchResult:
        """EXCHANGE the active sign-in with the spare for ``email``: the active key and account block go to the
        old account's spare folder, and the spare's come into the default folder. Verified by reading back.

        The checks run in the contract's order (contract.md section 3.3): already active, then
        ``expect_active`` (when given, the active account must be that one, else active-account-changed), then
        the spare must be held. Every failure carries its reason code and the active account read before."""
        started = _dt.datetime.now(_dt.timezone.utc)
        with self._lock:
            previous: Optional[str] = None
            try:
                self.recover()
                previous = self.active_account_email()
                return self._switch(email, expect_active, previous, started)
            except LicenseStoreError as exc:
                exc.previous_active = previous
                raise

    def _switch(self, email: str, expect_active: Optional[str], previous: Optional[str],
                started: _dt.datetime) -> SwitchResult:
        if previous is not None and previous.lower() == email.strip().lower():
            self._log("switch-unchanged", account=previous)
            return SwitchResult(active=previous, previous=previous, previous_kept_in=None, changed=False,
                                started_at=started, finished_at=_dt.datetime.now(_dt.timezone.utc),
                                exchange_milliseconds=0)
        if expect_active is not None and (previous or "").lower() != expect_active.strip().lower():
            self._log("switch-refused", account=email, reason=REASON_ACTIVE_ACCOUNT_CHANGED,
                      expected=expect_active, active=previous)
            raise LicenseStoreError(
                f"the active account is {previous or 'none'}, not {expect_active} as expected; nothing was changed",
                ["cc-devthrottle license list"],
                reason_code=REASON_ACTIVE_ACCOUNT_CHANGED,
            )
        target = self._find(email)
        if target is None or target.role != ROLE_SPARE:
            self._log("switch-refused", account=email, reason=REASON_SPARE_NOT_HELD)
            raise LicenseStoreError(f"{email} is not held here as a spare", ["cc-devthrottle license list"],
                                    reason_code=REASON_SPARE_NOT_HELD)
        steps: list[dict] = []
        previous_folder = None
        if self.backend.exists(self.default_config_dir):
            if previous is None:
                raise LicenseStoreError(
                    f"the default folder holds a key but {account_file_for(self.default_config_dir)} has no "
                    "oauthAccount email, so the key's account is unknown and it cannot be put away; nothing was "
                    "changed",
                    ["claude auth status", "claude auth login"],
                    reason_code=REASON_EXCHANGE_FAILED_RESTORED,
                )
            previous_folder = self.spare_folder(previous)
            if self.backend.exists(previous_folder):
                raise LicenseStoreError(
                    f"{previous} is active and a spare for it is held too ({previous_folder}); putting the active "
                    "key away would overwrite that sign-in, so nothing was changed",
                    [f"cc-devthrottle license remove {previous}", "cc-devthrottle license list"],
                    reason_code=REASON_EXCHANGE_FAILED_RESTORED,
                )
            steps.append({"op": "copy", "from": self.default_config_dir, "to": previous_folder})
        bring_in = {"op": "copy", "from": target.config_dir, "to": self.default_config_dir}
        if previous_folder is not None:
            # Overwrites the default folder only while it still holds the key the first step put away.
            bring_in["replaces"] = previous_folder
            # Undone by putting the old active sign-in back, never by emptying the default folder (that would
            # sign the machine out), and only while the default folder still holds the spare's key.
            bring_in["undo"] = {"op": "copy", "from": previous_folder, "to": self.default_config_dir,
                                "replaces": target.config_dir}
        steps.append(bring_in)
        steps.append({"op": "drop", "folder": target.config_dir, "keptIn": self.default_config_dir})
        exchange_started = _dt.datetime.now(_dt.timezone.utc)
        try:
            self._start("switch", target.email, steps)
        except OSError as exc:
            # A disk or Keychain failure part way. The journal says how far it got; finishing or rolling back
            # now means the caller learns the real outcome instead of leaving it to the next run.
            self._log("switch-interrupted", account=target.email, error=type(exc).__name__)
            try:
                self.recover()
            except OSError:
                raise LicenseStoreError(
                    f"the switch to {target.email} stopped part way ({type(exc).__name__}) and could not be "
                    f"finished or rolled back yet; the journal is kept at {self._journal_path} for the next run",
                    [f"Read {self._log_path}", "cc-devthrottle license list"],
                    reason_code=REASON_EXCHANGE_FAILED_UNKNOWN,
                ) from None
        exchange_ms = int((_dt.datetime.now(_dt.timezone.utc) - exchange_started).total_seconds() * 1000)
        self._verify_switch(target.email, previous, previous_folder, target.config_dir)
        self._log("switch-finished", account=target.email, previous=previous, exchange_milliseconds=exchange_ms)
        return SwitchResult(active=target.email, previous=previous, previous_kept_in=previous_folder, changed=True,
                            started_at=started, finished_at=_dt.datetime.now(_dt.timezone.utc),
                            exchange_milliseconds=exchange_ms)

    def _verify_switch(self, email: str, previous: Optional[str], previous_folder: Optional[str], spare: str) -> None:
        problems = []
        active = _email_of(read_oauth_account(self.default_config_dir))
        if not self.backend.exists(self.default_config_dir) or (active or "").lower() != email.lower():
            problems.append(f"the default folder reads back as {active or 'no account'}, not {email}")
        if self.backend.exists(spare):
            problems.append(f"{spare} still holds a key")
        if previous_folder is not None:
            kept = _email_of(read_oauth_account(previous_folder))
            if not self.backend.exists(previous_folder) or (kept or "").lower() != (previous or "").lower():
                problems.append(f"{previous_folder} reads back as {kept or 'no account'}, not {previous}")
        if problems:
            self._log("switch-verify-failed", account=email, problems=problems)
            raise LicenseStoreError(
                "the switch finished but did not read back correctly: " + "; ".join(problems),
                ["cc-devthrottle license list", f"Read {self._log_path}"],
                reason_code=REASON_VERIFY_FAILED,
            )

    def store_refreshed_key(self, email: str, credentials_json: str) -> None:
        """Write a refreshed key for ``email`` back into the folder that holds it (not a copy: it replaces the
        old key in the same place). ``credentials_json`` is the whole credentials document."""
        with self._lock:
            self.recover()
            account = self._find(email)
            if account is None:
                raise LicenseStoreError(f"{email} is not held here", ["cc-devthrottle license list"])
            old = account.read_key()
            self._checkpoint("refresh-key")
            self.backend.write(account.config_dir, HeldKey(credentials_json, keychain_account=old.keychain_account))
            self._log("key-refreshed", account=account.email)


def _inside(child: str, parent: str) -> bool:
    """True when ``child`` is ``parent`` or below it."""
    c = os.path.normcase(os.path.realpath(child))
    p = os.path.normcase(os.path.realpath(parent))
    return c == p or c.startswith(p.rstrip(os.sep) + os.sep)
