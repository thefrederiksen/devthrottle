"""Command line for cc-secrets.

Agents call `list`, `run` and `login`. None of them ever prints a secret: every line of output passes
through the process-wide scrubber, and the commands that act with a secret hand back only the result.

The owner calls `add` and `remove` from their own terminal. Those commands refuse to run inside a
DevThrottle session, and `add` takes the secret only from a hidden prompt or piped on standard input -
never as a command-line argument, where it would land in shell history and the process list.

No error is ever printed as a traceback. The console-script entry point is `main`, which shows an
unexpected error by its type name alone: a traceback's messages and local variables can hold the whole
store. Typer's own pretty tracebacks, which print local variables, are switched off as well.
"""

from __future__ import annotations

import getpass
import json
import os
import re
import sys
import traceback
import warnings
from pathlib import Path
from typing import List, NoReturn, Optional
from urllib.parse import urlsplit

import typer
from rich.console import Console
from rich.table import Table

from . import _console  # noqa: F401  (installs the ASCII-only output patches)
from . import __version__, filelog, paths
from .audit import AuditLog
from .browser_login import OUTCOME_LOGGED_IN, OUTCOME_REFUSED, login as browser_login
from .errors import CcSecretsError, InputError
from .redact import SCRUBBER
from .runner import DEFAULT_ENV_NAME, VIA_CHOICES, run_with_secrets
from .store import (ENV_NAME_PATTERN, KIND_SECRET, KIND_SETTING, MIN_SECRET_LENGTH, USES, EntryNotAvailableError,
                    SecretStore, make_entry, validate_name)
from .storefile import UserOnlyFile

# Make cc_shared importable when running from source, matching the other cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

PROTECTION = ("It protects against accidental exposure (transcripts, logs, output, screenshots), not against a "
              "hostile program running as the same user.")

app = typer.Typer(
    name="cc-secrets",
    help="Use a stored password without the model ever seeing it. Agents: list, run, login. Owner: add, remove. "
         "Anyone: log. " + PROTECTION,
    add_completion=False,
    no_args_is_help=True,
    pretty_exceptions_enable=False,
    pretty_exceptions_show_locals=False,
)
console = Console()

EXIT_FAILED = 1
EXIT_REFUSED = 2

MINTTY_MESSAGE = (
    "This terminal (Git Bash, or another mintty window) cannot hide what you type, so cc-secrets will not "
    "read a secret from it. Run 'cc-secrets add' from PowerShell or cmd, or pipe the secret in, for example: "
    "<password manager command> | cc-secrets add NAME --username USER --domains https://example.com --agents"
)

_MSYS_PTY_PIPE = re.compile(r"\\(?:msys|cygwin)-[0-9a-f]+-pty\d+-(?:from|to)-master", re.IGNORECASE)


def _say(text: str, err: bool = False) -> None:
    stream = sys.stderr if err else sys.stdout
    stream.write(SCRUBBER.scrub(text) + "\n")
    stream.flush()


def _say_json(payload: object) -> None:
    _say(json.dumps(payload, indent=2))


def _describe(exc: BaseException) -> str:
    """What may be shown about an error: a message cc-secrets wrote itself, or only the type of anything else."""
    if isinstance(exc, CcSecretsError):
        return SCRUBBER.scrub(str(exc))
    return f"an unexpected {type(exc).__name__} (details, without any store content, are in the cc-secrets tool log)"


def _log_failure(where: str, exc: BaseException) -> None:
    """Log where it failed: the type and the stack of code lines, never the message or local variables."""
    stack = "".join(traceback.format_list(traceback.extract_tb(exc.__traceback__)))
    message = f": {exc}" if isinstance(exc, CcSecretsError) else ""
    filelog.write(f"[cli] {where} FAILED: {type(exc).__name__}{message}\n{stack}")


def _store() -> SecretStore:
    return SecretStore(UserOnlyFile(paths.store_path()))


