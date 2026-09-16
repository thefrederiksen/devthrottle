"""Fleet/session operations for cc-devthrottle."""

from __future__ import annotations

import json
import os
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import typer
from rich import box
from rich.console import Console
from rich.table import Table


# --- ASCII-only output (project house rule): Rich truncates an overflowing table cell with the
# Unicode ellipsis U+2026; emit ASCII "..." instead. Patched once at import. cc-devthrottle's cli
# imports this module eagerly, so the global patch is in place before any table renders. ---
def _install_ascii_truncation():
    import rich.text
    from rich.cells import set_cell_size
    _orig = rich.text.Text.truncate
    if getattr(_orig, "_ascii_ellipsis", False):
        return
    def _truncate(self, max_width, *, overflow=None, pad=False):
        _orig(self, max_width, overflow=overflow, pad=pad)
        if "\u2026" in self.plain:
            self.plain = set_cell_size(self.plain.replace("\u2026", ""), max(0, max_width - 3)) + "..."
            if pad and len(self.plain) < max_width:
                self.plain += " " * (max_width - len(self.plain))
    _truncate._ascii_ellipsis = True
    rich.text.Text.truncate = _truncate


_install_ascii_truncation()


# --- ASCII-only output: Typer renders its --help and error panels with Rich's default ROUNDED
# (Unicode) box; force them to the ASCII box so panels stay ASCII even when the console is UTF-8.
# Guarded so a non-Typer environment is a harmless no-op. ---
def _install_ascii_typer_panels():
    try:
        import typer.rich_utils as _tru
        from rich import box as _rbox
    except Exception:
        return
    if getattr(_tru.Panel, "_ascii_box", False):
        return
    _OrigPanel = _tru.Panel

    class _AsciiPanel(_OrigPanel):
        _ascii_box = True

        def __init__(self, *args, **kwargs):
            kwargs.setdefault("box", _rbox.ASCII)
            super().__init__(*args, **kwargs)

    _tru.Panel = _AsciiPanel


_install_ascii_typer_panels()

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402
from cc_shared import axi_output  # noqa: E402
from . import axi_cli  # noqa: E402
from . import usage_errors  # noqa: E402

from .repo_ops import is_windows_path, matches_repo  # noqa: E402

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass

# soft_wrap: a sentence is never broken across lines at the console width. An id or a session name
# split in two cannot be read back or pasted; tables still fit their columns.
console = Console(soft_wrap=True)
SELFTEST_MARKER = "FLEETPONG"


def _repo_name(repo: str) -> str:
    """The repository's folder name. A backslash separates folders only in a Windows-shaped path; on
    macOS and Linux it is an ordinary character, so /home/a\\b is the folder a\\b."""
    if not repo:
        return "-"
    separated = repo.replace("\\", "/") if is_windows_path(repo) else repo
    trimmed = separated.rstrip("/")
    if is_windows_path(repo) and len(trimmed) == 2 and trimmed.endswith(":"):
        # The root of a drive keeps its slash: C: alone is a different place (issue #2922).
        return repo[:3]
    return trimmed.split("/")[-1]


def _model_text(s: Dict[str, Any]) -> str:
    """The MODEL column (issue devthrottle_internal#1340): which model this session is actually running.

    It exists because an agent driving the fleet had to parse the JSON to learn the one fact that
    drives both cost and quality, while a human reading the same table could not learn it at all.

    RENDER, NEVER RULE. The Gateway folds this - the full recorded id when there is one, and the words
    for WHICH of the two absences applies when there is not ("no model yet" for a session that has not
    finished a turn, "model not reported" for an agent that can never report one). This prints the id
    in full rather than the fold's shortened badge text, because a table has the width a rail does not
    and a truncated id is not a name anything else will match.

    A row with no folded verdict at all is a Gateway too old to have stamped one. Then, and only then,
    the raw recorded model stands in - and when there is no model either, the cell reads "(unknown)":
    deliberately NOT one of the fold's two sentences, because this is a third case. We were told
    nothing, which is not the same as being told there is no model YET or that there never will be.
    An empty cell would have quietly claimed the second of those.
    """
    display = s.get("modelDisplay", s.get("ModelDisplay"))
    if isinstance(display, dict):
        model_id = (display.get("modelId") or display.get("ModelId") or "").strip()
        if model_id:
            return model_id
        text = (display.get("text") or display.get("Text") or "").strip()
        if text:
            return text
    raw = (gateway.field(s, "currentModel", "CurrentModel") or "").strip()
    return raw if raw else "(unknown)"


def _get_fleet() -> Tuple[List[Dict[str, Any]], Optional[bool], Optional[str], Optional[str]]:
    """The fleet roster and BOTH folded cautions, with this tool's error posture (issue #1051).

    The fetch itself is shared (gateway.get_fleet) so the four tools that resolve a target against
    this roster cannot drift; only the "print and exit" behaviour is local. The fourth value is the
    negative-answer caution and is printed ONLY where this tool's own answer came back empty.
    """
    try:
        return gateway.get_fleet()
    except gateway.GatewayError as err:
        axi_cli.fail(f"could not read the fleet list: {err}", [axi_cli.CHECK_GATEWAY])


def _roster_caveat(complete: Optional[bool], reason: Optional[str]) -> str:
    return gateway.roster_caveat(complete, reason)


def _resolve_target(target: str, *, command_name: str) -> Dict[str, Any]:
    sessions, complete, reason, stale_caution = _get_fleet()
    # Issue #821: the shared resolver now understands the three-digit session number (#820) as a
    # first-class target, preferring it over id-prefix / name matching, so message send / ask and
    # session rename all address a session by its number through this one call.
    matches = gateway.resolve_target(sessions, target)
    if not matches:
        # Issue #1051: this is where a dropped Director does its real damage. "No session matches"
        # reads as "that session does not exist", and for a session on a machine the Gateway could
        # not reach that is simply false - the roster we searched never contained it. Say which we
        # mean, because the two call for opposite next steps: give up, or go and look at machine B.
        details = []
        caveat = _roster_caveat(complete, reason)
        if caveat:
            details.append(f"The fleet list searched may be incomplete. {caveat}")
        # THE negative answer the second caution exists for. A machine whose tunnel is up but whose
        # pushes are late can be hiding the very session being addressed, and every target-resolving
        # verb comes through here - message send, message ask, rename, done, hold, compact. Printed on
        # this path only, so it stays rare enough to be read.
        if stale_caution:
            details.append(stale_caution)
        axi_cli.fail(
            " ".join([f"No session matches '{target}'. Pass a full session id, a session number, or an exact name.", *details]),
            ["cc-devthrottle session list"],
        )
    if len(matches) > 1:
        _refuse_ambiguous_target(target, matches, command_name=command_name)
    return matches[0]


def _refuse_ambiguous_target(
    target: str, matches: List[Dict[str, Any]], *, command_name: str
) -> None:
    """Print the several sessions a target matched, and exit - it is always an error, never a verdict.

    Lifted out of _resolve_target so that `session stop`, which deliberately does NOT go through that
    resolver (see _stop_target), still refuses an ambiguous target in exactly these words. Two
    wordings for one refusal is how the tool comes to answer the same question two ways.
    """
    # FULL ids, never the short form: the caller's next move is to paste one of these back, and a
    # shortened id can be ambiguous all over again.
    listed = []
    for s in matches:
        sid = gateway.field(s, "sessionId", "SessionId")
        name = gateway.field(s, "name", "Name") or "(unnamed)"
        machine = gateway.field(s, "machineName", "MachineName") or "-"
        listed.append(f"{sid} {name} ({machine})")
    axi_cli.fail(
        f"'{target}' is ambiguous - {len(matches)} sessions match: " + "; ".join(listed)
        + f". Re-run {command_name} with one of these full session ids.",
        ["cc-devthrottle session list --fields id,name,machine,state"],
    )


def resolve_session(target: str, *, command_name: str) -> Dict[str, Any]:
    """Resolve one session from the fleet roster, or print why it could not and exit.

    The public door onto the shared resolver above, for sibling command modules (mission attach and
    detach) that address a session exactly the way the session verbs do - by number, id prefix, or
    name. It exists so those commands cannot grow a second, subtly different way to name a session:
    one resolver means one answer to "which session did you mean", including the caveats about a
    roster that may be incomplete.
    """
    return _resolve_target(target, command_name=command_name)


def fleet_or_exit() -> Tuple[List[Dict[str, Any]], Optional[bool], Optional[str], Optional[str]]:
    """The fleet roster, or a printed error and exit - the public door onto the shared fetch."""
    return _get_fleet()


def resolve_target_or_current(target: Optional[str], command_name: str = "the command") -> str:
    """Return the requested session id, defaulting to this session."""
    if target is None or not target.strip():
        sid = gateway.session_id()
        if not sid:
            axi_cli.usage_error(
                "no target session was given, and CC_SESSION_ID is not set, so there is no current "
                "session to default to. "
                "Name the session: a full id, a session number, or an exact name from "
                "cc-devthrottle session list.",
            )
        return sid

    chosen = _resolve_target(target, command_name=command_name)
    return gateway.field(chosen, "sessionId", "SessionId")


# --- session list (AXI standard, docs/axi-standard.md; issue #2922) ---

# The five plain states, in the order the count line lists them.
SESSION_STATES = ("needs-you", "working", "ready", "snoozed", "crashed")

# Every field `session list --fields` accepts, and the four shown when it is not given.
SESSION_LIST_FIELDS = ("id", "name", "state", "repo", "machine", "number", "model", "agent", "mission", "path")
SESSION_LIST_DEFAULT_FIELDS = ("id", "name", "state", "repo")

# The roster's triage buckets this tool knows how to read (SessionOrdering on the Gateway).
_KNOWN_BUCKETS = ("needsYou", "active", "onHold")


class SessionStateError(Exception):
    """A roster row carries a triage bucket this tool does not know. Never guessed around."""


def plain_state(s: Dict[str, Any]) -> str:
    """Fold one roster row into its plain state: needs-you, working, ready, snoozed or crashed.

    RENDER, NEVER RULE. The Gateway has already folded the row into a triage bucket (the same fold the
    Cockpit uses); this only names that bucket in one word, splitting the active bucket by whether the
    agent is working right now. The order is the Architect's ruling for #2922:

    - crashed is true                          -> crashed
    - bucket needsYou                          -> needs-you
    - bucket onHold                            -> snoozed (this also holds supervised Workers that
                                                  have stopped - the Gateway parks them in the same bucket)
    - bucket active, activity state Working    -> working
    - bucket active otherwise                  -> ready

    A missing or unknown bucket raises SessionStateError naming the value. lastStatusReason is free text
    for people and is never read.
    """
    # The boolean is read DIRECTLY: a string "False" would be truthy, and absent is not a crash.
    if s.get("crashed", s.get("Crashed")) is True:
        return "crashed"
    bucket = s.get("triageBucket", s.get("TriageBucket"))
    if bucket == "needsYou":
        return "needs-you"
    if bucket == "onHold":
        return "snoozed"
    if bucket == "active":
        activity = s.get("activityState", s.get("ActivityState"))
        return "working" if activity == "Working" else "ready"
    sid = gateway.field(s, "sessionId", "SessionId") or "(no id)"
    shown = "missing" if bucket is None else repr(bucket)
    raise SessionStateError(
        f"session {axi_output.escape_ascii(sid)} has a triage bucket that is {axi_output.escape_ascii(shown)}; "
        f"this tool knows only {', '.join(_KNOWN_BUCKETS)}. "
        "If the Gateway has added a bucket, update cc-devthrottle; --json shows the raw rows."
    )


def _session_record(s: Dict[str, Any], state: str) -> Dict[str, object]:
    """Every field `session list` can show, for one roster row. Ids and names are never shortened.

    The row must carry a session id (see _require_session_ids). A name that is the empty string stays
    the empty string, so it renders as "" and reads back exactly; only an absent name is None.
    """
    repo_path = gateway.field(s, "repoPath", "RepoPath")
    number = s.get("number", s.get("Number"))
    name = s.get("name", s.get("Name"))
    return {
        "id": gateway.field(s, "sessionId", "SessionId"),
        "name": None if name is None else str(name),
        "state": state,
        "repo": _repo_name(repo_path) if repo_path else None,
        "machine": gateway.field(s, "machineName", "MachineName") or None,
        "number": number if isinstance(number, int) and not isinstance(number, bool) else None,
        "model": _model_text(s),
        "agent": gateway.field(s, "agent", "Agent") or None,
        "mission": gateway.field(s, "missionName", "MissionName") or None,
        "path": repo_path or None,
    }


