"""Every cc-* tool reports its own failures to the Gateway (issue #3642, Error Logging mission #3675).

Before this, a tool that failed on somebody's machine printed to that machine's terminal and nowhere
else. Most of these tools are run by agents, and agents are users too: the owner never saw any of it.

THE ONE HOOK. Each tool's entry point - the console script the installer writes and the source
`main.py` - runs the tool through `run_tool`. That is the only place a report is made. It sees how
the process ends, so no failure site has to remember to report:

  - an exception nobody caught                    -> one report, kind "unhandled", then re-raised
  - an exit with a non-zero code                  -> one report, kind "exit", then the same exit
  - an entry function that RETURNS a non-zero int -> one report, kind "exit", then that exit

The process ends exactly as it would have without the hook: the same exception object is re-raised,
the same exit code leaves, and nothing is printed. Reporting never changes what the user reads.

A USER MISTAKE IS NOT A DEFECT, and is not reported. Three things say so:
  - exit code 2. It is the usage-error code of Click (which every Typer tool runs on) and of argparse
    (which the others run on), and the tools that exit 2 themselves use it for the same thing: a value
    that is not one of the valid ones.
  - a Click `UsageError` anywhere in the exception chain (an unknown option, a missing argument).
  - the user stopping the tool: Ctrl+C (`KeyboardInterrupt`) or a Click `Abort` at a confirmation.

WHAT A REPORT CARRIES, AND WHAT IT NEVER CARRIES. The tool's name and the command that ran (resolved
against the tool's own command tree, so only real command names are ever taken from the command
line), the exception type, and the failure's message - scrubbed. NEVER the tool's arguments, flag
values, file contents or a prompt's words: every value on the command line is cut out of the message
before it is scrubbed (see `redact_arguments`), so a message that quotes a path or a prompt back
reaches the Gateway as `<argument>`. The scrubbing is `scrub_text`, a port of the Gateway's own
`ErrorTextScrubber`, and the Gateway scrubs again on receipt. An unhandled exception carries its
frames - file, line and function, never the source lines or any value.

THE MESSAGE. An explicit failure exit usually follows `except SomeError as e: print(...); raise
typer.Exit(1)`, so the exception that caused it is still on the exit's context chain: its type and
message become the report. A shared fail helper names its message outright with `note_failure`
before it exits. With neither, the report says the command failed and with which exit code.

THE CREDENTIAL (owner ruling, 9 October 2026), first that applies:
  1. inside a session: the session's own key (CC_GATEWAY_SESSION_KEY) to CC_GATEWAY_URL;
  2. outside a session: this machine's own Gateway credential (`gateway.url` and `gateway.token` in
     config.json - the one the Director and launcher use);
  3. before the machine has signed in: the hosted Gateway's public `POST /install-reports`, the route
     the Director's `PreSignInOutbox` uses, at most three reports an hour from the tools.

A REPORT THAT CANNOT BE SENT IS KEPT AND COUNTED, never dropped. Every report is written to the outbox
on disk FIRST (`<machine root>/logs/error-outbox/tool/`), one file each, and leaves it only when the
Gateway accepted it. Whatever is waiting goes with the next failure's report. When the outbox is full,
a marker is kept per report that did not fit, and the next report sent says how many. What happened
to each report is written to `<machine root>/logs/tool-error-reports.log`.
"""

from __future__ import annotations

import hashlib
import json
import os
import platform
import re
import socket
import sys
import time
import traceback
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Dict, Iterable, List, Optional, Sequence, Tuple

COMPONENT = "tool"
DIRECTOR_ERRORS_PATH = "/gateway/director-errors"
INSTALL_REPORTS_PATH = "/install-reports"
STEP_BEFORE_SIGN_IN = "errors-before-sign-in"
HOSTED_GATEWAY_URL = "https://gateway.devthrottle.com"
HOSTED_GATEWAY_ENV = "DEVTHROTTLE_HOSTED_GATEWAY_URL"

# The Gateway's limits (ErrorReportLimits / InstallReportLimits); the Gateway's constants are the authority.
MAX_REPORTS_PER_BATCH = 25
MAX_SHORT_FIELD = 120
MAX_ACTION = 200
MAX_MESSAGE = 2000
MAX_STACK = 8000
INSTALL_MAX_MESSAGE = 4000
INSTALL_DIAGNOSTICS_BUDGET = 16000 - 1000

MAX_KEPT = 200
MAX_BEFORE_SIGN_IN_PER_HOUR = 3
SEND_TIMEOUT_SECONDS = 5.0
PAUSE_AFTER_RATE_LIMIT_SECONDS = 15 * 60
PAUSE_AFTER_MISSING_ROUTE_SECONDS = 60 * 60