def _audit() -> AuditLog:
    return AuditLog(paths.audit_path())


def _fail(command: str, name: str, label: str, exc: BaseException) -> NoReturn:
    _log_failure(command, exc)
    _audit().record(name, label, "failed", _describe(exc))
    _say(f"failed: {_describe(exc)}", err=True)
    raise typer.Exit(EXIT_FAILED)


def _owner_only(command: str) -> None:
    """Owner commands are refused inside a DevThrottle session: a secret is never entered through one."""
    if os.environ.get("CC_SESSION_ID"):
        _say(f"'cc-secrets {command}' is for the owner, in their own terminal. It does not run inside a "
             "DevThrottle session, because a secret must never be entered through one.", err=True)
        raise typer.Exit(EXIT_REFUSED)


def _stdin_is_tty() -> bool:
    return sys.stdin.isatty()


def is_msys_pty_pipe_name(name: str) -> bool:
    """True for the pipe a Git Bash (mintty) window connects to a program's standard input."""
    return bool(_MSYS_PTY_PIPE.search(name or ""))


def _stdin_is_mintty() -> bool:
    """True when standard input is a Git Bash or Cygwin terminal window rather than a real pipe.

    mintty is not a Windows console, so a program's standard input there is a named pipe and does not
    count as a terminal - yet the person is typing into it, and it shows every character. The pipe's
    name tells the two apart.
    """
    if sys.platform != "win32":
        return False
    import ctypes
    import msvcrt
    from ctypes import wintypes

    try:
        handle = msvcrt.get_osfhandle(sys.stdin.fileno())
    except (OSError, ValueError, AttributeError):
        return False  # standard input has no operating-system handle (an in-memory stream)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetFileInformationByHandleEx.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel32.GetFileInformationByHandleEx.restype = wintypes.BOOL
    FILE_NAME_INFO_CLASS = 2
    buffer = ctypes.create_string_buffer(4 + 4096)
    if not kernel32.GetFileInformationByHandleEx(handle, FILE_NAME_INFO_CLASS, buffer, len(buffer)):
        return False
    length = int.from_bytes(buffer.raw[:4], "little")
    return is_msys_pty_pipe_name(buffer.raw[4:4 + length].decode("utf-16-le", "replace"))


def _read_secret_from_owner() -> str:
    """The secret from a hidden prompt (typed twice), or piped on standard input."""
    if not _stdin_is_tty():
        piped = sys.stdin.read()
        secret = piped[:-1] if piped.endswith("\n") else piped
        secret = secret[:-1] if secret.endswith("\r") else secret
        if "\n" in secret:
            raise CcSecretsError("The piped secret has more than one line. Pipe exactly one line.")
        return secret
    with warnings.catch_warnings():
        # getpass falls back to a VISIBLE prompt when it cannot hide input; make that an error instead.
        warnings.simplefilter("error", getpass.GetPassWarning)
        first = getpass.getpass("Secret (hidden): ")
        second = getpass.getpass("Secret again: ")
    if first != second:
        raise CcSecretsError("The two entries did not match. Nothing was saved.")
    return first


def _split_list(value: str) -> List[str]:
    return [part.strip() for part in value.split(",") if part.strip()]