_usage_error = usage_errors.usage_error


def _parse_states(requested: Optional[str]) -> Optional[List[str]]:
    """Turn a `--state` value (one state, or several separated by commas) into a list, or exit 2."""
    if requested is None:
        return None
    names = [part.strip() for part in requested.split(",")]
    unknown = [name for name in names if name not in SESSION_STATES]
    if unknown:
        listed = ", ".join("'" + axi_output.escape_ascii(name) + "'" for name in unknown)
        _usage_error(f"unknown --state value {listed}. Valid states: {', '.join(SESSION_STATES)}")
    return names


def _matches_repo(s: Dict[str, Any], repo: str) -> bool:
    """--repo matches the repository folder name (as the repo field shows it, ignoring case) or the
    full path, through the matcher repo list and worktree list use: a Windows path ignores case and
    slash direction, and any other path must match exactly, because /home/A/proj and /home/a/proj can
    be two different repositories."""
    path = gateway.field(s, "repoPath", "RepoPath")
    if not path:
        return False
    return matches_repo(_repo_name(path), path, repo)


def _matches_machine(s: Dict[str, Any], machine: str) -> bool:
    return (gateway.field(s, "machineName", "MachineName") or "").lower() == machine.strip().lower()


def _require_session_ids(sessions: List[Dict[str, Any]]) -> None:
    """A row with no session id cannot be named by any verb, so listing it with a blank id would hand
    the reader a row they cannot act on. It is a broken answer from the Gateway, and it fails loudly."""
    for index, s in enumerate(sessions):
        if not gateway.field(s, "sessionId", "SessionId").strip():
            name = s.get("name", s.get("Name"))
            shown = "no name" if name is None else f"the name {axi_output.format_value(str(name))}"
            axi_cli.fail(
                f"the Gateway returned a session with no session id (row {index + 1}, {shown}). "
                "This tool will not list a session it cannot name; --json shows the raw rows.",
                ["cc-devthrottle session list --json", axi_cli.CHECK_GATEWAY],
            )


def _fold_or_exit(sessions: List[Dict[str, Any]]) -> List[str]:
    try:
        return [plain_state(s) for s in sessions]
    except SessionStateError as err:
        axi_cli.fail(str(err), ["cc-devthrottle session list --json", axi_cli.CHECK_GATEWAY])


def list_sessions(
    json_output: bool,
    *,
    state: Optional[str] = None,
    repo: Optional[str] = None,
    machine: Optional[str] = None,
    fields: Optional[str] = None,
) -> None:
    """List every session running across the fleet, optionally narrowed by state, repository or machine."""
    # Usage errors come before the fetch: a bad flag is the caller's to fix, whatever the fleet holds.
    if json_output and fields is not None:
        _usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen_fields = usage_errors.parse_fields(fields, SESSION_LIST_FIELDS, SESSION_LIST_DEFAULT_FIELDS)
    wanted_states = _parse_states(state)
    for flag, value in (("--repo", repo), ("--machine", machine)):
        if value is not None and not value.strip():
            _usage_error(f"{flag} needs a value.")

    sessions, complete, reason, stale_caution = _get_fleet()
    caveat = _roster_caveat(complete, reason)
    filtered = wanted_states is not None or repo is not None or machine is not None

    # The state is folded only where it is needed, so an unfiltered --json never depends on the fold:
    # it prints exactly what the Gateway sent, as it always has.
    need_states = not json_output or wanted_states is not None
    if not json_output:
        _require_session_ids(sessions)
    states = _fold_or_exit(sessions) if need_states else [""] * len(sessions)
    rows = [
        (s, st)
        for s, st in zip(sessions, states)
        if (wanted_states is None or st in wanted_states)
        and (repo is None or _matches_repo(s, repo))
        and (machine is None or _matches_machine(s, machine))
    ]

    if json_output:
        # Plain print, not console.print: Rich wraps to 80 columns when stdout is not a TTY and
        # injects newlines into long values, producing invalid JSON for agents/pipes. A filter narrows
        # the same bare array; it never changes its shape.
        print(json.dumps([s for s, _ in rows] if filtered else sessions, indent=2))
        # Issue #1051: the caveat goes to STDERR, never stdout. The shape of this output is depended
        # on by agents and pipes, so it stays a bare array - but a caller acting on a partial roster
        # still has to be told, and stderr reaches a human without corrupting the parse.
        # The cautions are the Gateway's sentences and may not be ASCII; stderr is escaped exactly
        # as the plain output is.
        if caveat:
            print(f"WARNING: the fleet list may be incomplete. {axi_output.escape_ascii(caveat)}", file=sys.stderr)
        # An EMPTY machine-readable answer is a negative answer too, and the agent parsing it is the
        # reader most likely to act on "nothing is running" as a fact.
        if not rows and stale_caution:
            print(f"WARNING: {axi_output.escape_ascii(stale_caution)}", file=sys.stderr)
        return

    records = [_session_record(s, st) for s, st in rows]
    # No rows means no breakdown at all: the helper refuses an empty one, and "count: 0" says it all.
    breakdown = [(name, n) for name in SESSION_STATES if (n := sum(1 for _, st in rows if st == name))] or None
    blocks = [
        axi_output.format_count(len(rows), total=len(sessions) if filtered else None, breakdown=breakdown),
        axi_output.render_list("sessions", chosen_fields, records),
    ]

    if not rows:
        # Issue #1051, the worst sentence in the tool. "No sessions are running in the fleet" is a
        # claim about the WHOLE FLEET, and an empty roster with an unreachable Director does not
        # support it. Absent is not empty, and only one of the two is worth saying out loud.
        if filtered and sessions:
            blocks.append("No session matches the filter.")
        elif caveat or stale_caution:
            blocks.append("No sessions were returned, but this is not the whole fleet.")
        else:
            blocks.append("No sessions are running in the fleet.")
    # Issue #1051: printed AFTER the rows, so the rows the reader can trust come first and the
    # qualification lands on what they have just read.
    if caveat:
        blocks.append(f"This is not the whole fleet. {axi_output.escape_ascii(caveat)}")
    # The stale caution qualifies a negative answer only. Both cautions can be live at once, and on an
    # empty answer they say different things, so this is printed after the other, never instead of it.
    if not rows and stale_caution:
        blocks.append(axi_output.escape_ascii(stale_caution))

    blocks.append(axi_output.format_help(_session_list_help(rows, filtered, chosen_fields)))
    axi_output.write_blocks(sys.stdout, *blocks)


#: The --state line of the help block. It names all five states whatever the rows hold: the count line
#: lists only the states that have sessions, so without this an agent never learns that, for example,
#: `--state crashed` exists, and reads the whole fleet as JSON to find out instead.
SESSION_LIST_STATE_HELP = "cc-devthrottle session list --state " + "|".join(SESSION_STATES)

#: How to reach a listed session. Agents looking at the list guessed `session message` and `session
#: send`; the command lives in its own group.
SESSION_LIST_MESSAGE_HELP = 'cc-devthrottle message send <session-id> "<message>"'


def _session_list_help(rows: List[Tuple[Dict[str, Any], str]], filtered: bool, chosen_fields: List[str]) -> List[str]:
    """Concrete next commands. Runtime values are placeholders, never guessed."""
    if not rows:
        if filtered:
            return ["cc-devthrottle session list", SESSION_LIST_STATE_HELP, "cc-devthrottle session list --help"]
        return ["cc-devthrottle director list", "cc-devthrottle session spawn <repo> --controlled-by self"]
    commands = [SESSION_LIST_STATE_HELP, SESSION_LIST_MESSAGE_HELP]
    if list(chosen_fields) == list(SESSION_LIST_DEFAULT_FIELDS):
        commands.append("cc-devthrottle session list --fields " + ",".join(SESSION_LIST_FIELDS))
    commands.append("cc-devthrottle session list --json")
    commands.append("cc-devthrottle session whoami")
    return commands


# --- cc-devthrottle with no arguments (AXI principle 8, content first; issue #2922) ---

# The fields shown for this session when the command runs inside one.
LIVE_STATE_SESSION_FIELDS = ("id", "name", "state", "repo")


def show_live_state() -> None:
    """What `cc-devthrottle` prints with no arguments: who this session is, how many sessions need the
    owner, and the most useful next commands. `--help` still prints the help.

    Identity comes only from CC_SESSION_ID. Outside a session this says so and names nobody. Inside
    one, the session is looked up in the roster by its full id; a session the roster does not hold is
    reported as not found, never matched by prefix or name.
    """
    # Unlike list_sessions, a failed fetch goes to standard error in plain text: this is the first
    # command an agent runs, and the sentence it reads must be the Gateway's, with no markup in it.
    try:
        sessions, complete, reason, stale_caution = gateway.get_fleet()
    except gateway.GatewayError as err:
        axi_cli.fail(
            str(err),
            [axi_cli.CHECK_GATEWAY, "cc-devthrottle --help"],
        )
    _require_session_ids(sessions)
    states = _fold_or_exit(sessions)
    caveat = _roster_caveat(complete, reason)

    sid = gateway.session_id()
    blocks = []
    if sid is None:
        blocks.append("session: none - CC_SESSION_ID is not set, so this is not running inside a DevThrottle session.")
    else:
        mine = [
            (s, st) for s, st in zip(sessions, states)
            if gateway.field(s, "sessionId", "SessionId").strip().lower() == sid.lower()
        ]
        if mine:
            blocks.append(axi_output.render_list(
                "session", LIVE_STATE_SESSION_FIELDS, [_session_record(s, st) for s, st in mine]
            ))
        else:
            blocks.append(
                f"session: {axi_output.format_value(sid)} - this is CC_SESSION_ID, "
                "but the fleet list the Gateway returned does not hold it."
            )

    breakdown = [(name, n) for name in SESSION_STATES if (n := states.count(name))] or None
    needs_you = states.count("needs-you")
    blocks.append(axi_output.format_count(len(sessions), breakdown=breakdown))
    blocks.append(f"needs-you: {needs_you}")
    if caveat:
        blocks.append(f"This is not the whole fleet. {axi_output.escape_ascii(caveat)}")
    # A count of zero is a negative answer, which is the one case the stale caution qualifies.
    if needs_you == 0 and stale_caution:
        blocks.append(axi_output.escape_ascii(stale_caution))

    blocks.append(axi_output.format_help(_live_state_help(sid is not None, needs_you, len(sessions))))
    axi_output.write_blocks(sys.stdout, *blocks)


def _live_state_help(inside_session: bool, needs_you: int, total: int) -> List[str]:
    """Concrete next commands. Runtime values are placeholders, never guessed."""
    commands = []
    if needs_you:
        commands.append("cc-devthrottle session list --state needs-you")
    if total:
        commands.append("cc-devthrottle session list")
    else:
        commands.append("cc-devthrottle director list")
    if inside_session:
        commands.append("cc-devthrottle session spawn <repo> --controlled-by self")
    commands.append("cc-devthrottle --help")
    return commands


def whoami() -> None:
    """Show this session's own fleet identity."""
    sid = gateway.session_id()
    if not sid:
        axi_cli.fail(
            "CC_SESSION_ID is not set, so this is not running inside a DevThrottle session and has no "
            "identity to show. Run it from inside a session.",
            ["cc-devthrottle session list"],
        )

    # Completeness is deliberately ignored here: whoami looks up THIS session, which lives on the
    # Director being asked, and a Director always reports its own sessions (issue #1019). An
    # unreachable Director elsewhere cannot hide the caller from itself.
    sessions, _, _, _ = _get_fleet()
    me = next(
        (s for s in sessions if gateway.field(s, "sessionId", "SessionId").lower() == sid.lower()),
        None,
    )
    if me is None:
        console.print(f"You are session {sid}. The fleet list does not hold it yet, so nothing more is known.")
    else:
        name = gateway.field(me, "name", "Name") or "(unnamed)"
        machine = gateway.field(me, "machineName", "MachineName") or "this machine"
        repo = gateway.field(me, "repoPath", "RepoPath")
        # gateway.field answers "" for an absent number, never None, so test for a value.
        number = gateway.field(me, "number", "Number")
        number_text = f"number {number}, " if number else ""
        console.print(
            f'You are session {number_text}{sid} ("{axi_cli.shown(name)}") on {axi_cli.shown(machine)}, '
            f"repo {axi_cli.shown(_repo_name(repo))}."
        )

    axi_cli.print_next([
        'cc-devthrottle message send <session-id> "<message>"',
        'cc-devthrottle message send all "<message>"',
        "cc-devthrottle session list",
    ])