USAGE_EXIT_CODE = 2
ARGUMENT = "<argument>"
REDACTED = "<redacted>"
#: A command-line value shorter than this is not cut out of a message: a one- or two-character value
#: ("1", "on") cannot carry a path, a prompt or a secret, and cutting it would shred every number in
#: the message ("HTTP 401" losing its "1"s).
MIN_ARGUMENT_CHARS = 3


# --- Scrubbing: a port of CcDirector.Core.ErrorReports.ErrorTextScrubber -------------------------

_MAC_HOME = re.compile(r"/Users/[^/\s\"']+")
_LINUX_HOME = re.compile(r"/home/[^/\s\"']+")
_WINDOWS_HOME = re.compile(r"[A-Za-z]:\\{1,2}Users\\{1,2}[^\\\s\"']+", re.IGNORECASE)
_UNC_HOME = re.compile(r"\\{2,4}[^\\\s\"']+\\{1,2}(?:[A-Za-z]\$\\{1,2})?Users\\{1,2}[^\\\s\"']+", re.IGNORECASE)
_IDENTIFIER = re.compile(r"^[A-Z][a-z]+(?:[A-Z][a-z]+)*(?:_[A-Z][a-z]+(?:[A-Z][a-z]+)*)*$")
_BEARER = re.compile(r"(?i)\bbearer\s+[^\s\"']+")
_NAMED_SECRET = re.compile(r"(?i)\b(token|key|apikey|api_key|secret|password|pwd|authorization)(\s*[=:]\s*)[^\s\"'&,;]+")
_KEY_RUN = re.compile(r"(?<![A-Za-z0-9_\-])[A-Za-z0-9_\-]{32,}(?![A-Za-z0-9_\-])")
_GUID = re.compile(r"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$")


def _looks_like_a_key(run: str) -> bool:
    """A long run is a key when it has an upper-case letter AND a lower-case letter or a digit, and is
    neither a GUID nor a camel-case identifier. The same rule as the C# scrubber."""
    if _GUID.match(run) or _IDENTIFIER.match(run):
        return False
    upper = any("A" <= c <= "Z" for c in run)
    lower = any("a" <= c <= "z" for c in run)
    digit = any("0" <= c <= "9" for c in run)
    return upper and (lower or digit)


def _in_stack_frame(text: str, index: int) -> bool:
    """Whether the position is on a stack frame line ("   at ..."): a frame is our own code's names."""
    line_start = 0 if index == 0 else text.rfind("\n", 0, index) + 1
    i = line_start
    while i < len(text) and text[i] in " \t":
        i += 1
    return i > line_start and text.startswith("at ", i)


def scrub_text(value: Optional[str]) -> str:
    """Home folders to "~" and credential-shaped values to <redacted>. No length cap."""
    if not value:
        return ""
    s = _MAC_HOME.sub("~", value)
    s = _LINUX_HOME.sub("~", s)
    s = _UNC_HOME.sub("~", s)
    s = _WINDOWS_HOME.sub("~", s)
    s = _BEARER.sub("Bearer " + REDACTED, s)
    s = _NAMED_SECRET.sub(lambda m: m.group(1) + m.group(2) + REDACTED, s)
    text = s
    return _KEY_RUN.sub(
        lambda m: REDACTED if _looks_like_a_key(m.group(0)) and not _in_stack_frame(text, m.start()) else m.group(0),
        text,
    )


def clean_text(value: Optional[str], limit: int) -> str:
    """Scrub, drop control characters other than newline and tab, cap at `limit`, and trim."""
    if not value:
        return ""
    out: List[str] = []
    for c in scrub_text(value):
        if len(out) >= limit:
            break
        if unicodedata.category(c) == "Cc" and c not in "\n\t":
            continue
        out.append(c)
    return "".join(out).strip()


# --- The command line: which words are commands, which are values -------------------------------


def _value_forms(value: str) -> List[str]:
    """Every spelling of one command-line value a message is likely to quote back."""
    forms = {value, value.strip(), value.replace("\\", "\\\\"), value.replace("\\", "/")}
    if "/" in value or "\\" in value or "." in value:
        path = Path(value)
        forms.update({path.name, path.stem})
        absolute = os.path.abspath(value)
        forms.update({absolute, absolute.replace("\\", "\\\\"), absolute.replace("\\", "/")})
    return [f for f in forms if len(f) >= MIN_ARGUMENT_CHARS]