@app.command()
def add(
    name: str = typer.Argument(..., help="Entry name, for example devlinux or github-work."),
    username: Optional[str] = typer.Option(None, "--username", help="The user name that goes with the secret."),
    domains: Optional[str] = typer.Option(None, "--domains", help="Comma-separated site addresses login may fill, for example https://example.com,https://*.example.com,http://127.0.0.1:8080. No scheme means https; scheme and port must match exactly."),
    notes: Optional[str] = typer.Option(None, "--notes", help="A note for yourself. Agents see it in list."),
    agents: Optional[bool] = typer.Option(None, "--agents/--no-agents", help="Whether sessions on this machine may use it."),
    uses: Optional[str] = typer.Option(None, "--uses", help="Comma-separated: login, run. Default both."),
    replace: bool = typer.Option(False, "--replace", help="Replace an existing entry without asking."),
    setting: bool = typer.Option(False, "--setting", help="Store a setting that is not secret (a host, an email address): readable with get, not hidden from output."),
    env_name: Optional[str] = typer.Option(None, "--env-name", help="The variable run supplies it in. Default CC_SECRET."),
):
    """OWNER: add or replace an entry. The secret comes from a hidden prompt, or piped on stdin."""
    _owner_only("add")
    interactive = _stdin_is_tty()
    if not interactive and _stdin_is_mintty():
        _say(MINTTY_MESSAGE, err=True)
        raise typer.Exit(EXIT_REFUSED)
    try:
        if not interactive and (username is None or domains is None or agents is None):
            _say("With the secret piped on stdin there is no prompt for the other fields: pass --username, "
                 "--domains and --agents or --no-agents.", err=True)
            raise typer.Exit(EXIT_FAILED)
        store = _store()
        if store.get(name) is not None and not replace:
            if not interactive or not typer.confirm(f"'{name}' already exists. Replace it?", default=False):
                _say(f"'{name}' already exists. Nothing was changed (use --replace to replace it).", err=True)
                raise typer.Exit(EXIT_FAILED)
        if username is None:
            username = typer.prompt("Username", default="", show_default=False)
        if domains is None:
            domains = typer.prompt("Allowed site addresses for login (comma-separated, blank for none)", default="", show_default=False)
        if notes is None:
            notes = typer.prompt("Notes", default="", show_default=False) if interactive else ""
        if agents is None:
            agents = typer.confirm("May sessions on this machine use it?", default=False)
        use_list = _split_list(uses) if uses is not None else list(USES)
        secret = _read_secret_from_owner()
        if not setting:
            SCRUBBER.add(secret, username)
        entry = make_entry(name, username, secret, _split_list(domains), notes, agents, use_list,
                           env_name=env_name or "", kind=KIND_SETTING if setting else KIND_SECRET)
        replaced = store.put(entry)
        _audit().record(name, "add", "ok", "replaced" if replaced else "added")
        _say(f"{'Replaced' if replaced else 'Added'} '{name}' in {store.location}. "
             f"Agents may use it: {'yes' if entry.agents_may_use else 'no'}. Uses: {', '.join(entry.uses)}. "
             f"Allowed addresses: {', '.join(entry.allowed_domains) or 'none'}.")
    except typer.Exit:
        raise
    except Exception as exc:
        _log_failure("add", exc)
        _say(f"add failed: {_describe(exc)}", err=True)
        raise typer.Exit(EXIT_FAILED)


@app.command()
def remove(
    name: str = typer.Argument(..., help="Entry name."),
    yes: bool = typer.Option(False, "--yes", help="Do not ask for confirmation."),
):
    """OWNER: remove an entry."""
    _owner_only("remove")
    try:
        if not yes and not typer.confirm(f"Remove '{name}'?", default=False):
            raise typer.Exit(EXIT_FAILED)
        if not _store().remove(name):
            _say(f"There is no entry named '{name}'.", err=True)
            raise typer.Exit(EXIT_FAILED)
        _audit().record(name, "remove", "ok")
        _say(f"Removed '{name}'.")
    except typer.Exit:
        raise
    except Exception as exc:
        _log_failure("remove", exc)
        _say(f"remove failed: {_describe(exc)}", err=True)
        raise typer.Exit(EXIT_FAILED)