#: The next step after a call about one session failed: look the session up again.
_CHECK_SESSION = ["cc-devthrottle session list", axi_cli.CHECK_GATEWAY]


def _is_true(value: Any) -> bool:
    return value is True


def _is_false(value: Any) -> bool:
    return value is False


def _same_session(sid: str) -> Any:
    """Accepts the answer's session id only when it is the session the change was sent to."""
    return lambda value: isinstance(value, str) and value.lower() == sid.lower()


def _accepted_or_fail(resp: Any, what: str, next_commands: List[str]) -> None:
    """A prompt-shaped answer (accepted, error) must say accepted: true. A refusal is reported in the
    Gateway's own words; an answer with no verdict at all is reported as unknown."""
    if isinstance(resp, dict) and resp.get("accepted", resp.get("Accepted")) is False:
        err = resp.get("error") or resp.get("Error")
        axi_cli.fail(
            f"{what} was not accepted: {err}" if err else f"{what} was not accepted, and the Gateway gave no reason.",
            next_commands,
        )
    axi_cli.confirmed(resp, ("accepted", "Accepted"), what, next_commands, accept=_is_true)


def rename_session(target: Optional[str], new_name: str) -> Dict[str, Any]:
    """Rename a target session, defaulting to the current session."""
    name = new_name.strip()
    if not name:
        axi_cli.usage_error(
            "the new session name is blank. "
            'Pass a name: cc-devthrottle session rename [<session-id>] "<new name>"',
        )

    sid = resolve_target_or_current(target, "cc-devthrottle session rename")
    try:
        # The Gateway renames a session anywhere in the account and answers with the updated row.
        resp = gateway.patch_json(f"sessions/{sid}", {"name": name})
    except gateway.GatewayError as err:
        axi_cli.fail(f"could not rename session {sid}: {err}", _CHECK_SESSION)

    # The answer is the renamed row. Both values are read from it, never from what was asked: an answer
    # without them - {} included - cannot say the rename happened. The Director only trims the name it
    # is given (SessionManager.RenameSession), and `name` is already trimmed, so the returned name must
    # be exactly it; any other name - the old one included - means this rename did not land.
    what = f"the rename of session {sid}"
    actual_sid = axi_cli.confirmed(resp, ("sessionId", "SessionId"), what, _CHECK_SESSION, accept=_same_session(sid))
    actual = axi_cli.confirmed(resp, ("name", "Name"), what, _CHECK_SESSION)
    if actual != name:
        axi_cli.fail(
            f'the Gateway\'s answer to {what} gave the name "{axi_cli.ascii_text(str(actual))}", not the '
            f'requested "{axi_cli.ascii_text(name)}", so the session was not renamed as asked.',
            _CHECK_SESSION,
        )
    console.print(f'[green]Renamed[/green] {actual_sid} to "{axi_cli.shown(actual)}".')
    axi_cli.print_next([
        "cc-devthrottle session list",
        f'cc-devthrottle message send {axi_cli.bare(actual_sid, "<session-id>")} "<message>"',
    ])
    return resp


def prompt_session(target: str, text: str, no_submit: bool = False) -> Dict[str, Any]:
    """Send raw text into a session - what a human typing into it would produce.

    Unlike `message send`, this does NOT frame the text with a sender. Restores the old
    POST /sessions/{sid}/prompt.
    """
    if not text.strip():
        axi_cli.usage_error(
            "the prompt text is blank. "
            'Pass the text to type: cc-devthrottle session prompt <session-id> "<text>"',
        )
    sid = resolve_target_or_current(target, "cc-devthrottle session prompt")
    try:
        resp = gateway.post_json(
            f"sessions/{sid}/prompt", {"text": text, "appendEnter": not no_submit}
        )
    except gateway.GatewayError as err:
        axi_cli.fail(f"could not send the prompt to session {sid}: {err}", _CHECK_SESSION)
    # A 200 can still carry accepted: false - a menu on the screen blocks typing - so the verdict is read.
    _accepted_or_fail(resp, f"the prompt to session {sid}", [
        f"cc-devthrottle session buffer {axi_cli.bare(sid, '<session-id>')}", *_CHECK_SESSION,
    ])
    console.print(f"[green]Sent[/green] prompt to {sid}.")
    axi_cli.print_next([
        f"cc-devthrottle session buffer {axi_cli.bare(sid, '<session-id>')}",
        f"cc-devthrottle session interrupt {axi_cli.bare(sid, '<session-id>')}",
    ])
    return resp if isinstance(resp, dict) else {}


def interrupt_session(target: Optional[str]) -> Dict[str, Any]:
    """Stop what a session is currently doing. Restores the old POST /sessions/{sid}/interrupt."""
    sid = resolve_target_or_current(target, "cc-devthrottle session interrupt")
    try:
        resp = gateway.post_json(f"sessions/{sid}/interrupt")
    except gateway.GatewayError as err:
        axi_cli.fail(f"could not interrupt session {sid}: {err}", _CHECK_SESSION)
    _accepted_or_fail(resp, f"the interrupt of session {sid}", [
        f"cc-devthrottle session buffer {axi_cli.bare(sid, '<session-id>')}", *_CHECK_SESSION,
    ])
    console.print(f"[green]Interrupted[/green] {sid}.")
    axi_cli.print_next([
        f"cc-devthrottle session buffer {axi_cli.bare(sid, '<session-id>')}",
        f'cc-devthrottle session prompt {axi_cli.bare(sid, "<session-id>")} "<text>"',
    ])
    return resp if isinstance(resp, dict) else {}


def hold_session(target: Optional[str], release: bool = False, minutes: Optional[int] = None) -> Dict[str, Any]:
    """Park a session, or release it. Restores the old POST /sessions/{sid}/hold.

    A hold asked for while the session is still working is DEFERRED: it applies when the turn
    settles, and the response's pending flag says so. A held session that starts working again
    always takes itself off hold.
    """
    # Refused before anything is sent: a timer on a release would otherwise be dropped without a word.
    if release and minutes is not None:
        axi_cli.usage_error(
            "--minutes and --release cannot be used together: --minutes sets how long a hold lasts, "
            "and --release ends the hold. "
            "Drop --minutes to release the hold, or drop --release to hold for that long.",
        )
    if minutes is not None and minutes < 1:
        axi_cli.usage_error(
            f"--minutes must be at least 1, not {minutes}. "
            "Pass a whole number of minutes: cc-devthrottle session hold [<session-id>] --minutes <n>",
        )
    sid = resolve_target_or_current(target, "cc-devthrottle session hold")
    body: Dict[str, Any] = {"onHold": not release}
    if minutes is not None:
        body["snoozeMinutes"] = minutes
    try:
        resp = gateway.post_json(f"sessions/{sid}/hold", body)
    except gateway.GatewayError as err:
        what = "release the hold on" if release else "hold"
        axi_cli.fail(f"could not {what} session {sid}: {err}", _CHECK_SESSION)

    # READ STRAIGHT OFF THE DICT: gateway.field stringifies, and str(False) is "False", which is truthy -
    # so a hold that was applied at once would have been reported as queued.
    # The answer says where the hold now stands: onHold, and pending for a hold that waits for the turn
    # to end. A release is confirmed by onHold: false; a hold by onHold: true or pending: true.
    what = f"the {'release' if release else 'hold'} of session {sid}"
    on_hold = axi_cli.confirmed(
        resp, ("onHold", "OnHold"), what, _CHECK_SESSION, accept=lambda v: isinstance(v, bool),
    )
    pending = resp.get("pending", resp.get("Pending")) is True
    if release == (on_hold or pending):
        axi_cli.fail(
            f"the Gateway's answer to {what} says onHold is {str(on_hold).lower()} and pending is "
            f"{str(pending).lower()}, so the session was not {'released' if release else 'held'}.",
            _CHECK_SESSION,
        )
    if release:
        console.print(f"[green]Released[/green] {sid} - no longer held.")
        axi_cli.print_next([
            "cc-devthrottle session list --state needs-you",
            f"cc-devthrottle session hold {axi_cli.bare(sid, '<session-id>')} --minutes <n>",
        ])
    else:
        if pending:
            console.print(f"[green]Hold queued[/green] {sid} is still working; it parks when it finishes.")
        else:
            for_text = f" for {minutes} minutes" if minutes else ""
            console.print(f"[green]Held[/green] {sid}{for_text}.")
        axi_cli.print_next([
            "cc-devthrottle session list --state snoozed",
            f"cc-devthrottle session hold {axi_cli.bare(sid, '<session-id>')} --release",
        ])
    return resp if isinstance(resp, dict) else {}


def raise_hand(reason: Optional[str], target: Optional[str] = None, clear: bool = False) -> Dict[str, Any]:
    """Put your hand up to the session that is driving you, or take it back down.

    A supervised session (a worker with a live supervisor, or a scheduled run) is quiet toward the
    owner BY CONSTRUCTION - it is parked on every screen the moment it stops working, and it has no
    channel to him. This is the channel it has instead: it tells the session that started it, in its
    own words, what decision it is blocked on. An ARCHITECT is NOT supervised (the owner's ruling,
    2026-09-06: "the architect is always the session i talk to"), so it reaches him directly.

    Your hand LOWERS ITSELF when you stop working. That is not a shortcut - stopping already tells
    your supervisor to look at you, so a flag that outlived the turn would just be noise nobody
    cleared. Raise it when you are mid-turn and cannot go on without an answer.
    """
    if clear:
        if reason is not None:
            axi_cli.usage_error(
                "a reason and --clear cannot be used together: --clear takes the hand down, so there "
                "is nothing for the reason to say. "
                'Run cc-devthrottle session raise --clear on its own, or drop --clear to raise the hand.',
            )
        body: Dict[str, Any] = {"raised": False}
    else:
        text = (reason or "").strip()
        if not text:
            axi_cli.usage_error(
                "say what you need. A raised hand with no words is a 'notice me' ping - your supervisor "
                "would have to open you to find out what for, which is the work this is meant to save. "
                'Pass the question: cc-devthrottle session raise "<what you are blocked on>"',
            )
        body = {"raised": True, "reason": text}

    sid = resolve_target_or_current(target, "cc-devthrottle session raise")
    try:
        resp = gateway.post_json(f"sessions/{sid}/needs-manager", body)
    except gateway.GatewayError as err:
        what = "lower the hand of" if clear else "raise the hand of"
        axi_cli.fail(f"could not {what} session {sid}: {err}", _CHECK_SESSION)

    change = f"{'lowering' if clear else 'raising'} the hand of session {sid}"
    axi_cli.confirmed(resp, ("sessionId", "SessionId"), change, _CHECK_SESSION, accept=_same_session(sid))
    axi_cli.confirmed(resp, ("raised", "Raised"), change, _CHECK_SESSION, accept=_is_false if clear else _is_true)

    target_flag = f" --target {axi_cli.bare(sid, '<session-id>')}" if target is not None and target.strip() else ""
    if clear:
        console.print(f"[green]Hand down[/green] {sid}.")
        axi_cli.print_next([f'cc-devthrottle session raise "<what you need>"{target_flag}'])
    else:
        console.print(
            f"[green]Hand up[/green] {sid}. Your supervisor sees it on the roster while you keep "
            "working; it lowers itself when your turn ends."
        )
        axi_cli.print_next([f"cc-devthrottle session raise --clear{target_flag}"])
    return resp if isinstance(resp, dict) else {}