def argument_values(argv: Sequence[str], command_path: Sequence[str]) -> List[str]:
    """The VALUES on a command line: everything but the command names and the option names.

    `--theme dark` gives "dark"; `--theme=dark` gives "dark"; `--json` gives nothing; a command name
    that was resolved as part of the command gives nothing. Everything else is somebody's data.
    """
    commands = list(command_path)
    values: List[str] = []
    for token in argv:
        if token.startswith("-"):
            if "=" in token:
                values.append(token.split("=", 1)[1])
            continue
        if commands and token == commands[0]:
            commands.pop(0)
            continue
        values.append(token)
    return values


def redact_arguments(text: Optional[str], values: Iterable[str]) -> str:
    """Cut every command-line value, in every spelling `_value_forms` knows, out of `text`."""
    if not text:
        return ""
    forms = set()
    for value in values:
        forms.update(_value_forms(value))
    for form in sorted(forms, key=len, reverse=True):
        text = re.sub(re.escape(form), ARGUMENT, text, flags=re.IGNORECASE)
    return text


def _click_command(app: Any) -> Any:
    """The Click command behind a Typer app, or the Click command itself."""
    if hasattr(app, "registered_commands"):
        import typer.main  # a Typer app: only a Typer tool passes one

        return typer.main.get_command(app)
    return app


def argparse_command_names(parser: Any) -> List[str]:
    """The subcommand names of an argparse parser, read from the parser itself rather than kept by hand."""
    import argparse

    names: List[str] = []
    for action in parser._actions:
        if isinstance(action, argparse._SubParsersAction):
            names.extend(action.choices.keys())
    return names


def command_path(argv: Sequence[str], app: Any = None, command_names: Optional[Iterable[str]] = None) -> List[str]:
    """The commands named on the command line, resolved against the tool's own command tree.

    Only a word that IS a command name in the tree is taken, so a value can never become part of the
    surface. `app` is a Typer app or a Click command; `command_names` is the one level of subcommands
    of an argparse tool.
    """
    words = [t for t in argv if not t.startswith("-")]
    if app is not None:
        path: List[str] = []
        current = _click_command(app)
        for word in words:
            commands = getattr(current, "commands", None)
            if not isinstance(commands, dict):
                break
            if word in commands:
                path.append(word)
                current = commands[word]
        return path
    if command_names is not None:
        names = set(command_names)
        for word in words:
            if word in names:
                return [word]
        return []
    return []


# --- What failed -------------------------------------------------------------------------------


@dataclass
class _Noted:
    message: str
    exception: Optional[BaseException]


_noted: Optional[_Noted] = None


def note_failure(message: str, exception: Optional[BaseException] = None) -> None:
    """Name the failure the process is about to exit for. A shared fail helper calls this just before it
    exits, so the report carries the helper's own sentence rather than "exited with code 1". The
    message is scrubbed and stripped of argument values like any other; it is never printed by this."""
    global _noted
    _noted = _Noted(message=message, exception=exception)


def _chain(exc: Optional[BaseException]) -> List[BaseException]:
    """The exception, then what caused it, then what that was raised while handling - in that order."""
    seen: List[BaseException] = []
    current = exc
    while current is not None and all(current is not s for s in seen):
        seen.append(current)
        current = current.__cause__ if current.__cause__ is not None else (
            None if current.__suppress_context__ else current.__context__)
    return seen


def _type_names(exc: BaseException) -> List[str]:
    return [t.__name__ for t in type(exc).__mro__]


def is_user_mistake(exc: Optional[BaseException], code: Optional[int]) -> bool:
    """True for a usage error, a refusal the user chose, or Ctrl+C - see the module text."""
    if code == USAGE_EXIT_CODE:
        return True
    for link in _chain(exc):
        names = _type_names(link)
        if "UsageError" in names or "Abort" in names or isinstance(link, KeyboardInterrupt):
            return True
    return False


def _cause_of(exc: Optional[BaseException]) -> Optional[BaseException]:
    """The exception behind an exit: the first link in the chain that is not itself an exit."""
    for link in _chain(exc):
        if isinstance(link, SystemExit) or type(link).__name__ == "Exit":
            continue
        return link
    return None


def exit_code_of(exc: SystemExit) -> int:
    """The process exit code a SystemExit leaves with: None is 0, an int is itself, anything else is 1."""
    if exc.code is None:
        return 0
    if isinstance(exc.code, int) and not isinstance(exc.code, bool):
        return exc.code
    return 1