@app.command("list")
def list_entries(
    all_entries: bool = typer.Option(False, "--all", help="OWNER: include entries agents may not use."),
    json_output: bool = typer.Option(False, "--json", help="Print JSON."),
):
    """Show the entries agents may use: names, usernames, allowed addresses. Never secrets."""
    if all_entries:
        _owner_only("list --all")
    try:
        store = _store()
        entries = sorted(store.entries(), key=lambda e: e.name) if all_entries else store.agent_entries()
        views = [e.public_view() for e in entries]
        for view, entry in zip(views, entries):
            if entry.is_setting:
                view["value"] = entry.secret.reveal()
        if json_output:
            _say_json({"entries": views})
            return
        if not views:
            _say("No secrets are available to agents on this machine." if not all_entries else "The store is empty.")
            return
        table = Table(show_lines=False)
        for column in ("Name", "Kind", "Variable", "Value", "Username", "Allowed addresses", "Uses") + (("Agents",) if all_entries else ()) + ("Notes",):
            table.add_column(column)
        for v in views:
            row = [v["name"], v["kind"], v["envName"], v.get("value", ""), v["username"], ", ".join(v["allowedDomains"]),
                   ", ".join(v["uses"])]
            if all_entries:
                row.append("yes" if v["agentsMayUse"] else "no")
            row.append(v["notes"])
            table.add_row(*[SCRUBBER.scrub(str(c)) for c in row])
        console.print(table)
    except Exception as exc:
        _log_failure("list", exc)
        _say(f"list failed: {_describe(exc)}", err=True)
        raise typer.Exit(EXIT_FAILED)


@app.command()
def get(
    name: str = typer.Argument(..., help="Setting name."),
    json_output: bool = typer.Option(False, "--json", help="Print JSON."),
):
    """Print a SETTING's value (a host, an email address). A secret is refused: it is never printed."""
    try:
        entry = _store().setting_for_agent(name)
    except EntryNotAvailableError as exc:
        _refused(name, "get", exc, json_output)
    except Exception as exc:
        _fail("get", name, "get", exc)
    _audit().record(name, "get", "ok")
    value = entry.secret.reveal()
    if json_output:
        _say_json({"entry": name, "value": value})
    else:
        _say(value)


def _refused(name: str, label: str, exc: EntryNotAvailableError, json_output: bool) -> NoReturn:
    _audit().record(name, label, "refused", str(exc))
    if json_output:
        _say_json({"entry": name, "outcome": OUTCOME_REFUSED, "reason": _describe(exc)})
    else:
        _say(f"refused: {_describe(exc)}", err=True)
    raise typer.Exit(EXIT_REFUSED)