def report_to_parent(summary: Optional[str], target: Optional[str] = None) -> None:
    """Tell the session that owns you what you did, now that your turn has ended.

    THE LAST STEP OF DELEGATED WORK, NOT A NOTIFICATION. Your parent asked you to do something;
    getting back to them is part of doing it. This is the session doing that itself, in its own
    words - not the roster hoping somebody wanders past a grey row and wonders about it.

    IT INTERRUPTS, DELIBERATELY (owner's ruling, 2026-09-13). Every fleet message lands mid-turn in
    the receiving agent, and that is the right cost here: a parent that took ownership of a session
    took on being interrupted when it comes back. The alternative already existed and is what failed
    - the hand-raise registry is pull-only, so a supervisor learns nothing unless it thinks to look,
    which is how a finished session sits quiet and finished with nobody ever told. A signal nobody is
    obliged to read is a signal that does not exist. The load is bounded by how many sessions a
    parent CHOSE to own, and since ownership must now be declared at spawn, owning five of them is a
    deliberate act rather than an accident of an environment variable.

    NO PARENT MEANS THE USER, AND THEN THERE IS NOTHING TO SEND. A session the user owns is already
    red and already in his queue - that red IS the report, and messaging him a second time through a
    channel he does not read would be noise. This prints what happened and exits successfully,
    because having no parent is a correct answer to "who do I report to", not a failure.

    IT REFUSES RATHER THAN GUESS. "Do I have a parent?" is answered from the fleet, so a roster this
    process could not read in full is not evidence of having none - it is not knowing. An
    absence-shaped check that fails open here would quietly convert every unreadable roster into
    "you are the user's", and a worker would stop reporting to a supervisor that was alive the whole
    time. So an incomplete roster, or a roster that does not contain this session, is an error.
    """
    text = (summary or "").strip()
    if not text:
        axi_cli.usage_error(
            "say what you did. A report with no words is a 'notice me' ping - your parent would have "
            "to open you to find out what happened, which is the work this is meant to save. "
            'Pass one or two sentences: cc-devthrottle session report "<what you did, and anything they must decide>"',
        )

    sid = resolve_target_or_current(target, "cc-devthrottle session report")
    sessions, complete, reason, _stale = _get_fleet()
    if not complete:
        axi_cli.fail(
            f"the fleet roster could not be read in full{(' - ' + reason) if reason else ''}. "
            "Refusing to report, because a roster that is missing sessions cannot tell 'you have no "
            "parent' apart from 'your parent is one of the rows I could not see'. Try again once the "
            "session list reads the whole fleet, or name the session yourself.",
            ["cc-devthrottle session list", 'cc-devthrottle message send <session-id> "<message>"'],
        )

    me = None
    for s in sessions:
        if gateway.field(s, "sessionId", "SessionId") == sid:
            me = s
            break
    if me is None:
        axi_cli.fail(
            f"session {sid} is not in the fleet roster, so who owns it cannot be answered. Refusing to guess.",
            ["cc-devthrottle session whoami", "cc-devthrottle session list"],
        )

    # The SAME fact the roster folds its colour from, read here so the report and the dot can never
    # disagree about who owns this session.
    #
    # READ STRAIGHT OFF THE DICT, NOT THROUGH gateway.field. That helper returns a STRING always, and
    # str(False) is "False", which is truthy - so every orphan would have tested as having a live
    # parent and sent its report into a dead session's mailbox. Delivered, unread, lost, with the
    # session believing it had handed its work back. The helper's own docstring says not to use it
    # for booleans; this comment is here because it was used for one anyway.
    has_parent = me.get("hasLiveSupervisor", me.get("HasLiveSupervisor", False)) is True
    parent_id = gateway.field(me, "controllerSessionId", "ControllerSessionId")

    if not has_parent or not parent_id:
        console.print(
            "[green]No parent - the USER owns you.[/green] Nothing was sent, and that is correct: "
            "you are red on his roster and in his queue the moment your turn ends, so that red IS "
            "your report. Leave your answer where he will read it - in this session."
        )
        axi_cli.print_next(["cc-devthrottle session whoami", "cc-devthrottle session done"])
        return

    try:
        resp = gateway.post_json(f"sessions/{parent_id}/message", {"text": text})
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"could not deliver the report to the parent session {parent_id}: {err}",
            ["cc-devthrottle session list", axi_cli.CHECK_GATEWAY],
        )

    parent_name = None
    for s in sessions:
        if gateway.field(s, "sessionId", "SessionId") == parent_id:
            parent_name = gateway.field(s, "name", "Name")
            break
    label = parent_name or gateway.short_id(parent_id)
    _report_delivery(resp, f"{label} ({parent_id})")
    axi_cli.print_next([
        f"cc-devthrottle session buffer {axi_cli.bare(parent_id, '<session-id>')}",
        "cc-devthrottle session done",
    ])


def list_my_workers(target: Optional[str] = None) -> None:
    """Show the sessions THIS session is driving, and which of them have their hand up.

    The manager's half of the supervised rule. Workers never reach the owner, so a manager is the
    only one who can see a blocked one - and the design says a manager learns by READING its workers,
    not by being messaged 'notice me'. This is that read, in one line instead of one session at a time.
    """
    me = resolve_target_or_current(target, "cc-devthrottle session workers")
    try:
        rows = gateway.get_json("sessions")
    except gateway.GatewayError as err:
        axi_cli.fail(f"could not read the fleet list: {err}", [axi_cli.CHECK_GATEWAY])

    sessions = rows.get("sessions") if isinstance(rows, dict) else rows
    if not isinstance(sessions, list):
        axi_cli.fail(
            "the Gateway did not return a session list, so the sessions you drive cannot be shown.",
            ["cc-devthrottle session list", axi_cli.CHECK_GATEWAY],
        )

    mine = [
        x for x in sessions
        if str(gateway.field(x, "controllerSessionId", "ControllerSessionId") or "").lower() == me.lower()
    ]
    if not mine:
        console.print("You are not driving any sessions.")
        return

    table = Table(title=f"Sessions driven by {gateway.short_id(me)}", box=box.ASCII)
    table.add_column("ID")
    table.add_column("NAME")
    table.add_column("STATE")
    table.add_column("HAND UP - WHAT THEY NEED")
    for x in mine:
        sid = str(gateway.field(x, "sessionId", "SessionId") or "")
        # STRAIGHT OFF THE DICT. gateway.field returns a STRING always, and str(False) is "False",
        # which is truthy - so `bool(gateway.field(...))` read EVERY worker as having its hand up.
        # Measured on the live wire: needsManager arrives present and false, so this fired on every
        # row. It has been invisible only because the reason is null when the hand is down, leaving
        # an empty cell; a reason that ever outlived a lowered hand would have shown a worker asking
        # for a supervisor it was not asking for. The helper's own docstring warns against this.
        raised = x.get("needsManager", x.get("NeedsManager", False)) is True
        reason = str(gateway.field(x, "needsManagerReason", "NeedsManagerReason") or "")
        table.add_row(
            gateway.short_id(sid),
            str(gateway.field(x, "name", "Name") or ""),
            str(gateway.field(x, "stateLabel", "StateLabel") or ""),
            f"[yellow]{reason}[/yellow]" if raised else "",
        )
    console.print(table)
    # A session that has STOPPED has its hand lowered by the fold, so an empty column does not mean
    # "nothing needs you" - it means nothing needs you MID-TURN. Say so rather than let the table imply it.
    console.print(
        "[dim]A hand lowers itself when its session stops working. A stopped worker has finished or is "
        "stuck - read it to find out which.[/dim]"
    )


def compact_session(target: Optional[str], continue_prompt: Optional[str]) -> Dict[str, Any]:
    """Compact a session's context and, unless asked not to, continue it. Issue #2150.

    A full session cannot read anything sent to it, so this is the only rescue that works from
    outside. The call BLOCKS until the tool reports the compaction finished - which is why the
    timeout here is generous - and the follow-up is sent at that moment, never on a guessed delay.
    """
    verb = "cc-devthrottle session compact-continue" if continue_prompt is not None else "cc-devthrottle session compact"
    if continue_prompt is not None and not continue_prompt.strip():
        axi_cli.usage_error(
            "the message to send after compacting is blank. "
            'Pass a message, or leave it out to send "continue": '
            'cc-devthrottle session compact-continue [<session-id>] ["<message>"]',
        )
    sid = resolve_target_or_current(target, verb)
    body: Dict[str, Any] = {}
    if continue_prompt:
        body["continuePrompt"] = continue_prompt
    try:
        # Outermost bound of three: this waits longer than the Gateway waits for the Director, which
        # waits longer than the Director waits for the tool. The innermost one fires first and says
        # what actually failed.
        resp = gateway.post_json(f"sessions/{sid}/compact-context", body, timeout=300)
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"could not compact session {sid}: {err}",
            [f"cc-devthrottle session buffer {axi_cli.bare(sid, '<session-id>')}"],
        )

    # submitted is the Director's word that the compaction was sent at all; without it nothing happened
    # that this can report, not even "submitted".
    axi_cli.confirmed(
        resp, ("submitted", "Submitted"), f"compacting session {sid}",
        [f"cc-devthrottle session buffer {axi_cli.bare(sid, '<session-id>')}"], accept=_is_true,
    )
    body = resp
    detail = gateway.field(body, "detail", "Detail")
    # Read the flag as a BOOLEAN, not through gateway.field: that helper stringifies, and str(False) is
    # "False" - a truthy string. Routed through it, a compaction nobody watched would be announced as
    # "Compacted", which is the one thing this line must never say without evidence.
    observed = bool(body.get("compactionObserved", body.get("CompactionObserved", False)))
    label = "[green]Compacted[/green]" if observed else "[yellow]Compaction submitted[/yellow]"
    console.print(f"{label} {sid}. {axi_cli.shown(detail or '')}")
    steps = [f"cc-devthrottle session buffer {sid}"]
    if continue_prompt is None:
        steps.append(f'cc-devthrottle message send {sid} "<message>"')
    axi_cli.print_next(steps)
    return resp if isinstance(resp, dict) else {}


def read_session_buffer(target: Optional[str]) -> None:
    """Print what a session's terminal is showing. Restores the old GET /sessions/{sid}/buffer."""
    sid = resolve_target_or_current(target, "cc-devthrottle session buffer")
    try:
        resp = gateway.get_json(f"sessions/{sid}/buffer")
    except gateway.GatewayError as err:
        # Plain text to standard error, never Rich markup: the error text comes from the server, so it
        # is no more ours to trust than the buffer itself - it can quote a path or a fragment of the
        # session's own output, and a token like [/tmp/x] once raised MarkupError from this very branch.
        axi_cli.fail(f"could not read the terminal of session {sid}: {err}", _CHECK_SESSION)

    # The buffer verb returns the terminal text under one of a couple of shapes depending on the
    # path it came back through; print whichever carries the text rather than guessing one.
    # A key that is ABSENT is not an empty terminal: gateway.field answers "" for both, which printed a
    # blank line and exited 0 for an answer that carried no text at all.
    text = None
    if isinstance(resp, dict):
        for key in ("text", "Text", "buffer", "Buffer"):
            if isinstance(resp.get(key), str):
                text = resp[key]
                break
    elif isinstance(resp, str):
        text = resp
    if text is None:
        axi_cli.fail(
            f"the Gateway's answer for session {sid} carried no terminal text. Try again.",
            [f"cc-devthrottle session buffer {axi_cli.bare(sid, '<session-id>')}", axi_cli.CHECK_GATEWAY],
        )
    # Plain print, not console.print, for the same reason as list_sessions above. This is raw terminal
    # text from another session, so it is arbitrary and nobody controls its shape: Rich reads a token
    # like [/tmp/x] as a closing tag and raises MarkupError - an uncaught traceback out of a read-only
    # verb - eats style-shaped tokens like [bold], and rewraps every line at 80 columns when stdout is
    # not a TTY, which is exactly how an agent or a pipe calls this.
    #
    # What this does and does not promise: the text is not INTERPRETED - not parsed as markup, not
    # rewrapped, not truncated, nothing added or removed in the middle. It is not a byte-for-byte
    # guarantee, and claiming one would be a lie the next reader would rely on: print appends a trailing
    # newline, the text layer translates newlines on the way out, and this module reconfigures stdout
    # with errors="replace" (line 73), so a character the console encoding cannot represent still
    # becomes a replacement character. Those three are the whole of the difference.
    print(text)