def _http_details(exc: Optional[BaseException]) -> Tuple[Optional[int], Optional[str], Optional[str]]:
    """(http_status, error_code, correlation_id) from a Gateway failure anywhere in the chain."""
    for link in _chain(exc):
        status = getattr(link, "status", None)
        if not isinstance(status, int) or isinstance(status, bool) or not 100 <= status <= 599:
            continue
        body = getattr(link, "body", None)
        code = None
        if isinstance(body, dict):
            for key in ("code", "error_code", "errorCode"):
                if isinstance(body.get(key), str) and body[key].strip():
                    code = body[key].strip()
                    break
        correlation = getattr(link, "correlation_id", None)
        return status, code, correlation if isinstance(correlation, str) and correlation else None
    return None, None, None


def _frames(exc: BaseException) -> str:
    """The frames of an exception: file, line and function. Never the source line, never a value."""
    lines = []
    for frame in traceback.extract_tb(exc.__traceback__):
        path = Path(frame.filename)
        where = f"{path.parent.name}/{path.name}" if path.parent.name else path.name
        lines.append(f"   at {where}:{frame.lineno} in {frame.name}")
    return "\n".join(lines)


def _utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# --- The machine -------------------------------------------------------------------------------


def machine_name() -> str:
    """The name the Director's Environment.MachineName gives: COMPUTERNAME on Windows, the host name
    up to its first dot elsewhere."""
    if sys.platform == "win32":
        name = os.environ.get("COMPUTERNAME", "").strip()
        if name:
            return name
    return socket.gethostname().split(".")[0]


def machine_id(name: Optional[str] = None) -> str:
    """ErrorReportMachineId.Of: 16 lower-case hex characters of SHA-256 over the upper-cased name."""
    raw = (name if name is not None else machine_name()).strip().upper()
    return hashlib.sha256(raw.encode("utf-8")).hexdigest()[:16]


def _os_name() -> str:
    if sys.platform == "win32":
        return "windows"
    if sys.platform == "darwin":
        return "macos"
    if sys.platform.startswith("linux"):
        return "linux"
    return "other"


def _arch() -> str:
    machine = platform.machine().lower()
    return {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64", "i386": "x86", "i686": "x86", "x86": "x86"}.get(machine, machine)


def _product_version(tool: str) -> str:
    """The installed tool's version; "source" when it runs from a checkout rather than an install."""
    from importlib import metadata

    try:
        return metadata.version(tool)
    except metadata.PackageNotFoundError:
        return "source"


def machine_root() -> Path:
    """CcStorage.MachineRoot: the storage root, climbed out of a Director's `instances/<name>` folder."""
    from cc_storage import CcStorage

    root = CcStorage.config().parent
    while root.parent.name.lower() == "instances" and root.parent.parent != root.parent:
        root = root.parent.parent
    return root


def _log(line: str) -> None:
    """One line in the tools' error-report log. If even that cannot be written, say so on standard
    error: the reporting path's own failure must be loud somewhere."""
    try:
        path = machine_root() / "logs" / "tool-error-reports.log"
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "a", encoding="utf-8") as f:
            f.write(f"{_utc_now()} [tool_errors] {line}\n")
    except OSError as exc:
        sys.stderr.write(f"[tool_errors] could not write the error-report log ({type(exc).__name__}): {exc}\n")


# --- Building one report -----------------------------------------------------------------------


def build_report(
    tool: str,
    argv: Sequence[str],
    exc: Optional[BaseException],
    code: Optional[int],
    *,
    app: Any = None,
    command_names: Optional[Iterable[str]] = None,
    report_message: bool = True,
    noted: Optional[_Noted] = None,
) -> Optional[Dict[str, Any]]:
    """The one report for how the process ended, or None for a user mistake. `code` is None for an
    unhandled exception, otherwise the exit code."""
    if is_user_mistake(exc, code):
        return None

    path = command_path(argv, app=app, command_names=command_names)
    values = argument_values(argv, path)
    surface = " ".join([tool, *path])
    action = path[-1] if path else "run"

    if code is None:
        kind = "unhandled"
        cause = exc
    else:
        kind = "exit"
        cause = (noted.exception if noted and noted.exception is not None else None) or _cause_of(exc)

    exception_type = type(cause).__name__ if cause is not None else "Exit"
    if not report_message:
        message = f"{surface} failed with {exception_type}" + (f" (exit code {code})" if code is not None else "")
    elif noted is not None and noted.message.strip():
        message = noted.message
    elif cause is not None and str(cause).strip():
        message = str(cause)
    else:
        message = f"{surface} failed (exit code {code})"

    stack = _frames(cause) if kind == "unhandled" and cause is not None else ""
    status, error_code, correlation = _http_details(cause if cause is not None else exc)
    now = _utc_now()
    report: Dict[str, Any] = {
        "component": COMPONENT,
        "source": clean_text(tool, MAX_SHORT_FIELD),
        "kind": kind,
        "message": clean_text(redact_arguments(message, values), MAX_MESSAGE),
        "exception_type": clean_text(exception_type, MAX_SHORT_FIELD),
        "stack": clean_text(redact_arguments(stack, values), MAX_STACK),
        "repeat_count": 1,
        "first_seen_utc": now,
        "last_seen_utc": now,
        "product_version": clean_text(_product_version(tool), MAX_SHORT_FIELD),
        "os": _os_name(),
        "os_version": clean_text(f"{platform.system()} {platform.version()}", MAX_SHORT_FIELD),
        "arch": _arch(),
        "machine_id": machine_id(),
        "user_visible": True,
        "surface": clean_text(surface, MAX_SHORT_FIELD),
        "action": clean_text(action, MAX_ACTION),
        "correlation_id": clean_text(correlation, MAX_SHORT_FIELD) or None,
        "http_status": status,
        "error_code": clean_text(redact_arguments(error_code, values), MAX_SHORT_FIELD) or None,
        "session_id": clean_text(os.environ.get("CC_SESSION_ID", ""), MAX_SHORT_FIELD) or None,
    }
    if not report["message"]:
        report["message"] = clean_text(f"{surface} failed", MAX_MESSAGE)
    return report