@app.command()
def run(
    name: str = typer.Argument(..., help="Entry name."),
    command: Optional[List[str]] = typer.Argument(None, help="The command, after '--'."),
    also: Optional[List[str]] = typer.Option(None, "--with", help="Another entry to supply at the same time, in its own variable. Repeatable. Supplies every entry by env."),
    via: Optional[str] = typer.Option(None, "--via", help=f"How the command receives the secret: {', '.join(VIA_CHOICES)}. Default: env when the entry has its own variable name or --with is used, otherwise stdin."),
    env_name: Optional[str] = typer.Option(None, "--env-name", help="The variable name for --via env with one entry. Default: the entry's own variable name, otherwise CC_SECRET."),
    timeout: float = typer.Option(600, "--timeout", help="Seconds before the command is stopped. 0 means no limit."),
    json_output: bool = typer.Option(False, "--json", help="Print JSON."),
):
    """Run a command with the secret supplied. Returns its exit code and output, with the secret removed.

    Examples: cc-secrets run devlinux -- sudo -S apt-get update
              cc-secrets run godaddy-key --with godaddy-secret -- python dns.py
    """
    names = [name] + list(also or [])
    label = "run " + " ".join(command or [])[:200]
    entries = []
    for entry_name in names:
        try:
            entries.append(_store().entry_for_agent(entry_name, "run"))
        except EntryNotAvailableError as exc:
            _refused(entry_name, label, exc, json_output)
        except Exception as exc:
            _fail("run", entry_name, label, exc)
    try:
        if len(entries) > 1:
            if via not in (None, "env"):
                raise InputError("Several entries can only be supplied with --via env.")
            if env_name is not None:
                raise InputError("--env-name names the variable of ONE entry. With --with, each entry uses its own variable name.")
            chosen_via = "env"
        else:
            chosen_via = via or ("env" if entries[0].env_name else "stdin")
        supplied = []
        for entry in entries:
            if len(entries) == 1:
                variable = env_name or entry.env_name or DEFAULT_ENV_NAME
            else:
                variable = entry.env_name or entry.name.upper().replace("-", "_").replace(".", "_")
            if not ENV_NAME_PATTERN.match(variable):
                raise InputError(f"'{variable}' is not a valid environment variable name.")
            supplied.append((entry, variable))
        result = run_with_secrets(supplied, command or [], chosen_via, timeout)
    except Exception as exc:
        _fail("run", name, label, exc)
    outcome = "ok" if result.exit_code == 0 and not result.timed_out else "failed"
    detail = f"exit {result.exit_code}" + (", timed out" if result.timed_out else "")
    for entry in entries:
        _audit().record(entry.name, label, outcome, detail)
    if json_output:
        _say_json({"entry": name, "entries": names, "exitCode": result.exit_code, "timedOut": result.timed_out,
                   "stdout": result.stdout, "stderr": result.stderr})
    else:
        if result.stdout:
            sys.stdout.write(SCRUBBER.scrub(result.stdout))
            sys.stdout.flush()
        if result.stderr:
            sys.stderr.write(SCRUBBER.scrub(result.stderr))
            sys.stderr.flush()
        _say(f"[cc-secrets] command exited {result.exit_code}" + (" (timed out)" if result.timed_out else ""), err=True)
    raise typer.Exit(result.exit_code if result.exit_code != 0 else (EXIT_FAILED if result.timed_out else 0))


def register_env_file_values(text: str, settings: frozenset = frozenset()) -> None:
    """Hand every value of a KEY=VALUE file to the scrubber BEFORE the file is checked. A malformed line is then
    reported by its number alone, and any key that happens to be another line's value is hidden wherever it is
    printed (review of pull request 2978). Keys themselves are not registered, so the report stays readable."""
    for raw in text.splitlines():
        if "=" in raw:
            key, value = raw.split("=", 1)
            if key.strip() in settings:
                continue  # declared a setting by the owner: not secret, and not to be hidden
            if value.strip():
                SCRUBBER.add(value)


def parse_env_file(text: str) -> List[tuple]:
    """[(line number, KEY, VALUE)] from KEY=VALUE text. Blank lines and lines starting with # are skipped.

    The value is everything after the first '=', exactly - never trimmed, because a credential can hold
    spaces; a value that starts or ends with a space is refused instead of guessed at. A line that is not
    KEY=VALUE, a key that is not a variable name, a key given twice, and two keys that would become the same
    entry name are refused naming the LINES only - no text from the file is ever put in a message."""
    pairs = []
    seen_keys = {}
    seen_names = {}
    for number, raw in enumerate(text.splitlines(), start=1):
        stripped = raw.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if "=" not in raw:
            raise InputError(f"Line {number} is not KEY=VALUE.")
        key, value = raw.split("=", 1)
        if key != key.strip() or not ENV_NAME_PATTERN.match(key):
            raise InputError(f"Line {number}: the part before '=' is not a variable name (letters, digits and "
                             "underscores, nothing around it).")
        if value != value.strip():
            raise InputError(f"Line {number}: the value starts or ends with a space. Remove the space, or if the "
                             "credential really has one, add that entry by hand with cc-secrets add.")
        if key in seen_keys:
            raise InputError(f"Line {number}: the same key was already given on line {seen_keys[key]}.")
        name = entry_name_for_key(key)
        if name in seen_names:
            raise InputError(f"Lines {seen_names[name]} and {number} would both become the same entry name. "
                             "Rename one of the keys.")
        seen_keys[key] = number
        seen_names[name] = number
        pairs.append((number, key, value))
    return pairs