def set_session_role(target: Optional[str], role: Optional[str]) -> Dict[str, Any]:
    """Declare a session's explicit role, defaulting to the current session.

    Restores the set-role verb the tunnel-only cut removed with POST /sessions/{sid}/role, which
    left a running session stuck with the role it was born with. Architect cannot be derived from
    the spawn graph, so this is the only way to make one after birth. An empty role clears the
    explicit role and reverts the session to auto-derivation.
    """
    sid = resolve_target_or_current(target, "cc-devthrottle session role")
    wanted = (role or "").strip()
    try:
        resp = gateway.post_json(f"sessions/{sid}/role", {"role": wanted})
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"could not set the role of session {sid}: {err}",
            [f"cc-devthrottle session role {axi_cli.bare(sid, '<session-id>')} <Standalone|Manager|Worker|Architect|none>", *_CHECK_SESSION],
        )

    # The answer is the session's row. Its id says the row is this session; its explicitRole must be the
    # role asked for, or null when the role was cleared. An answer without the id - {} included - would
    # otherwise read as "Role cleared", whatever was asked. ABSENT IS NOT CLEARED: the Director writes
    # explicitRole on every row, as null when there is none, so a row without the field is a missing
    # answer and exits 1 here rather than reading as a cleared role.
    what = f"setting the role of session {sid}"
    actual_sid = axi_cli.confirmed(resp, ("sessionId", "SessionId"), what, _CHECK_SESSION, accept=_same_session(sid))
    axi_cli.confirmed(
        resp, ("explicitRole", "ExplicitRole"), what, _CHECK_SESSION,
        accept=lambda v: axi_cli.is_cleared(v) or isinstance(v, str),
    )
    explicit = gateway.field(resp, "explicitRole", "ExplicitRole")
    if explicit.lower() != wanted.lower():
        # A blank role is not a cleared one (only null or "" is), so it is shown quoted, not as "none".
        shown_role = explicit if explicit.strip() else (repr(explicit) if explicit else "none")
        axi_cli.fail(
            f"the Gateway's answer to {what} gave the explicit role {shown_role}, not "
            f"{wanted or 'none'}, so the role was not changed as asked.",
            _CHECK_SESSION,
        )
    # Only the explicit role is reported: Worker/Manager derivation needs the fleet-wide spawn graph, which
    # lives in the Gateway, so the effective role is read from `session list`, not returned here.
    if explicit:
        console.print(f"[green]Role set[/green] {actual_sid} is now explicitly {axi_cli.shown(explicit)}.")
        axi_cli.print_next([f"cc-devthrottle session role {axi_cli.bare(actual_sid, '<session-id>')} none", "cc-devthrottle session list"])
    else:
        console.print(f"[green]Role cleared[/green] {actual_sid} reverts to automatic role derivation.")
        axi_cli.print_next([f"cc-devthrottle session role {axi_cli.bare(actual_sid, '<session-id>')} <role>", "cc-devthrottle session list"])
    return resp


def mark_done(target: Optional[str], reason: Optional[str]) -> Dict[str, Any]:
    """Flag a session for deletion, defaulting to the current session.

    The session is not killed synchronously - it is flagged, and the owning Director's
    deletion reaper removes it on a sweep after its grace period has passed and the session is no
    longer working; a session that stays working stays listed. This is how an unattended run tears ITSELF down when it has
    nothing left for the user, instead of lingering as a dead session in the fleet.
    """
    sid = resolve_target_or_current(target, "cc-devthrottle session done")
    body: Dict[str, Any] = {}
    if reason and reason.strip():
        body["reason"] = reason.strip()
    try:
        resp = gateway.post_json(f"sessions/{sid}/request-deletion", body)
    except gateway.GatewayError as err:
        axi_cli.fail(f"could not flag session {sid} for deletion: {err}", _CHECK_SESSION)
    axi_cli.confirmed(
        resp, ("pendingDeletion", "PendingDeletion"), f"flagging session {sid} for deletion",
        _CHECK_SESSION, accept=_is_true,
    )

    console.print(
        f"[green]Marked[/green] {sid} for deletion; "
        "the Director will reap it shortly."
    )
    axi_cli.print_next([f"cc-devthrottle session done {axi_cli.bare(sid, '<session-id>')} --undo", "cc-devthrottle session list"])
    return resp


def undo_done(target: Optional[str], reason: Optional[str] = None) -> Dict[str, Any]:
    """Take a pending deletion back off a session, defaulting to the current session.

    The opposite of mark_done, and the cure for having asked politely about the wrong session. Being
    able to stop a session outright is not a substitute: making the only remedy for a mistyped target
    the most destructive act the product has would have it exactly backwards.

    NO REASON IS ASKED FOR, DELIBERATELY. A stop carries a reason because it is destructive and is
    recorded; clearing a flag is the safe direction and there is nothing to justify. A reason passed
    here is therefore refused rather than dropped - see below for why refusing is the honest answer.
    """
    if reason is not None:
        # --undo with --reason is a contradiction, and both ways of resolving it quietly are worse
        # than refusing. Dropping the reason lets the caller walk away believing something was
        # recorded that never was. Recording it would attach a justification to the one session
        # operation that needs none, and would put a sentence in the trail for an act that is not an
        # intervention. So say the two do not go together, and let the caller choose.
        axi_cli.usage_error(
            "--undo and --reason cannot be used together, so nothing was changed. "
            "--reason is shown while a session winds down, and --undo is what cancels that "
            "wind-down, so there is nothing left for the reason to be shown on. Re-run "
            "cc-devthrottle session done --undo on its own, or drop --undo to flag the session."
        )

    sid = resolve_target_or_current(target, "cc-devthrottle session done --undo")
    try:
        resp = gateway.delete(f"sessions/{sid}/request-deletion")
    except gateway.GatewayError as err:
        # Plain text to standard error: the server's sentence can quote a path or a fragment of another
        # session's output, and a token shaped like [/tmp/x] once raised MarkupError from this branch.
        axi_cli.fail(f"could not clear the deletion flag on session {sid}: {err}", _CHECK_SESSION)
    axi_cli.confirmed(
        resp, ("pendingDeletion", "PendingDeletion"), f"clearing the deletion flag on session {sid}",
        _CHECK_SESSION, accept=_is_false,
    )

    console.print(
        f"[green]Cleared[/green] {sid} is no longer marked for deletion.", soft_wrap=True
    )
    axi_cli.print_next(["cc-devthrottle session list", f"cc-devthrottle session done {axi_cli.bare(sid, '<session-id>')}"])
    return resp if isinstance(resp, dict) else {}


#: The one thing `session stop` knows that the Gateway cannot: how to type its own flag. Everything
#: else an operator reads about a stop is written on the Gateway and printed here verbatim.
STOP_REASON_FLAG_HINT = (
    'Re-run with --reason "why you are stopping it" (-r says the same thing).'
)


def _stop_target(target: str) -> str:
    """The session id `session stop` addresses - and the raw target when nothing on the roster matches.

    THIS DELIBERATELY DOES NOT GO THROUGH _resolve_target, and that is the whole point of it.
    _resolve_target prints "No session matches" and exits 1 when the roster has nothing for a target.
    For a stop that is the exact defect Ruling 3 exists to prevent: an operator who stops a session
    twice would get an error the second time and read it as "it is still alive". The Gateway answers
    a target it cannot find with a 200 and the verdict notOnFleet, in words that say no machine was
    asked - and that careful answer never gets a chance to be read if the client exits first.

    So an unmatched target is sent on exactly as it was typed, and the GATEWAY rules on it. That is
    Ruling 5 applied to the one case where a client is most tempted to rule for itself.

    An AMBIGUOUS target is different and stays an error. "Which of these three did you mean" is a
    question about what the caller typed, not a verdict about any session's state, and guessing one
    of three sessions to end would be the worst possible way to be helpful.
    """
    wanted = target.strip()
    sessions, _complete, _reason, _stale = _get_fleet()
    matches = gateway.resolve_target(sessions, wanted)
    if len(matches) > 1:
        _refuse_ambiguous_target(wanted, matches, command_name="cc-devthrottle session stop")
    if matches:
        return gateway.field(matches[0], "sessionId", "SessionId")
    return wanted


#: What a failed stop is announced with when the request was REFUSED outright - nothing was carried
#: out, so "not stopped" is a fact the client holds rather than a guess about a machine it cannot see.
STOP_REFUSED_PREFIX = "Not stopped:"

#: What a failed stop is announced with when the outcome is genuinely UNKNOWN. A lost reply, a
#: dropped connection or a Director that answered late can all happen AFTER the session was ended, and
#: the Gateway says so in terms: "It is not known whether the command was carried out." This client
#: used to print "Not stopped:" over the top of that sentence - a client composing a verdict, in the
#: same line as the server saying there is no verdict to be had.
STOP_UNKNOWN_PREFIX = "Outcome unknown:"


def _refused_outright(status: Optional[int]) -> bool:
    """Whether a failed call was refused before anything could have been carried out.

    A 4xx on this route is a refusal: no reason was given, the key was not accepted, no tenant was
    bound, the route was not found. Every one of those is decided before the owning Director is
    asked, so nothing was stopped and saying so is reporting rather than guessing.

    EVERYTHING ELSE IS UNKNOWN, INCLUDING EVERY 5xx AND EVERY FAILURE WITH NO STATUS AT ALL. A 504 is
    the Gateway's own "the Director did not answer in time", a 502 covers both "the command was not
    delivered" and "the connection dropped while it was being sent", and a timeout or a dropped
    socket here never reached a status. The stop may have happened in any of them. The unknown side
    is the safe default, so a status this client has never seen lands there.
    """
    return status is not None and 400 <= status < 500


def _failure_prefix(status: Optional[int]) -> str:
    """The one thing this command may put in front of a failure: what it knows, never what it hopes.

    A 502 that carries the Director's own "the process would not die" sentence is announced as
    unknown, which is weaker than that sentence deserves. It is the honest side of a genuine limit:
    the Gateway gives a Director-reported failure and a tunnel that dropped mid-command the same
    status, so this client cannot tell them apart, and the Director's definite words are printed
    directly underneath in any case.
    """
    return STOP_REFUSED_PREFIX if _refused_outright(status) else STOP_UNKNOWN_PREFIX