# --- The credential ----------------------------------------------------------------------------


@dataclass
class Credential:
    """Where a report goes. `kind` is "session", "machine" or "before-sign-in"; `bearer` is None for the
    last, whose route takes no credential."""

    kind: str
    url: str
    bearer: Optional[str]


def _is_local_gateway_host(url: str) -> bool:
    """GatewayConfig.IsLocalGatewayHost: loopback, localhost, this machine's name, or its Tailscale name."""
    host = (urllib.parse.urlparse(url).hostname or "").lower()
    if not host:
        return False
    if host in ("localhost", "::1") or host.startswith("127."):
        return True
    name = machine_name().lower()
    return host == name or host.split(".", 1)[0] == name.replace("_", "-")


def _hosted_url() -> str:
    raw = os.environ.get(HOSTED_GATEWAY_ENV, "").strip()
    if not raw:
        return HOSTED_GATEWAY_URL
    parsed = urllib.parse.urlparse(raw)
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        raise ValueError(
            f"{HOSTED_GATEWAY_ENV} is set to '{raw}', which is not an absolute http:// or https:// address. "
            f"Set it to a full hosted Gateway address, or clear it to use {HOSTED_GATEWAY_URL}."
        )
    return raw.rstrip("/")


def resolve_credential() -> Credential:
    """The credential a report goes with, in the owner's order: session key, machine, before sign-in.

    Raises ValueError when the machine's config.json or the hosted-Gateway override cannot be read -
    the report is then kept, and the reason logged."""
    url = os.environ.get("CC_GATEWAY_URL", "").strip()
    key = os.environ.get("CC_GATEWAY_SESSION_KEY", "").strip()
    if url and key:
        return Credential("session", url.rstrip("/"), key)
    if url or key:
        _log("a session holds only one of CC_GATEWAY_URL and CC_GATEWAY_SESSION_KEY; using the machine's credential")

    config = machine_root() / "config" / "config.json"
    if config.exists():
        try:
            data = json.loads(config.read_text(encoding="utf-8-sig"))
        except ValueError as exc:
            raise ValueError(f"{config} is not readable JSON ({exc})") from exc
        block = data.get("gateway") if isinstance(data, dict) else None
        if isinstance(block, dict):
            gw_url = str(block.get("url") or "").strip()
            token = str(block.get("token") or "").strip()
            if gw_url and not token and _is_local_gateway_host(gw_url):
                local = machine_root() / "config" / "director" / "gateway-token.txt"
                if local.exists():
                    token = local.read_text(encoding="utf-8").strip()
            if gw_url and token:
                return Credential("machine", gw_url.rstrip("/"), token)
    return Credential("before-sign-in", _hosted_url(), None)


# --- The outbox --------------------------------------------------------------------------------