def _folded(text: str) -> str:
    return text.lower().replace("-", "_")


def commented_values(text: str) -> List[str]:
    """Values on commented-out KEY=VALUE lines - old credentials are often kept that way. Each comes both exactly
    and trimmed: a comment is not parsed strictly, so '# OLD= value' means the value without the space (review of
    pull request 2978)."""
    found = []
    for line in (raw.strip() for raw in text.splitlines()):
        if line.startswith("#") and "=" in line:
            value = line.split("=", 1)[1]
            found.extend({value, value.strip()})
    return found


def check_keys_hold_no_secret(pairs: List[tuple], skipped: set, protected: List[str]) -> None:
    """Refuse, by line number, any key to be imported that contains a secret.

    A key becomes a public entry name and variable name - listed to sessions, printed, audited - so one that
    holds a secret, in any letter case and with - or _, would carry it there (review of pull request 2978).
    `protected` is every secret that must not appear: the store's, and this file's values that are not skipped
    settings, commented-out lines included. Values shorter than the shortest secret cc-secrets stores are not
    secrets it holds, and would match half of all keys, so they are not compared."""
    guarded = [(_folded(value), value) for value in protected if len(value) >= MIN_SECRET_LENGTH]
    for number, key, _ in pairs:
        if key in skipped:
            continue
        if any(folded in _folded(key) for folded, _ in guarded):
            raise InputError(f"Line {number}: the key contains a stored or imported secret. Rename the key.")


def entry_name_for_key(key: str) -> str:
    """The entry name an imported KEY gets: lower case, underscores as hyphens (POSTHOG_API_KEY -> posthog-api-key)."""
    return key.lower().replace("_", "-")