def stop_session(target: str, reason: Optional[str], json_output: bool = False) -> Dict[str, Any]:
    """End a session now, and print what the Gateway says actually happened to it.

    THE CLIENT IS DUMB (Ruling 5). The Gateway folds the whole answer - the verdict, the one-line
    headline an operator reads, and any further lines the case needs - and this prints the headline
    and then each detail line, in the order they were given, and nothing else. It does not compose a
    sentence, it does not decide what a verdict means, and it does not re-word anything. A new state
    is then one edit on the Gateway rather than a new branch in three clients.

    EXIT ZERO FOR ALL FOUR VERDICTS (Ruling 3) - stopped, alreadyStopped, notOnFleet and
    stoppedNotDescribed. A stop never fails because there is nothing left to stop. The failure being
    designed out is a second run returning an error, which an operator reads as "it is still alive".

    This counts FOUR because nothing here counts at all: the exit code follows the 200, and the
    headline is printed as the Gateway wrote it, so a verdict this client has never heard of already
    works. The number is in this sentence only to keep it honest - it said THREE while the fold had
    four, which is the kind of stale comment that teaches the next reader to trust a client to know
    what a verdict means. It must not become a list anything branches on.

    Non-zero is for three things only, and each says which one it was: no reason was given (refused
    here, before the call); the Director could not be reached (the shared client writes that
    sentence, and it names the machine rather than the Gateway when the Gateway stamped the fault as
    a Director-side one); and the process would not die (the Gateway writes that sentence, and it is
    printed as written).

    AND IT NEVER TURNS A NON-ZERO EXIT INTO A VERDICT ABOUT THE SESSION. Only two of those three are
    known not to have stopped anything; a lost reply, a dropped tunnel or a Director that answered
    late may all have ended the session first, and the Gateway says as much in words. So a refused
    request is announced as "Not stopped:" and everything else as "Outcome unknown:" - see
    _refused_outright for which is which, and why the unknown side is the default.

    Every sentence is printed with soft_wrap: Rich would otherwise break it at the console width,
    and a reader - an agent above all - searching the output for the Gateway's sentence would find
    it split across two lines.
    """
    if reason is None or not reason.strip():
        # Refused before the round trip - there is no point asking the Gateway to tell us what we
        # already know. The Gateway refuses a missing reason too, in its own words, and that refusal
        # is what the failure branch below prints; this sentence is ours because no call was made to
        # answer it.
        axi_cli.usage_error(
            f"a reason is required to stop a session, and none was given. {STOP_REASON_FLAG_HINT}"
        )

    sid = _stop_target(target)
    try:
        # path_segment, because the target reaching here can be a NAME the roster did not match -
        # see _stop_target - and this fleet names its sessions "<Mission> - <Role> - <what it does>".
        # A slash in that name interpolated raw makes "sessions/Mission / Worker/stop", which is not
        # the stop route, and the second stop of such a session came back 404 instead of the answer
        # Ruling 3 exists to give it.
        resp = gateway.post_json(
            f"sessions/{gateway.path_segment(sid)}/stop", {"reason": reason.strip()}
        )
    except gateway.GatewayError as err:
        # The server's sentence survives INTACT. gateway._error_message has already lifted it out of
        # the body, so re-wording it here would throw away the one sentence written for this failure -
        # and for a Director-side fault it is the sentence that names the machine rather than the
        # Gateway. It goes to standard error as plain text, never through Rich markup: it is text from
        # somewhere else, and a token shaped like [/tmp/x] raises MarkupError.
        text = str(err)
        if not _refused_outright(err.status):
            # Said ONCE, after the server's own words, and only where the outcome is genuinely
            # unknown. Without it the reader is left with a sentence about a lost reply and no idea
            # what to do next.
            text = f"{text} This cannot say whether the session is still running."
            next_steps = ["cc-devthrottle session list"]
        elif "reason" in text.lower():
            # The one thing only this command knows. Added AFTER the server's words, never instead of
            # them. Still tested textually rather than on the status alone: the sentence is what names
            # the reason as the missing thing, and a refusal that is about something else must not send
            # the caller off to fix a flag that was never wrong.
            text = f"{text} {STOP_REASON_FLAG_HINT}"
            next_steps = ['cc-devthrottle session stop <session-id> --reason "<why you are stopping it>"']
        else:
            text = f"{text} Nothing was stopped."
            next_steps = _CHECK_SESSION
        axi_cli.fail(text, next_steps, label=_failure_prefix(err.status))

    body = resp if isinstance(resp, dict) else {}
    headline = gateway.field(body, "headline", "Headline")
    if not headline:
        # A BROKEN INSTRUMENT, NOT A FOURTH VERDICT. Every answer this route gives carries a headline;
        # one that does not is a Gateway that did not understand the request, and printing nothing
        # while exiting 0 would be the "button that accepts a click and says nothing" this mission
        # exists to remove. Said as ignorance rather than as an outcome: we do not know whether it
        # stopped, and neither does anybody reading this.
        axi_cli.fail(
            f"the Gateway returned nothing that says what happened to {sid}, so this cannot report "
            "whether it was stopped.",
            ["cc-devthrottle session list"],
            label="No answer:",
        )

    if json_output:
        # Plain print, not console.print: Rich wraps to 80 columns when stdout is not a TTY and injects
        # newlines into long values, producing invalid JSON. The same reason `session list --json` does it.
        # This is the shape an AGENT reads, and Ruling 4 makes an agent the ordinary caller of a stop - so
        # the alternative was every agent parsing sentences that re-wrap with the console width.
        # The WHOLE answer is printed, verbatim, exactly as the Gateway folded it: this client no more
        # edits the JSON than it edits the sentences.
        print(json.dumps(body, indent=2))
        return body

    console.print(axi_cli.shown(headline), soft_wrap=True)
    details = body.get("details", body.get("Details"))
    if isinstance(details, list):
        # In the order the Gateway gave them. The order is part of the answer - the worktree line
        # before the reason line - and sorting or filtering here would be this client deciding what
        # matters, which is the one thing it must never do.
        for line in details:
            if isinstance(line, str) and line.strip():
                console.print(axi_cli.shown(line), soft_wrap=True)
    axi_cli.print_next([
        "cc-devthrottle session list",
        "cc-devthrottle session spawn <repo> --controlled-by self",
    ])
    return body


def _report_delivery(resp: Any, who: str) -> None:
    """Report a delivery from either of the Gateway's two answer shapes.

    A message to ONE session answers with the prompt result - accepted plus an error - and a broadcast
    answers with the fan-out: a per-recipient result row each, a refusal, or a note that there was
    nobody to send to. Both are read here rather than at the two call sites so the sentence the user
    reads cannot drift between "message send" and "message send all".

    The counting is the part worth being careful about. A fan-out row with no error was delivered; a
    row with one was not, and counting rows rather than successes would report a storm of failures as
    a successful broadcast. A refusal is an error even though it arrives with a 200 - the Hub answers
    scope refusals in the body, not the status code.
    """
    accepted = False
    count = 0
    err: Optional[str] = None
    warning: Optional[str] = None
    if isinstance(resp, dict):
        warning = resp.get("warning") or resp.get("Warning")
        results = resp.get("results", resp.get("Results"))
        if bool(resp.get("denied", resp.get("Denied", False))):
            err = resp.get("deniedReason") or resp.get("DeniedReason") or "the broadcast was refused"
        elif isinstance(results, list):
            count = sum(1 for r in results if isinstance(r, dict) and not (r.get("error") or r.get("Error")))
            failed = [r for r in results if isinstance(r, dict) and (r.get("error") or r.get("Error"))]
            accepted = True
            if failed and not warning:
                warning = (f"{len(failed)} of {len(results)} recipients did not receive it: "
                           + "; ".join(str(r.get("error") or r.get("Error")) for r in failed[:3]))
        else:
            accepted = bool(resp.get("accepted", resp.get("Accepted", False)))
            count = 1 if accepted else 0
            err = resp.get("error") or resp.get("Error")
    if accepted:
        console.print(f"[green]Delivered[/green] to {axi_cli.shown(who)} ({count} session(s)).")
        if warning:
            console.print(f"[yellow]Note:[/yellow] {axi_cli.shown(warning)}")
    else:
        # The Gateway said nothing about why: say THAT, rather than a sentence that reads like a reason.
        axi_cli.fail(
            str(err) if err else "the Gateway did not accept the message and gave no reason.",
            ["cc-devthrottle session list"],
            label="Not delivered:",
        )


def send_message(
    target: str,
    message: str,
    everyone: bool = False,
    reason: str | None = None,
    grant: str | None = None,
) -> None:
    """Send a message to one session, or broadcast with target 'all'.

    A plain 'all' reaches only the sender's team (its Mission, or - solo - the same repository on the
    same machine). --everyone asks to reach the whole fleet, which the Gateway Hub gates on a human
    grant plus a reason (issue #1229)."""
    is_broadcast = target.strip().lower() == "all"
    # Refused before anything is sent: each of these flags only means something on a fleet-wide
    # broadcast, and a flag that is silently dropped is a defect (docs/axi-standard.md).
    if everyone and not is_broadcast:
        axi_cli.usage_error(
            f"--everyone broadcasts to the whole fleet, so it needs the target 'all', not '{target}'. "
            'Use cc-devthrottle message send all "<message>" --everyone --reason "<why>" --grant <grant-id>, '
            "or drop --everyone to message one session.",
        )
    for flag, value in (("--reason", reason), ("--grant", grant)):
        if value is not None and not everyone:
            axi_cli.usage_error(
                f"{flag} only applies to a fleet-wide broadcast (--everyone), so it would be ignored here. "
                f"Drop {flag}, or add --everyone with target 'all'.",
            )
    if not message.strip():
        axi_cli.usage_error(
            "the message is blank. "
            'Pass the text: cc-devthrottle message send <session-id> "<message>"',
        )

    if is_broadcast:
        # No sender field: the Gateway takes it from the session key that authenticated the call, so
        # the team it resolves and the message it frames are about the same session by construction.
        body = {"text": message}
        if everyone:
            body["everyone"] = True
            if reason:
                body["reason"] = reason
            if grant:
                body["grantId"] = grant
        who = "the whole fleet" if everyone else "your team"
        try:
            resp = gateway.post_json("fleet/broadcast", body)
        except gateway.GatewayError as err:
            axi_cli.fail(
                f"could not broadcast to {who}: {err}",
                ["cc-devthrottle session list", axi_cli.CHECK_GATEWAY],
            )
        _report_delivery(resp, who)
        axi_cli.print_next(["cc-devthrottle session list", 'cc-devthrottle message send <session-id> "<message>"'])
        return

    chosen = _resolve_target(target, command_name="cc-devthrottle message send")
    target_sid = gateway.field(chosen, "sessionId", "SessionId")
    try:
        resp = gateway.post_json(f"sessions/{target_sid}/message", {"text": message})
    except gateway.GatewayError as err:
        axi_cli.fail(f"could not send the message to session {target_sid}: {err}", _CHECK_SESSION)

    name = gateway.field(chosen, "name", "Name") or gateway.short_id(target_sid)
    _report_delivery(resp, f'{name} ({target_sid})')
    axi_cli.print_next([
        f"cc-devthrottle session buffer {axi_cli.bare(target_sid, '<session-id>')}",
        f'cc-devthrottle message ask {axi_cli.bare(target_sid, "<session-id>")} "<question>"',
    ])


def ask_session(target: str, question: str, timeout_ms: int) -> None:
    """Ask one session a question and print its answer."""
    if target.strip().lower() == "all":
        axi_cli.usage_error(
            "message ask targets a single session, not 'all'. "
            'Use cc-devthrottle message send all "<message>" for a broadcast, or name one session.',
        )
    if not question.strip():
        axi_cli.usage_error(
            "the question is blank. "
            'Pass the question: cc-devthrottle message ask <session-id> "<question>"',
        )
    if timeout_ms < 1:
        axi_cli.usage_error(
            f"--timeout-ms must be at least 1, not {timeout_ms}. "
            "Pass how long to wait in milliseconds, for example --timeout-ms 120000.",
        )

    chosen = _resolve_target(target, command_name="cc-devthrottle message ask")
    target_sid = gateway.field(chosen, "sessionId", "SessionId")

    http_timeout = max(30.0, timeout_ms / 1000.0 + 15.0)
    try:
        # An ask is a message that WAITS. waitForIdle also drops the reply hint from the frame: the
        # asker is already holding the line and reads the answer from the target's own output, so a
        # "reply with this command" line would make the recipient answer into a channel nobody reads.
        resp = gateway.post_json(
            f"sessions/{target_sid}/message",
            {"text": question, "waitForIdle": True, "timeoutMs": timeout_ms},
            timeout=http_timeout,
        )
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"no answer from session {target_sid}: {err}",
            [f"cc-devthrottle session buffer {axi_cli.bare(target_sid, '<session-id>')}"],
        )

    # The question was delivered only if the answer says accepted: true, and the wait happened only if
    # it names how the wait ended. Without both, "(the target produced no output)" would be a guess.
    ask_next = [f"cc-devthrottle session buffer {axi_cli.bare(target_sid, '<session-id>')}"]
    _accepted_or_fail(resp, f"the question to session {target_sid}", ask_next)
    axi_cli.confirmed(resp, ("waitStatus", "WaitStatus"), f"the question to session {target_sid}", ask_next)
    answer = gateway.field(resp, "output", "Output").strip()
    name = gateway.field(chosen, "name", "Name") or gateway.short_id(target_sid)
    console.print(f"[dim]-- answer from {axi_cli.shown(name)} ({target_sid}) --[/dim]")
    # The answer is another session's own words: printed as text, never read as markup.
    print(axi_cli.ascii_text(answer) if answer else "(the target produced no output)")
    axi_cli.print_next([
        f"cc-devthrottle session buffer {axi_cli.bare(target_sid, '<session-id>')}",
        f'cc-devthrottle message send {axi_cli.bare(target_sid, "<session-id>")} "<message>"',
    ])