class Outbox:
    """Reports waiting to be accepted: one file each, so two tool processes never write the same file.

    `keep` writes a report and never loses one: when the outbox is full it leaves a `not-kept-*` marker
    instead, and the next batch sent carries their count. `remove` takes out only what the Gateway
    accepted. A file that cannot be read is renamed aside, never deleted.
    """

    def __init__(self, folder: Path, clock: Callable[[], float] = time.time):
        self.folder = folder
        self._clock = clock

    def _items(self) -> List[Path]:
        if not self.folder.exists():
            return []
        return sorted(p for p in self.folder.glob("report-*.json"))

    def not_kept_markers(self) -> List[Path]:
        if not self.folder.exists():
            return []
        return sorted(self.folder.glob("not-kept-*"))

    @property
    def count(self) -> int:
        """Reports waiting on disk."""
        return len(self._items())

    def keep(self, report: Dict[str, Any]) -> Optional[Path]:
        """Write one report. Returns its file, or None when the outbox was full and it was counted instead.
        Raises OSError when nothing could be written."""
        self.folder.mkdir(parents=True, exist_ok=True)
        if len(self._items()) >= MAX_KEPT:
            (self.folder / f"not-kept-{uuid.uuid4().hex}").write_text("", encoding="utf-8")
            return None
        stamp = f"{int(self._clock() * 1000):015d}"
        final = self.folder / f"report-{stamp}-{uuid.uuid4().hex}.json"
        temp = self.folder / f".tmp-{uuid.uuid4().hex}"
        temp.write_text(json.dumps(report), encoding="utf-8")
        os.replace(temp, final)
        return final

    def oldest(self, limit: int) -> List[Tuple[Path, Dict[str, Any]]]:
        """Up to `limit` waiting reports, oldest first. An unreadable file is moved aside and logged."""
        out: List[Tuple[Path, Dict[str, Any]]] = []
        for path in self._items():
            if len(out) >= limit:
                break
            try:
                report = json.loads(path.read_text(encoding="utf-8"))
            except ValueError as exc:
                aside = path.with_suffix(".unreadable")
                os.replace(path, aside)
                _log(f"outbox file {path.name} was not readable JSON ({exc}); moved to {aside.name}")
                continue
            except FileNotFoundError:
                continue  # another tool process sent it a moment ago
            if isinstance(report, dict):
                out.append((path, report))
        return out

    def remove(self, paths: Iterable[Path]) -> None:
        for path in paths:
            try:
                path.unlink()
            except FileNotFoundError:
                pass  # another tool process sent and removed it first

    def _state_path(self) -> Path:
        return self.folder / "state.json"

    def _state(self) -> Dict[str, Any]:
        path = self._state_path()
        if not path.exists():
            return {}
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
        except ValueError:
            _log(f"{path} was not readable JSON; starting it again")
            return {}
        return value if isinstance(value, dict) else {}

    def _save_state(self, state: Dict[str, Any]) -> None:
        self.folder.mkdir(parents=True, exist_ok=True)
        temp = self.folder / f".tmp-state-{uuid.uuid4().hex}"
        temp.write_text(json.dumps(state), encoding="utf-8")
        os.replace(temp, self._state_path())

    def paused_until(self, kind: str) -> float:
        value = self._state().get(f"paused_until_{kind}", 0)
        return float(value) if isinstance(value, (int, float)) else 0.0

    def pause(self, kind: str, seconds: float) -> None:
        state = self._state()
        state[f"paused_until_{kind}"] = self._clock() + seconds
        self._save_state(state)

    def before_sign_in_sends_last_hour(self) -> int:
        now = self._clock()
        sent = [t for t in self._state().get("before_sign_in_sent", []) if isinstance(t, (int, float)) and now - t < 3600]
        return len(sent)

    def record_before_sign_in_send(self) -> None:
        state = self._state()
        now = self._clock()
        sent = [t for t in state.get("before_sign_in_sent", []) if isinstance(t, (int, float)) and now - t < 3600]
        sent.append(now)
        state["before_sign_in_sent"] = sent
        self._save_state(state)


def default_outbox() -> Outbox:
    return Outbox(machine_root() / "logs" / "error-outbox" / COMPONENT)


# --- Sending -----------------------------------------------------------------------------------


@dataclass
class SendOutcome:
    """What one post came back with: an HTTP status, or why no answer arrived."""

    status: Optional[int]
    failure: Optional[str] = None

    @property
    def accepted(self) -> bool:
        return self.status is not None and 200 <= self.status < 300

    def __str__(self) -> str:
        return f"HTTP {self.status}" if self.status is not None else f"not delivered ({self.failure})"