@app.command("import")
def import_entries(
    file: Path = typer.Argument(..., help="A KEY=VALUE file, for example credentials.env. Blank lines and # comments are skipped."),
    agents: Optional[bool] = typer.Option(None, "--agents/--no-agents", help="Whether sessions on this machine may use the imported entries. Required."),
    uses: str = typer.Option("run", "--uses", help="Comma-separated: login, run. Default run."),
    settings: Optional[str] = typer.Option(None, "--settings", help="Comma-separated keys to import as SETTINGS - not secret (hosts, email addresses, identifiers): readable with get and not hidden from output."),
    skip: Optional[str] = typer.Option(None, "--skip", help="Comma-separated keys NOT to import."),
    replace: bool = typer.Option(False, "--replace", help="Replace entries that already exist."),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would happen, by name only, and change nothing."),
):
    """OWNER: import every KEY=VALUE line of a file as its own entry. Each entry is named after its key
    (POSTHOG_API_KEY becomes posthog-api-key) and `run` supplies it in a variable of that same name. No value is
    ever printed."""
    _owner_only("import")
    try:
        if agents is None:
            _say("Say whether sessions may use the imported entries: --agents or --no-agents.", err=True)
            raise typer.Exit(EXIT_FAILED)
        use_list = _split_list(uses)
        skipped = set(_split_list(skip or ""))
        as_settings = set(_split_list(settings or ""))
        both = sorted(skipped & as_settings)
        if both:
            _say(f"Keys given to both --skip and --settings: {', '.join(both)}. Nothing was imported.", err=True)
            raise typer.Exit(EXIT_FAILED)
        text = file.read_text(encoding="utf-8-sig")
        register_env_file_values(text, frozenset(as_settings))
        for value in commented_values(text):
            if value.strip():
                SCRUBBER.add(value)
        pairs = parse_env_file(text)
        unknown = sorted((skipped | as_settings) - {key for _, key, _ in pairs})
        if unknown:
            _say(f"--skip or --settings names keys that are not in the file: {', '.join(unknown)}. Nothing was imported.", err=True)
            raise typer.Exit(EXIT_FAILED)
        store = _store()
        stored = store.entries()
        protected = [e.secret.reveal() for e in stored if not e.is_setting] + commented_values(text) + \
            [value for _, key, value in pairs if key not in skipped and key not in as_settings]
        check_keys_hold_no_secret(pairs, skipped, protected)
        notes = f"imported from {file.name}"
        built, failed = [], {}
        for _, key, value in pairs:
            if key in skipped:
                continue
            try:
                name = validate_name(entry_name_for_key(key))
                kind = KIND_SETTING if key in as_settings else KIND_SECRET
                built.append(make_entry(name, "", value, [], notes, agents, use_list, env_name=key, kind=kind))
            except InputError as exc:
                failed[key] = str(exc)
        existing = {e.name for e in stored}
        if dry_run:
            table = Table(show_lines=False)
            for column in ("Key", "Entry", "Would be"):
                table.add_column(column)
            for _, key, _ in pairs:
                if key in skipped:
                    row = (key, "", "skipped (setting)")
                elif key in failed:
                    row = (key, "", "NOT imported: " + failed[key])
                else:
                    entry_name = entry_name_for_key(key)
                    state = ("replaced" if replace else "left as it is (exists)") if entry_name in existing else "added"
                    kind = "setting" if key in as_settings else "secret"
                    row = (key, entry_name, f"{state} as a {kind}")
                # Scrubbed like every other line: a key can be the same text as another line's value.
                table.add_row(*[SCRUBBER.scrub(cell) for cell in row])
            console.print(table)
            _say(f"Dry run: nothing was changed. {len(built)} to import, {len(skipped)} skipped, {len(failed)} not importable.")
            raise typer.Exit(EXIT_FAILED if failed else 0)
        outcomes = store.put_many(built, replace) if built else {}
        for entry in built:
            _audit().record(entry.name, "import", "ok" if outcomes[entry.name] != "exists" else "unchanged",
                            f"{outcomes[entry.name]} from {file.name} as {entry.env_name}")
        counts = {k: sum(1 for o in outcomes.values() if o == k) for k in ("added", "replaced", "exists")}
        setting_count = sum(1 for e in built if e.is_setting and outcomes[e.name] != "exists")
        _say(f"Imported into {store.location}: {counts['added']} added, {counts['replaced']} replaced, "
             f"{counts['exists']} already there and left as they are ({setting_count} of the changed ones are settings), "
             f"{len(skipped)} skipped, "
             f"{len(failed)} not importable. Agents may use them: {'yes' if agents else 'no'}. Uses: {', '.join(use_list)}.")
        for key, reason in failed.items():
            _say(f"  NOT imported: {key}: {reason}", err=True)
        raise typer.Exit(EXIT_FAILED if failed else 0)
    except typer.Exit:
        raise
    except Exception as exc:
        _log_failure("import", exc)
        _say(f"import failed: {_describe(exc)}", err=True)
        raise typer.Exit(EXIT_FAILED)


def _browser_port(target: str) -> int:
    """The debug port of a Director-owned browser on THIS machine, resolved through the Director.

    Only a registered profile can be named - never a raw port or address - so login cannot be pointed at
    something an agent started itself.
    """
    from cc_shared import gateway

    director_id = (os.environ.get("CC_DIRECTOR_ID") or "").strip()
    if not director_id:
        raise CcSecretsError("CC_DIRECTOR_ID is not set. login drives a Director-owned browser and only works inside a DevThrottle session.")
    try:
        payload = gateway.get_json(f"directors/{director_id}/browsers") or {}
    except gateway.GatewayError as exc:
        raise CcSecretsError(f"The Director could not be asked for its browsers: {exc}") from exc
    browsers = payload.get("browsers", payload.get("Browsers", [])) or []
    key = target.strip().lower()
    match = next((b for b in browsers if gateway.field(b, "id", "Id").lower() == key), None) \
        or next((b for b in browsers if gateway.field(b, "name", "Name").lower() == key), None)
    if match is None:
        names = ", ".join(gateway.field(b, "id", "Id") for b in browsers) or "none"
        raise CcSecretsError(f"No Director-owned browser named '{target}' on this machine (browsers: {names}).")
    address = urlsplit(gateway.field(match, "buCdpUrl", "BuCdpUrl"))
    if address.hostname not in ("127.0.0.1", "localhost") or not address.port:
        raise CcSecretsError(f"Browser '{target}' does not have a loopback debug address.")
    return address.port