def _controller_mission(controller_session_id: str) -> Optional[Dict[str, Any]]:
    """The controlling session's roster row, when it exists AND carries a mission; else None.

    Returns the whole row rather than the id so the caller can name the session the mission came
    from. Missing controller, or a controller attached to nothing, is a plain None - those are
    ordinary and there is nothing to report.

    A roster this process cannot READ is different, and is reported rather than swallowed: the
    spawn proceeds unattached (refusing to open a session because an optional grouping could not be
    looked up would be worse than the ungrouped session), but the human is told, in that order, so
    the missing mission is never a mystery. One 'mission attach' fixes it afterwards - which is the
    whole point of this issue existing.
    """
    try:
        sessions, _, _, _ = gateway.get_fleet()
    except gateway.GatewayError as err:
        axi_cli.warn(
            "could not read the fleet list to inherit the controlling "
            f"session's mission, so the new session starts attached to no mission: {err} "
            "Attach it afterwards with: cc-devthrottle mission attach <session-id> <mission-id>"
        )
        return None

    wanted = controller_session_id.strip().lower()
    for s in sessions:
        if gateway.field(s, "sessionId", "SessionId").lower() != wanted:
            continue
        return s if gateway.field(s, "missionId", "MissionId") else None
    return None


def spawn_session(
    repo: str,
    agent: str,
    prompt: Optional[str],
    name: Optional[str],
    purpose: Optional[str],
    command: Optional[str],
    command_args: Optional[str],
    controlled_by: Optional[str] = None,
    args: Optional[str] = None,
    standalone: bool = False,
    why: Optional[str] = None,
    role: Optional[str] = None,
    machine: Optional[str] = None,
    mission: Optional[str] = None,
    workflow_run: Optional[str] = None,
    # NOT named `director`: this module's Director-client is imported under that name and a parameter
    # would shadow it, breaking every gateway.post_json call in here.
    director_target: Optional[str] = None,
) -> None:
    """Open a new session here, on another computer (--machine), or on one named Director (--director)."""
    # WHO OWNS THE NEW SESSION MUST BE STATED, NOT INFERRED (owner's ruling, 2026-09-13): "I think it is
    # clear if you have to state ownership intent when you start a session."
    #
    # Every session has exactly ONE owner, and it is either another SESSION or the USER. There is no third
    # answer and there is no unowned session: no owner means the user. That is the whole model, and the
    # attention rule downstream is nothing more than reading it - a session whose owner is a live session
    # goes quiet and reports there; a session whose owner is the user goes red and asks him.
    #
    # THIS USED TO BE A SILENT DEFAULT, AND THAT IS WHAT CHANGED. The line was:
    #
    #     elif cc_session: controller_session_id = cc_session
    #
    # so a session that spawned without saying anything took ownership of what it spawned, because an
    # environment variable happened to be set. Nobody chose it and nothing recorded that a choice had been
    # made - and the consequence is the one that matters most, because it is the only way work leaves the
    # user's queue. A person spawning and a schedule firing both land on him already; only an agent
    # spawning an agent can quietly make work answer to a machine. So that is the case that must declare
    # itself, and it is the only one.
    #
    # A HUMAN SPAWN DECLARES NOTHING, and that is not an exemption. There is nothing to state: a session a
    # person opens from the desktop, the Cockpit or the phone is the user's, and asking him to say so would
    # be ceremony rather than clarity. The requirement lands exactly where the ambiguity is.
    #
    # (The handover / move-session flow does NOT come through here - it uses POST /handover, which never
    # sets a controller - so a moved session keeps its red visible to the user, by construction.)
    controller_session_id: Optional[str] = None
    cc_session = os.environ.get("CC_SESSION_ID")
    opt_out = standalone or (controlled_by is not None and controlled_by.strip().lower() == "none")

    if cc_session and not opt_out and not controlled_by:
        axi_cli.usage_error(
            "this spawn has to say who will OWN the new session: you are spawning from inside a session, "
            "so there are two possible owners and no safe default between them. Pass --controlled-by self "
            "if YOU own it (it stays quiet and reports back to you), --standalone if the USER owns it (it "
            "goes red and asks him when it finishes), or --controlled-by <session-id> if another session owns it."
        )

    # HANDING WORK TO THE USER IS A DELIBERATE ACT, SO IT STATES A REASON (owner's ruling, 2026-09-14).
    # --standalone and --controlled-by self cost the same to type, and on 14 September three of thirteen
    # agent-started sessions had chosen --standalone - two of them then sat red at the owner. Requiring a
    # reason does not forbid the choice; it makes an agent that cannot justify it collect its own work.
    #
    # THE REASON IS NOT YET CARRIED ON THE WIRE, and saying so is better than implying otherwise: there is
    # no field for it on the create, so it is printed here and lands in the SPAWNING session's transcript,
    # which is searchable and durable. A field on the session is the right home and is not built.
    if cc_session and opt_out and not (why or "").strip():
        axi_cli.usage_error(
            "--standalone gives this session to the USER, so it goes red and asks him when it finishes. "
            'Say why with --why "<why it is his>", for example --why "he asked me to open this for him". '
            "If you cannot say why it is his, it is probably yours: pass --controlled-by self."
        )

    if opt_out:
        controller_session_id = None
    elif controlled_by:
        if controlled_by.strip().lower() == "self":
            controller_session_id = cc_session
            if not controller_session_id:
                axi_cli.usage_error(
                    "--controlled-by self requires CC_SESSION_ID to be set, but it is not. "
                    "Run this from inside a session, or pass an explicit controlling session id: "
                    "--controlled-by <session-id>",
                )
        else:
            controller_session_id = controlled_by
    # Issue #800: always name your session. On this fleet many sessions run in the same
    # checkout, so a session with neither a name nor a purpose still gets an auto-composed
    # name from the Director, but it reads better when you describe what it is FOR.
    if not name and not purpose:
        axi_cli.warn(
            "no --name or --purpose given; the session will get an "
            "auto-composed name. Pass --purpose \"<what it is for>\" so it is easy to tell apart."
        )

    body: Dict[str, Any] = {"repoPath": repo, "agent": agent}
    # Issue #1017: with no --args, the Director applies the SAME default agent settings (permission
    # mode preset, default model) the desktop New Session dialog uses, so a spawned session is
    # usable for unattended work without hand-fixing permissions. Passing --args overrides that
    # default with an explicit command line for this session only.
    if args is not None:
        body["args"] = args
    if name:
        body["name"] = name
    if purpose:
        body["purpose"] = purpose
    if prompt:
        body["prePrompt"] = prompt
    if command:
        body["command"] = command
    if command_args:
        body["commandArgs"] = command_args
    if controller_session_id:
        body["controllerSessionId"] = controller_session_id
    elif cc_session:
        # THE USER OWNS IT, SAID OUT LOUD (issue #2838). Omitting the field used to mean two different
        # things - "the user's" and "nobody said" - and the Gateway cannot tell those apart, so it now
        # refuses an agent-initiated spawn that sends nothing. A deliberate --standalone therefore has to
        # state itself on the wire rather than be inferred from an absence.
        body["controllerSessionId"] = "none"
    # Session origin and lineage (devthrottle_internal issue #982). This process is the only place
    # that can tell a session-initiated spawn from a human one: CC_SESSION_ID is injected into a
    # session's environment at birth and is absent from a human's own shell, so its presence IS the
    # answer. Stated here rather than inferred at the Director, which sees an identical HTTP request
    # either way and would have to guess.
    #
    # NOT the same as controllerSessionId above, and deliberately sent separately. That one asks for a
    # live supervision relationship and is dropped by --standalone; this one records who made the call
    # and survives it. A session spawning a deliberate human-facing peer is exactly the case where the
    # two must differ - it is still an agent starting a session, which is the thing being counted.
    if cc_session:
        body["origin"] = "agent"
        body["parentSessionId"] = cc_session
    else:
        body["origin"] = "human"
    body["originSurface"] = "cli"
    # Automatic session roles: forward an explicit --role VERBATIM to the Director, which validates it
    # against Standalone/Manager/Worker/Architect and rejects an unknown value (never a silent drop).
    if role:
        body["role"] = role
    # Mission attach at spawn: forward the Mission id ALONE. The GATEWAY resolves and validates it against
    # its own store - the only one that holds missions - and sends the Director the resolved name alongside
    # the id; an unknown mission is a 400 from the Gateway. This tool deliberately does not look a mission
    # up itself: a second copy of that rule here would be a second thing to get wrong (issue #2629, where
    # one of the two Gateway spawn routes forwarded the id without the name and the Director, asked to
    # resolve something it does not hold, called a live mission unknown).
    #
    # INHERITANCE (issue #2387). With no --mission, a session that has a CONTROLLER inherits that
    # controller's mission. Default ON, because the fleet already records the relationship and the case
    # that found this gap - a release push that grew from one seat to about a dozen in a day - would have
    # been grouped for free: every one of those sessions was spawned by a seat that already belonged to
    # the mission. Making it opt-in would mean the grouping only ever happens when somebody remembers,
    # which is the same failure as attach-at-birth in a different coat.
    #
    # Three things keep it honest. An explicit --mission always WINS (it is stated intent, not a
    # default). --mission none is the OPT-OUT, spelled the same way --controlled-by none is, for the
    # deliberate case of a child that is not part of its controller's work. And it is never SILENT: the
    # inheritance is printed, naming the mission and the session it came from, so a wrong inheritance is
    # visible immediately and one 'mission detach' away.
    inherited_from: Optional[Dict[str, Any]] = None
    mission_opt_out = mission is not None and mission.strip().lower() == "none"
    if mission and not mission_opt_out:
        body["missionId"] = mission
    elif not mission_opt_out and controller_session_id:
        inherited_from = _controller_mission(controller_session_id)
        if inherited_from:
            body["missionId"] = gateway.field(inherited_from, "missionId", "MissionId")
    # Workflow seat at spawn (Workflows phase 5b): forward the run id; the Gateway validates it and
    # stamps the workflow id + pinned version, and the seated session's preamble tells the agent to
    # fetch its conduct at exactly that version. A mission spawn auto-seats without this flag.
    if workflow_run:
        body["workflowRunId"] = workflow_run

    # "Start a session on some computer." Every spawn - including one on this very machine - goes to the
    # Gateway's POST /machines/{machine}/sessions, which picks a Director on that machine (auto-launching
    # one if none is running) and creates the session there. An off or unreachable machine fails loudly,
    # with NO local path to fall back to: that is the whole point of the Remove-the-network-port mission,
    # and a spawn that quietly landed somewhere else would be the second door in its worst form.
    #
    # THE MACHINE MUST NOW BE NAMED, where the Director floor could leave it blank and mean "here". With
    # the Director out of the path there is no "here" to infer, so an unqualified spawn resolves THIS
    # session's own machine from the roster - the Gateway's own view of where this session runs, not a
    # hostname read off the operating system, which is a different string on a different day.
    #
    # --director names ONE Director instead of "some Director on that computer", the only way to be
    # specific on a machine running several named instances. Passed through verbatim - resolving a
    # Director name here would mean this tool holding a second copy of a rule the Gateway already applies.
    target_machine = machine.strip() if machine else ""
    target_director = director_target.strip() if director_target else ""

    # Resolved INSIDE the error handling: both lookups raise GatewayError with the sentence written for
    # the case, and outside it that sentence reached the caller as a traceback.
    try:
        if target_director:
            # ONE named Director. The Gateway's machine route picks "some Director on that computer" and
            # has no way to be told which, so naming one has to be addressed to it BY ID - which is what
            # /directors/{id}/sessions is. Resolving the typed name against the Director list is the same
            # class of lookup `session list` already does for a session id or name; what a name MATCHES is
            # a client's job, what may be DONE with the result is the Gateway's.
            path = f"directors/{gateway.path_segment(_resolve_director_id(target_director, target_machine))}/sessions"
        elif target_machine:
            # "Some Director on that computer", launching one if none is running.
            path = f"machines/{gateway.path_segment(target_machine)}/sessions"
        else:
            # HERE. This session's own Director, named from what the session was told at launch - no
            # roster lookup, no hostname read off the operating system, and no round trip to work out
            # something the session already knows.
            path = f"directors/{gateway.path_segment(_my_director())}/sessions"
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"no session was opened: {err}",
            ["cc-devthrottle director list"],
        )

    try:
        resp = gateway.post_json(path, body)
    except gateway.GatewayError as err:
        where = target_director or target_machine or "this session's own Director"
        axi_cli.fail(
            f"no session was opened on {where}: {err}",
            ["cc-devthrottle director list"],
        )

    sid = gateway.field(resp, "sessionId", "SessionId") if isinstance(resp, dict) else ""
    if not sid:
        axi_cli.fail(
            "the Gateway did not return a session id, so whether a session was opened is unknown. Look for it before spawning again.",
            ["cc-devthrottle session list"],
        )

    short = gateway.short_id(sid)
    # The Director names the session at birth (issue #800), so the response carries the final name.
    # Only the answer names the session: the Director composes the name from the folder, --name and
    # --purpose, so the name asked for is not what it is called and is never printed in its place.
    label = gateway.field(resp, "name", "Name") or short
    console.print(f"[green]Opened[/green] session {short} ({axi_cli.shown(label)}).")
    if opt_out and cc_session:
        console.print(
            f"[yellow]The USER owns it[/yellow] - it will go red and ask him. Reason given: {axi_cli.shown(why.strip())}"
        )
    console.print(f"id: {sid}")
    if inherited_from is not None:
        # Never silent. An inherited mission the caller did not ask for is only safe if they can see
        # it happened, so name the mission AND the session it came from, and say how to undo it.
        mission_label = (
            gateway.field(inherited_from, "missionName", "MissionName")
            or gateway.short_id(gateway.field(inherited_from, "missionId", "MissionId"))
        )
        controller_label = (
            gateway.field(inherited_from, "name", "Name")
            or gateway.short_id(gateway.field(inherited_from, "sessionId", "SessionId"))
        )
        console.print(
            f"Attached to mission [bold]{axi_cli.shown(mission_label)}[/bold], inherited from its controlling "
            f"session {axi_cli.shown(controller_label)}. Undo with: cc-devthrottle mission detach {sid}"
        )
    steps = [
        f'cc-devthrottle message send {sid} "<message>"',
        f"cc-devthrottle session buffer {sid}",
    ]
    if inherited_from is not None:
        steps.append(f"cc-devthrottle mission detach {sid}")
    axi_cli.print_next(steps)