def _post(url: str, body: Dict[str, Any], bearer: Optional[str], timeout: float) -> SendOutcome:
    """POST JSON. Never carries the credential to another origin (the shared opener refuses that)."""
    from cc_shared import gateway

    data = json.dumps(body).encode("utf-8")
    try:
        req = urllib.request.Request(url, data=data, method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json")
        if bearer:
            req.add_header("Authorization", f"Bearer {bearer}")
        with gateway._OPENER.open(req, timeout=timeout) as resp:
            return SendOutcome(resp.status)
    except urllib.error.HTTPError as err:
        return SendOutcome(err.code)
    except (urllib.error.URLError, OSError, ValueError, gateway.GatewayError) as err:
        return SendOutcome(None, f"{type(err).__name__}: {err}")


def _compose_before_sign_in(reports: List[Dict[str, Any]], not_kept: int, install_id: str) -> Tuple[Dict[str, Any], int]:
    """PreSignInOutbox.Compose: one install report carrying as many whole errors as fit."""
    blocks: List[str] = []
    length = 0
    for r in reports:
        block = (
            f"--- [{r.get('kind')}] {r.get('surface')} x{max(1, int(r.get('repeat_count') or 1))}, "
            f"at {r.get('last_seen_utc')}, version {r.get('product_version')}\n"
            f"{r.get('message')}\n{r.get('exception_type') or ''}\n{(r.get('stack') or '')[:1500]}\n\n"
        )
        if blocks and length + len(block) > INSTALL_DIAGNOSTICS_BUDGET:
            break
        blocks.append(block)
        length += len(block)
    used = len(blocks)
    first = reports[0]
    message = f"{used} error(s) from cc-* tools, reported before this machine signed in."
    if used < len(reports):
        message += f" {len(reports) - used} more are waiting for the next report."
    if not_kept:
        message += f" {not_kept} further error(s) were not kept because the outbox on disk was full."
    message += f" First: [{first.get('surface')}] {first.get('message')}"
    return {
        "install_id": install_id,
        "installer": COMPONENT,
        "component": COMPONENT,
        "step": STEP_BEFORE_SIGN_IN,
        "message": message[:INSTALL_MAX_MESSAGE],
        "diagnostics": "".join(blocks)[:INSTALL_DIAGNOSTICS_BUDGET],
        "os": first.get("os") or "",
        "os_version": first.get("os_version") or "",
        "arch": first.get("arch") or "",
        "product_version": first.get("product_version") or "",
    }, used


def install_id() -> str:
    """InstallId.ReadOrCreate: the machine's install id, created on first use. Shared with the
    installer, the launcher and every Director, so the owner can put one machine's story together."""
    path = machine_root() / "install-id"
    if path.exists():
        existing = path.read_text(encoding="utf-8").strip()
        try:
            uuid.UUID(existing)
            return existing
        except ValueError:
            pass  # unreadable: replaced below, exactly as the Director does
    value = str(uuid.uuid4())
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(value, encoding="utf-8")
    return value


def _not_kept_report(count: int, template: Dict[str, Any]) -> Dict[str, Any]:
    report = dict(template)
    report.update({
        "kind": "outbox-full",
        "message": f"{count} cc-* tool error report(s) were not kept because the outbox on disk was full.",
        "exception_type": None,
        "stack": None,
        "surface": "tool error outbox",
        "action": "keep an error report",
        "correlation_id": None,
        "http_status": None,
        "error_code": None,
        "repeat_count": count,
    })
    return report


def flush(outbox: Outbox, credential: Credential, timeout: float = SEND_TIMEOUT_SECONDS) -> int:
    """Send what is waiting with `credential`. Returns how many reports the Gateway accepted; whatever it
    did not accept stays in the outbox, and the reason is logged."""
    now = time.time()
    if now < outbox.paused_until(credential.kind):
        _log(f"{outbox.count} report(s) kept: sending with the {credential.kind} credential is paused")
        return 0
    waiting = outbox.oldest(MAX_REPORTS_PER_BATCH)
    markers = outbox.not_kept_markers()
    if not waiting and not markers:
        return 0
    reports = [r for _, r in waiting]

    if credential.kind == "before-sign-in":
        if not reports:
            return 0
        if outbox.before_sign_in_sends_last_hour() >= MAX_BEFORE_SIGN_IN_PER_HOUR:
            _log(f"{outbox.count} report(s) kept: {MAX_BEFORE_SIGN_IN_PER_HOUR} reports before sign-in already sent this hour")
            return 0
        payload, used = _compose_before_sign_in(reports, len(markers), install_id())
        outbox.record_before_sign_in_send()
        outcome = _post(credential.url + INSTALL_REPORTS_PATH, payload, None, timeout)
        sent_paths = [p for p, _ in waiting[:used]]
    else:
        if markers:
            if len(reports) >= MAX_REPORTS_PER_BATCH:
                reports = reports[:-1]
                waiting = waiting[:-1]
            template = reports[0] if reports else {"component": COMPONENT, "source": "cc-tools", "machine_id": machine_id(),
                                                   "os": _os_name(), "arch": _arch(), "user_visible": False,
                                                   "first_seen_utc": _utc_now(), "last_seen_utc": _utc_now()}
            reports = reports + [_not_kept_report(len(markers), template)]
        outcome = _post(credential.url + DIRECTOR_ERRORS_PATH, {"reports": reports}, credential.bearer, timeout)
        sent_paths = [p for p, _ in waiting]
        used = len(reports)

    if outcome.accepted:
        outbox.remove(sent_paths)
        outbox.remove(markers)
        _log(f"{used} report(s) sent with the {credential.kind} credential ({outcome}); {outbox.count} still waiting")
        return used
    if outcome.status == 429:
        outbox.pause(credential.kind, PAUSE_AFTER_RATE_LIMIT_SECONDS)
    elif outcome.status == 404:
        outbox.pause(credential.kind, PAUSE_AFTER_MISSING_ROUTE_SECONDS)
    _log(f"{outbox.count} report(s) kept: the {credential.kind} send was {outcome}")
    return 0


def report(report_item: Dict[str, Any], outbox: Optional[Outbox] = None) -> None:
    """Keep one report, then send everything waiting. Never raises: a report that could not be kept or
    sent is logged, and a report that could be neither is also said on standard error."""
    box = outbox if outbox is not None else default_outbox()
    kept = True
    try:
        box.keep(report_item)
    except OSError as exc:
        kept = False
        _log(f"a report could not be written to {box.folder} ({type(exc).__name__}: {exc}); sending it directly")
    try:
        credential = resolve_credential()
    except (OSError, ValueError) as exc:
        _log(f"{box.count} report(s) kept: no credential could be read ({type(exc).__name__}: {exc})")
        if not kept:
            sys.stderr.write(f"[tool_errors] an error report could be neither kept nor sent: {exc}\n")
        return
    try:
        if kept:
            flush(box, credential)
            return
        if credential.kind == "before-sign-in":
            payload, _ = _compose_before_sign_in([report_item], 0, install_id())
            outcome = _post(credential.url + INSTALL_REPORTS_PATH, payload, None, SEND_TIMEOUT_SECONDS)
        else:
            outcome = _post(credential.url + DIRECTOR_ERRORS_PATH, {"reports": [report_item]}, credential.bearer,
                            SEND_TIMEOUT_SECONDS)
        _log(f"a report that could not be kept was sent directly: {outcome}")
        if not outcome.accepted:
            sys.stderr.write(f"[tool_errors] an error report could be neither kept nor sent ({outcome})\n")
    except OSError as exc:
        _log(f"{box.count} report(s) kept: the send failed ({type(exc).__name__}: {exc})")
        if not kept:
            sys.stderr.write(f"[tool_errors] an error report could be neither kept nor sent: {exc}\n")


# --- The entry point ---------------------------------------------------------------------------


def _report_ending(tool: str, argv: Sequence[str], exc: Optional[BaseException], code: Optional[int],
                   app: Any, command_names: Optional[Iterable[str]], report_message: bool) -> None:
    """Build and deliver the report for how the process ended. A bug in building it is logged and said
    on standard error - it must not change how the tool itself ends."""
    global _noted
    noted, _noted = _noted, None
    try:
        item = build_report(tool, argv, exc, code, app=app, command_names=command_names,
                            report_message=report_message, noted=noted)
    except Exception as build_error:  # the reporter's own defect: loud, and never the tool's ending
        _log(f"building the report for {tool} FAILED ({type(build_error).__name__}: {build_error})")
        sys.stderr.write(f"[tool_errors] the error report for {tool} could not be built: {type(build_error).__name__}\n")
        return
    if item is None:
        return
    report(item)


def run_tool(entry: Callable[[], Any], tool: str, *, app: Any = None,
             command_names: Optional[Iterable[str]] = None, report_message: bool = True) -> None:
    """Run a tool's entry point and report how it failed, if it did. The ONE hook every tool goes through.

    `entry` is the tool itself: a Typer app, or a function. When it returns an int that is the exit code.
    `app` (a Typer app or Click command) or `command_names` (an argparse tool's subcommands) is how the
    command that ran is named. `report_message=False` reports only the command and the exception type,
    for a tool whose messages may quote what it guards (cc-secrets).
    """
    argv = list(sys.argv[1:])
    try:
        result = entry()
    except SystemExit as exc:
        code = exit_code_of(exc)
        if code != 0:
            _report_ending(tool, argv, exc, code, app, command_names, report_message)
        raise
    except KeyboardInterrupt:
        raise
    except Exception as exc:
        _report_ending(tool, argv, exc, None, app, command_names, report_message)
        raise
    if isinstance(result, int) and not isinstance(result, bool):
        if result != 0:
            _report_ending(tool, argv, None, result, app, command_names, report_message)
        raise SystemExit(result)