@app.command()
def login(
    name: str = typer.Argument(..., help="Entry name."),
    browser: str = typer.Option(..., "--browser", help="The Director-owned browser profile (see cc-devthrottle browser list)."),
    timeout: float = typer.Option(30, "--timeout", help="Seconds to wait for the login to complete."),
    json_output: bool = typer.Option(False, "--json", help="Print JSON."),
):
    """Fill and submit the login form on the open tab of the entry's site. Refuses any other address.

    Open the login page in that browser first (cc-devthrottle browser start, then browser-harness).
    """
    label = f"login --browser {browser}"
    try:
        entry = _store().entry_for_agent(name, "login")
    except EntryNotAvailableError as exc:
        _refused(name, label, exc, json_output)
    except Exception as exc:
        _fail("login", name, label, exc)
    try:
        result = browser_login(entry, _browser_port(browser), timeout)
    except Exception as exc:
        _fail("login", name, label, exc)
    outcome = {"logged in": "ok", "refused": "refused"}.get(result.outcome, "failed")
    _audit().record(name, label, outcome, f"{result.outcome}: {result.reason} origin={result.host}")
    if json_output:
        _say_json({"entry": name, "outcome": result.outcome, "reason": result.reason, "host": result.host})
    elif result.outcome == OUTCOME_LOGGED_IN:
        _say(f"logged in ({result.host})")
    else:
        _say(f"{result.outcome}: {result.reason}", err=True)
    if result.outcome != OUTCOME_LOGGED_IN:
        raise typer.Exit(EXIT_REFUSED if result.outcome == OUTCOME_REFUSED else EXIT_FAILED)


@app.command("log")
def show_log(
    count: int = typer.Option(50, "--count", "-n", help="How many of the most recent lines."),
    json_output: bool = typer.Option(False, "--json", help="Print JSON."),
):
    """Show the audit log: time, entry, session, command, outcome. Never a secret."""
    try:
        lines = _audit().read(count)
        if json_output:
            _say_json({"lines": lines})
            return
        if not lines:
            _say("The audit log is empty.")
            return
        table = Table(show_lines=False)
        for column in ("Time", "Entry", "Session", "Command", "Outcome", "Detail"):
            table.add_column(column)
        for l in lines:
            table.add_row(*[SCRUBBER.scrub(str(l.get(k, ""))) for k in ("time", "entry", "session", "command", "outcome", "detail")])
        console.print(table)
    except Exception as exc:
        _log_failure("log", exc)
        _say(f"log failed: {_describe(exc)}", err=True)
        raise typer.Exit(EXIT_FAILED)


@app.command()
def version():
    """Print the version."""
    _say(__version__)


def main() -> None:
    """The console-script entry point. Whatever escapes a command is shown without a traceback."""
    try:
        app()
    except SystemExit:
        raise
    except KeyboardInterrupt:
        _say("cc-secrets: interrupted.", err=True)
        raise SystemExit(130)
    except BaseException as exc:
        try:
            _log_failure("main", exc)
        except BaseException:
            pass  # the log itself may be what failed (for example, the folder's permissions); still say why below
        _say(f"cc-secrets stopped: {_describe(exc)}", err=True)
        raise SystemExit(EXIT_FAILED)


if __name__ == "__main__":
    main()