def _my_director() -> str:
    """The Director THIS session belongs to, from what the session was told at launch.

    This is how "here" is named once the loopback port is gone. Before the Remove-the-network-port
    mission it needed no name at all: the command line called its own Director directly, so "here" was
    wherever the call landed. Going through the Gateway makes it something that has to be said, and
    the session is told it at launch - so it costs nothing to ask and cannot disagree with itself.
    """
    import os
    director_id = (os.environ.get("CC_DIRECTOR_ID") or "").strip()
    if not director_id:
        raise gateway.GatewayError(
            "CC_DIRECTOR_ID is not set, so this process cannot say where 'here' is. "
            "Name the machine explicitly with --machine, or run this inside a DevThrottle session."
        )
    return director_id


def _resolve_director_id(name: str, machine: str) -> str:
    """Resolve a user-typed Director name to its id, optionally narrowed to one machine.

    Matched the way every other target in this tool is: an exact id wins outright, then an exact
    (case-insensitive) display name, then an id prefix. An ambiguous name is refused rather than
    guessed - picking one of two Directors called the same thing is how a session lands on the wrong
    computer and nobody notices until they go looking for it.
    """
    try:
        rows = gateway.get_json("directors")
    except gateway.GatewayError as err:
        raise gateway.GatewayError(f"Cannot list this account's Directors to resolve '{name}': {err}") from err
    # Absent is not empty: an answer that is not a list would otherwise read as "no Director matches".
    if not isinstance(rows, list) or not all(isinstance(d, dict) for d in rows):
        raise gateway.GatewayError(
            f"Cannot resolve '{name}': the Gateway's Director list answer was not a list of Directors."
        )

    wanted = name.strip().lower()
    if machine:
        rows = [d for d in rows
                if gateway.field(d, "machineName", "MachineName").lower() == machine.strip().lower()]

    exact = [d for d in rows if gateway.field(d, "directorId", "DirectorId").lower() == wanted]
    if not exact:
        exact = [d for d in rows if gateway.field(d, "displayName", "DisplayName").lower() == wanted]
    if not exact:
        exact = [d for d in rows if gateway.field(d, "directorId", "DirectorId").lower().startswith(wanted)]

    if not exact:
        where = f" on machine '{machine}'" if machine else ""
        raise gateway.GatewayError(
            f"No Director matches '{name}'{where}. Run cc-devthrottle director list to see them."
        )
    if len(exact) > 1:
        raise gateway.GatewayError(
            f"'{name}' matches {len(exact)} Directors: "
            + ", ".join(gateway.field(d, "directorId", "DirectorId") for d in exact)
            + ". Pass one of these ids to --director, or add --machine."
        )
    return gateway.field(exact[0], "directorId", "DirectorId")



def _spawn_selftest(repo: str, command_args: str, name: str) -> str:
    body: Dict[str, Any] = {
        "repoPath": repo, "agent": "RawCli", "command": "cmd", "commandArgs": command_args,
        # Named at birth, not renamed afterwards: a rename that failed used to be swallowed, leaving a
        # throwaway nobody could tell apart from real work.
        "name": name,
    }
    # The throwaways are this session's own work, declared as such: the Gateway refuses a spawn from
    # inside a session that does not say who owns the result (issue #2838).
    me = gateway.session_id()
    if me:
        body["controllerSessionId"] = me
    resp = gateway.post_json(f"directors/{gateway.path_segment(_my_director())}/sessions", body)
    sid = gateway.field(resp, "sessionId", "SessionId") if isinstance(resp, dict) else ""
    if not sid:
        raise gateway.GatewayError("the Gateway did not return a session id when spawning.")
    return sid


def _fleet_ids() -> List[str]:
    # Goes through the one shared fetch so the selftest reads the same roster every verb does. The
    # sessions it checks for are the ones it just spawned on THIS Director, which always reports its
    # own (issue #1019), so completeness cannot hide them - but reading a different route than the
    # rest of the tool is how a selftest ends up passing on a roster nobody else sees.
    sessions, _, _, _ = gateway.get_fleet()
    return [gateway.field(s, "sessionId", "SessionId") for s in sessions]


def _runs_on_windows() -> bool:
    return sys.platform.startswith("win")


#: When a flagged throwaway actually leaves the roster, read from SessionManager.ReapPendingDeletions
#: on the Director: a sweep (every 30 seconds) removes a flagged session only once its 30-second grace
#: period has passed AND it is not working. A session that stays working is skipped on every sweep, so
#: no deadline is promised. Printed, never waited for.
SELFTEST_REMOVAL_NOTE = (
    "the Director removes them after its 30-second grace period, on a later reaper sweep "
    "once they are no longer working"
)


def selftest(timeout_ms: int) -> None:
    """Run the fleet messaging self-test against the local Director."""
    # The responder sessions are Windows command prompts (cmd /k prompt ...), and they start on THIS
    # session's own Director, which runs on this machine. Anywhere else they cannot start, so say so
    # before anything is spawned rather than report a failure that reads like a messaging fault.
    if not _runs_on_windows():
        axi_cli.fail(
            f"selftest drives Windows command prompt sessions, so it runs only on Windows (this is {sys.platform}).",
            ["cc-devthrottle session list", axi_cli.CHECK_GATEWAY],
        )
    if timeout_ms < 1:
        axi_cli.usage_error(
            f"--timeout-ms must be at least 1, not {timeout_ms}. "
            "Pass how long the ask step waits in milliseconds, for example --timeout-ms 25000.",
        )
    repo = tempfile.gettempdir()
    results: List[Tuple[str, bool, str]] = []
    responder: Optional[str] = None
    recipient: Optional[str] = None

    def record(step: str, ok: bool, detail: str = "") -> None:
        results.append((step, ok, detail))
        mark = "[green]PASS[/green]" if ok else "[red]FAIL[/red]"
        console.print(f"  {mark}  {axi_cli.shown(step)}{('  - ' + axi_cli.shown(detail)) if detail else ''}")

    try:
        responder = _spawn_selftest(repo, f"/k prompt {SELFTEST_MARKER}$G", "selftest-responder")
        recipient = _spawn_selftest(repo, "/k", "selftest-recipient")
        record(
            "spawn two sessions",
            True,
            f"responder={gateway.short_id(responder)} recipient={gateway.short_id(recipient)}",
        )
        time.sleep(2)

        ids = _fleet_ids()
        listed = responder in ids and recipient in ids
        record("session list includes both", listed)

        # The self-test's messages are sent AS THIS SESSION, not as the throwaway it spawned: the
        # Gateway takes the sender from the key that authenticated the call, and this process holds
        # its own session's key, not the throwaways'. What is under test is that a message reaches a
        # session and that an ask comes back with its answer, and both still are.
        send = gateway.post_json(
            f"sessions/{recipient}/message", {"text": "fleet self-test message"},
        )
        accepted = bool(isinstance(send, dict) and send.get("accepted", send.get("Accepted", False)))
        record("message send delivers", accepted, str(gateway.field(send, "error", "Error") or ""))

        ask = gateway.post_json(
            f"sessions/{responder}/message",
            {"text": "selftest ping", "waitForIdle": True, "timeoutMs": timeout_ms},
            timeout=timeout_ms / 1000.0 + 15.0,
        )
        answer = gateway.field(ask, "output", "Output") if isinstance(ask, dict) else ""
        got_marker = SELFTEST_MARKER in answer
        record(
            "message ask returns the answer",
            got_marker,
            "marker found" if got_marker else f"status={gateway.field(ask, 'waitStatus', 'WaitStatus')}",
        )

    except gateway.GatewayError as err:
        record("fleet messaging reachable", False, str(err))
    finally:
        # request-deletion, not a hard DELETE: that is the verb an agent credential may call, and it is
        # what `session done` uses. What this can check at once is that each flag was ACCEPTED - the
        # Director answers pendingDeletion: true. Removal from the roster is not immediate and is not
        # checked here: the Director keeps a flagged session for its grace period and removes it on its
        # next reaper sweep (SessionManager.DeletionGraceMs and DeletionReaperIntervalMs, 30 seconds
        # each), so a roster read a second later still lists both, correctly.
        flagged = 0
        wanted = 0
        for sid in (responder, recipient):
            if not sid:
                continue
            wanted += 1
            try:
                resp = gateway.post_json(f"sessions/{sid}/request-deletion", {})
            except gateway.GatewayError as err:
                record(f"flag throwaway {sid} for deletion", False, str(err))
                continue
            if isinstance(resp, dict) and resp.get("pendingDeletion", resp.get("PendingDeletion")) is True:
                flagged += 1
            else:
                record(f"flag throwaway {sid} for deletion", False, "the answer did not say pendingDeletion: true")
        if wanted:
            record(
                "throwaway sessions flagged for deletion",
                flagged == wanted,
                f"{flagged}/{wanted} accepted; {SELFTEST_REMOVAL_NOTE}",
            )

    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    if passed == total and total > 0:
        console.print(f"[green]PASS[/green] - fleet messaging self-test: {passed}/{total} checks passed.")
        axi_cli.print_next(["cc-devthrottle session list", 'cc-devthrottle message send <session-id> "<message>"'])
        raise typer.Exit(0)
    # Throwaways that may still be listed; removing one that is already gone is harmless.
    leftovers = [sid for sid in (responder, recipient) if sid]
    axi_cli.fail(
        f"fleet messaging self-test: {passed}/{total} checks passed. The failed checks are marked FAIL above.",
        [axi_cli.CHECK_GATEWAY, *(f"cc-devthrottle session done {axi_cli.bare(sid, '<session-id>')}" for sid in leftovers)],
        label="FAIL -",
    )
