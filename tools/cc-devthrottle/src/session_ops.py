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
from rich.markup import escape
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
from . import usage_errors  # noqa: E402

from .repo_ops import is_windows_path, matches_repo  # noqa: E402

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass

console = Console()
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
        # Plain and escaped: the sentence is the Gateway's, and Rich markup would read a token like
        # [/tmp/x] in it as a tag, while a control character in it would split the line.
        print(f"Error: {axi_output.escape_ascii(str(err))}", file=sys.stderr)
        raise typer.Exit(1)


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
        console.print(
            f"[red]No session matches '{_text(target)}'.[/red] "
            "Run cc-devthrottle session list to see the fleet.",
            soft_wrap=True,
        )
        caveat = _roster_caveat(complete, reason)
        if caveat:
            console.print(
                f"[yellow]The fleet list searched may be incomplete.[/yellow] {_text(caveat)}", soft_wrap=True
            )
        # THE negative answer the second caution exists for. A machine whose tunnel is up but whose
        # pushes are late can be hiding the very session being addressed, and every target-resolving
        # verb comes through here - message send, rename, done, hold, compact. Printed on
        # this path only, so it stays rare enough to be read.
        if stale_caution:
            console.print(f"[yellow]{_text(stale_caution)}[/yellow]", soft_wrap=True)
        raise typer.Exit(1)
    if len(matches) > 1:
        _refuse_ambiguous_target(target, matches, command_name=command_name)
    return matches[0]


def _text(value: Any) -> str:
    """Text from somewhere else, ready for console.print: escaped to ASCII, so a newline or a
    non-ASCII character in it cannot split its line or leave the line not ASCII, and for Rich, so a
    token like [bold] or [/tmp/x] is printed rather than read as markup. Printed with soft_wrap, one
    sentence is then exactly one line."""
    return escape(axi_output.escape_ascii(str(value)))


def _refuse_ambiguous_target(
    target: str, matches: List[Dict[str, Any]], *, command_name: str
) -> None:
    """Print the several sessions a target matched, and exit - it is always an error, never a verdict.

    Lifted out of _resolve_target so that `session stop`, which deliberately does NOT go through that
    resolver (see _stop_target), still refuses an ambiguous target in exactly these words. Two
    wordings for one refusal is how the tool comes to answer the same question two ways.
    """
    console.print(
        f"[yellow]'{_text(target)}' is ambiguous - {len(matches)} matches:[/yellow]", soft_wrap=True
    )
    for s in matches:
        sid = gateway.field(s, "sessionId", "SessionId")
        name = gateway.field(s, "name", "Name") or "(unnamed)"
        machine = gateway.field(s, "machineName", "MachineName") or "-"
        console.print(
            f"  {_text(gateway.short_id(sid))}  {_text(name)}  ({_text(machine)})", soft_wrap=True
        )
    console.print(f"Re-run {command_name} with a longer id prefix.", soft_wrap=True)
    raise typer.Exit(1)


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


def resolve_target_or_current(target: Optional[str]) -> str:
    """Return the requested session id, defaulting to this session."""
    if target is None or not target.strip():
        sid = gateway.session_id()
        if not sid:
            console.print(
                "[red]Error:[/red] no target was provided and CC_SESSION_ID is not set."
            )
            raise typer.Exit(1)
        return sid

    chosen = _resolve_target(target, command_name="cc-devthrottle session rename")
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
            print(
                f"Error: the Gateway returned a session with no session id (row {index + 1}, {shown}). "
                "This tool will not list a session it cannot name; --json shows the raw rows.",
                file=sys.stderr,
            )
            raise typer.Exit(1)


def _fold_or_exit(sessions: List[Dict[str, Any]]) -> List[str]:
    try:
        return [plain_state(s) for s in sessions]
    except SessionStateError as err:
        print(f"Error: {err}", file=sys.stderr)
        raise typer.Exit(1)


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
        _usage_error("--fields does not apply to --json, which always carries every field. Drop one of them.")
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


def _session_list_help(rows: List[Tuple[Dict[str, Any], str]], filtered: bool, chosen_fields: List[str]) -> List[str]:
    """Concrete next commands. Runtime values are placeholders, never guessed."""
    if not rows:
        if filtered:
            return ["cc-devthrottle session list", "cc-devthrottle session list --help"]
        return ["cc-devthrottle director list", "cc-devthrottle session spawn <repo> --controlled-by self"]
    commands = []
    if not filtered:
        commands.append("cc-devthrottle session list --state needs-you")
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
        print(f"Error: {axi_output.escape_ascii(str(err))}", file=sys.stderr)
        print(
            "Nothing about the fleet can be shown without the Gateway. "
            "Run cc-devthrottle setup status to check this machine, or cc-devthrottle --help for the commands.",
            file=sys.stderr,
        )
        raise typer.Exit(1)
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
        console.print(
            "[red]Error:[/red] CC_SESSION_ID is not set. "
            "cc-devthrottle session whoami only works inside a DevThrottle session."
        )
        raise typer.Exit(1)

    short = gateway.short_id(sid)
    # Completeness is deliberately ignored here: whoami looks up THIS session, which lives on the
    # Director being asked, and a Director always reports its own sessions (issue #1019). An
    # unreachable Director elsewhere cannot hide the caller from itself.
    sessions, _, _, _ = _get_fleet()
    me = next(
        (s for s in sessions if gateway.field(s, "sessionId", "SessionId").lower() == sid.lower()),
        None,
    )
    if me is None:
        console.print(f"You are session {short} (id {sid}).")
    else:
        name = gateway.field(me, "name", "Name") or "(unnamed)"
        machine = gateway.field(me, "machineName", "MachineName") or "this machine"
        repo = gateway.field(me, "repoPath", "RepoPath")
        number = gateway.field(me, "number", "Number")
        number_text = f"number {number}, " if number is not None else ""
        console.print(f'You are session {number_text}{short} ("{name}") on {machine}, repo {_repo_name(repo)}.')

    console.print('To message another session:  cc-devthrottle message send <id> "<message>"')
    console.print('To message everyone:         cc-devthrottle message send all "<message>"')
    console.print("To see all sessions:         cc-devthrottle session list")


def rename_session(target: Optional[str], new_name: str) -> Dict[str, Any]:
    """Rename a target session, defaulting to the current session."""
    name = new_name.strip()
    if not name:
        console.print("[red]Error:[/red] the new session name cannot be blank.")
        raise typer.Exit(1)

    sid = resolve_target_or_current(target)
    try:
        # The Gateway renames a session anywhere in the account and answers with the updated row.
        resp = gateway.patch_json(f"sessions/{sid}", {"name": name})
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if not isinstance(resp, dict):
        console.print("[red]Error:[/red] the Gateway did not return the renamed session.")
        raise typer.Exit(1)

    actual = gateway.field(resp, "name", "Name") or name
    actual_sid = gateway.field(resp, "sessionId", "SessionId") or sid
    console.print(f'[green]Renamed[/green] {gateway.short_id(actual_sid)} to "{actual}".')
    return resp


def prompt_session(target: str, text: str, no_submit: bool = False) -> Dict[str, Any]:
    """Send raw text into a session - what a human typing into it would produce.

    THE GATEWAY REFUSES THIS TO EVERY AGENT (the Message Load mission, ruling 17): only the owner
    types into a session, from his own screens. This command always runs with a session key, so it
    prints the Gateway's refusal, which names the queued message to send instead.
    """
    if not text.strip():
        console.print("[red]Error:[/red] the prompt text cannot be blank.")
        raise typer.Exit(1)
    sid = resolve_target_or_current(target)
    try:
        resp = gateway.post_json(
            f"sessions/{sid}/prompt", {"text": text, "appendEnter": not no_submit}
        )
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {escape(str(err))}")
        raise typer.Exit(1)
    console.print(f"[green]Sent[/green] prompt to {gateway.short_id(sid)}.")
    return resp if isinstance(resp, dict) else {}


def interrupt_session(target: Optional[str]) -> Dict[str, Any]:
    """Stop what a session is currently doing.

    THE GATEWAY REFUSES THIS TO EVERY AGENT (the Message Load mission, ruling 17), for the same reason
    as `session prompt`; the refusal is printed as the Gateway wrote it.
    """
    sid = resolve_target_or_current(target)
    try:
        resp = gateway.post_json(f"sessions/{sid}/interrupt")
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {escape(str(err))}")
        raise typer.Exit(1)
    console.print(f"[green]Interrupted[/green] {gateway.short_id(sid)}.")
    return resp if isinstance(resp, dict) else {}


def hold_session(target: Optional[str], release: bool = False, minutes: Optional[int] = None) -> Dict[str, Any]:
    """Park a session, or release it. Restores the old POST /sessions/{sid}/hold.

    A hold asked for while the session is still working is DEFERRED: it applies when the turn
    settles, and the response's pending flag says so. A held session that starts working again
    always takes itself off hold.
    """
    sid = resolve_target_or_current(target)
    body: Dict[str, Any] = {"onHold": not release}
    if minutes is not None:
        body["snoozeMinutes"] = minutes
    try:
        resp = gateway.post_json(f"sessions/{sid}/hold", body)
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    short = gateway.short_id(sid)
    if release:
        console.print(f"[green]Released[/green] {short} - no longer held.")
    elif isinstance(resp, dict) and gateway.field(resp, "pending", "Pending"):
        console.print(f"[green]Hold queued[/green] {short} is still working; it parks when it finishes.")
    else:
        for_text = f" for {minutes} minutes" if minutes else ""
        console.print(f"[green]Held[/green] {short}{for_text}.")
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
    sid = resolve_target_or_current(target)
    if clear:
        body: Dict[str, Any] = {"raised": False}
    else:
        text = (reason or "").strip()
        if not text:
            console.print(
                "[red]Error:[/red] say what you need. A raised hand with no words is a 'notice me' "
                "ping - your supervisor would have to open you to find out what for, which is the "
                "work this is meant to save."
            )
            raise typer.Exit(1)
        body = {"raised": True, "reason": text}

    try:
        resp = gateway.post_json(f"sessions/{sid}/needs-manager", body)
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    short = gateway.short_id(sid)
    if clear:
        console.print(f"[green]Hand down[/green] {short}.")
    else:
        console.print(
            f"[green]Hand up[/green] {short}. Your supervisor sees it on the roster while you keep "
            "working; it lowers itself when your turn ends."
        )
    return resp if isinstance(resp, dict) else {}


def report_to_parent(summary: Optional[str], target: Optional[str] = None) -> None:
    """Tell the session that owns you what you did, now that your turn has ended.

    THE LAST STEP OF DELEGATED WORK, NOT A NOTIFICATION. Your parent asked you to do something;
    getting back to them is part of doing it. This is the session doing that itself, in its own
    words - not the roster hoping somebody wanders past a grey row and wonders about it.

    IT IS QUEUED, NOT TYPED (the Message Load mission, 16 September 2026, reversing the owner's
    ruling of 13 September that a report interrupts). The report is written to the parent's inbox as
    a message of kind "report" and the parent reads it when it is free - nothing lands mid-turn. It is
    still not a pull-only flag: the record stays open until the parent reads it. A report is not held
    to the one-message-per-ten-minutes spacing, because every refused message is told to put what it
    wanted to say in the report.

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
    sid = resolve_target_or_current(target)

    text = (summary or "").strip()
    if not text:
        console.print(
            "[red]Error:[/red] say what you did. A report with no words is a 'notice me' ping - "
            "your parent would have to open you to find out what happened, which is the work this "
            "is meant to save. One or two sentences: what you did, and anything they must decide."
        )
        raise typer.Exit(1)

    sessions, complete, reason, _stale = _get_fleet()
    if not complete:
        console.print(
            f"[red]Error:[/red] the fleet roster could not be read in full{(' - ' + reason) if reason else ''}.\n"
            "Refusing to report, because a roster that is missing sessions cannot tell 'you have no "
            "parent' apart from 'your parent is one of the rows I could not see'. Fix the roster read "
            "and try again, or name the session yourself with cc-devthrottle message send."
        )
        raise typer.Exit(1)

    me = None
    for s in sessions:
        if gateway.field(s, "sessionId", "SessionId") == sid:
            me = s
            break
    if me is None:
        console.print(
            f"[red]Error:[/red] session {gateway.short_id(sid)} is not in the fleet roster, so who "
            "owns it cannot be answered. Refusing to guess."
        )
        raise typer.Exit(1)

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
        return

    try:
        resp = gateway.post_json(f"sessions/{parent_id}/message", {"text": text, "kind": "report"})
    except gateway.GatewayError as err:
        _say_gateway("[red]Not queued:[/red]", err)
        raise typer.Exit(1)

    parent_name = None
    for s in sessions:
        if gateway.field(s, "sessionId", "SessionId") == parent_id:
            parent_name = gateway.field(s, "name", "Name")
            break
    label = parent_name or gateway.short_id(parent_id)
    _report_queued(resp, f"{label} ({gateway.short_id(parent_id)})")


def list_my_workers(target: Optional[str] = None) -> None:
    """Show the sessions THIS session is driving, and which of them have their hand up.

    The manager's half of the supervised rule. Workers never reach the owner, so a manager is the
    only one who can see a blocked one - and the design says a manager learns by READING its workers,
    not by being messaged 'notice me'. This is that read, in one line instead of one session at a time.
    """
    me = resolve_target_or_current(target)
    try:
        rows = gateway.get_json("sessions")
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    sessions = rows.get("sessions") if isinstance(rows, dict) else rows
    if not isinstance(sessions, list):
        console.print("[red]Error:[/red] the Gateway did not return a session list.")
        raise typer.Exit(1)

    mine = [
        x for x in sessions
        if str(gateway.field(x, "controllerSessionId", "ControllerSessionId") or "").lower() == me.lower()
    ]
    if not mine:
        console.print("You are not driving any sessions.")
        return

    table = Table(title=f"Sessions driven by {gateway.short_id(me)}")
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
    sid = resolve_target_or_current(target)
    body: Dict[str, Any] = {}
    if continue_prompt:
        body["continuePrompt"] = continue_prompt
    try:
        # Outermost bound of three: this waits longer than the Gateway waits for the Director, which
        # waits longer than the Director waits for the tool. The innermost one fires first and says
        # what actually failed.
        resp = gateway.post_json(f"sessions/{sid}/compact-context", body, timeout=300)
    except gateway.GatewayError as err:
        _say_gateway("[red]Error:[/red]", err)
        raise typer.Exit(1)

    short = gateway.short_id(sid)
    body = resp if isinstance(resp, dict) else {}
    detail = gateway.field(body, "detail", "Detail")
    # Read the flag as a BOOLEAN, not through gateway.field: that helper stringifies, and str(False) is
    # "False" - a truthy string. Routed through it, a compaction nobody watched would be announced as
    # "Compacted", which is the one thing this line must never say without evidence.
    observed = bool(body.get("compactionObserved", body.get("CompactionObserved", False)))
    label = "[green]Compacted[/green]" if observed else "[yellow]Compaction submitted[/yellow]"
    console.print(f"{label} {short}. {escape(str(detail or ''))}")
    return resp if isinstance(resp, dict) else {}


def read_session_buffer(target: Optional[str]) -> None:
    """Print what a session's terminal is showing. Restores the old GET /sessions/{sid}/buffer."""
    sid = resolve_target_or_current(target)
    try:
        resp = gateway.get_json(f"sessions/{sid}/buffer")
    except gateway.GatewayError as err:
        # escape(): the error text comes from the server, so it is no more ours to trust than the buffer
        # itself - it can quote a path or a fragment of the session's own output. Interpolated raw, a
        # token like [/tmp/x] raises the very MarkupError this verb was crashing on, from the branch whose
        # job is to REPORT a failure. The "Error:" label is ours, so it keeps its markup.
        console.print(f"[red]Error:[/red] {escape(str(err))}")
        raise typer.Exit(1)

    # The buffer verb returns the terminal text under one of a couple of shapes depending on the
    # path it came back through; print whichever carries the text rather than guessing one.
    text = None
    if isinstance(resp, dict):
        text = gateway.field(resp, "text", "Text") or gateway.field(resp, "buffer", "Buffer")
    elif isinstance(resp, str):
        text = resp
    if text is None:
        console.print("[red]Error:[/red] the Gateway did not return the session's buffer.")
        raise typer.Exit(1)
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
    sid = resolve_target_or_current(target)
    wanted = (role or "").strip()
    try:
        resp = gateway.post_json(f"sessions/{sid}/role", {"role": wanted})
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if not isinstance(resp, dict):
        console.print("[red]Error:[/red] the Gateway did not return the session's role.")
        raise typer.Exit(1)

    actual_sid = gateway.field(resp, "sessionId", "SessionId") or sid
    explicit = gateway.field(resp, "explicitRole", "ExplicitRole")
    short = gateway.short_id(actual_sid)
    # Only the explicit role is reported: Worker/Manager derivation needs the fleet-wide spawn graph, which
    # lives in the Gateway, so the effective role is read from `session list`, not returned here.
    if explicit:
        console.print(f"[green]Role set[/green] {short} is now explicitly {explicit}.")
    else:
        console.print(f"[green]Role cleared[/green] {short} reverts to automatic role derivation.")
    return resp


def mark_done(target: Optional[str], reason: Optional[str]) -> Dict[str, Any]:
    """Flag a session for deletion, defaulting to the current session.

    The session is not killed synchronously - it is flagged, and the owning Director's
    deletion reaper removes it within about a minute, once a short grace has elapsed and the
    session is no longer working. This is how an unattended run tears ITSELF down when it has
    nothing left for the user, instead of lingering as a dead session in the fleet.
    """
    sid = resolve_target_or_current(target)
    body: Dict[str, Any] = {}
    if reason and reason.strip():
        body["reason"] = reason.strip()
    try:
        resp = gateway.post_json(f"sessions/{sid}/request-deletion", body)
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    console.print(
        f"[green]Marked[/green] {gateway.short_id(sid)} for deletion; "
        "the Director will reap it shortly."
    )
    return resp if isinstance(resp, dict) else {}


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
        console.print(
            "[red]Nothing was changed:[/red] --undo and --reason cannot be used together. "
            "--reason is shown while a session winds down, and --undo is what cancels that "
            "wind-down, so there is nothing left for the reason to be shown on. Re-run "
            "cc-devthrottle session done --undo on its own, or drop --undo to flag the session.",
            soft_wrap=True,
        )
        raise typer.Exit(1)

    sid = resolve_target_or_current(target)
    try:
        resp = gateway.delete(f"sessions/{sid}/request-deletion")
    except gateway.GatewayError as err:
        # escape(): the server's sentence can quote a path or a fragment of another session's output,
        # and a token shaped like [/tmp/x] raises MarkupError out of the branch whose only job is to
        # report a failure.
        console.print(f"[red]Error:[/red] {_text(err)}", soft_wrap=True)
        raise typer.Exit(1)

    console.print(
        f"[green]Cleared[/green] {gateway.short_id(sid)} is no longer marked for deletion.",
        soft_wrap=True,
    )
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
        console.print(
            f"[red]{STOP_REFUSED_PREFIX}[/red] a reason is required to stop a session, and none was "
            "given. " + STOP_REASON_FLAG_HINT,
            soft_wrap=True,
        )
        raise typer.Exit(1)

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
        # Gateway. escape() for the reason it is used everywhere else in this module: it is text from
        # somewhere else, and a token shaped like [/tmp/x] raises MarkupError.
        text = str(err)
        console.print(f"[red]{_failure_prefix(err.status)}[/red] {_text(text)}", soft_wrap=True)
        if not _refused_outright(err.status):
            # Said ONCE, after the server's own words, and only where the outcome is genuinely
            # unknown. Without it the reader is left with a sentence about a lost reply and no idea
            # what to do next.
            console.print(
                "This cannot say whether the session is still running. Run "
                "cc-devthrottle session list to see whether it is still there.",
                soft_wrap=True,
            )
        # The one thing only this command knows. Added AFTER the server's words, never instead of
        # them. Still tested textually rather than on the status alone: the sentence is what names
        # the reason as the missing thing, and a refusal that is about something else must not send
        # the caller off to fix a flag that was never wrong.
        if _refused_outright(err.status) and "reason" in text.lower():
            console.print(STOP_REASON_FLAG_HINT, soft_wrap=True)
        raise typer.Exit(1)

    body = resp if isinstance(resp, dict) else {}
    headline = gateway.field(body, "headline", "Headline")
    if not headline:
        # A BROKEN INSTRUMENT, NOT A FOURTH VERDICT. Every answer this route gives carries a headline;
        # one that does not is a Gateway that did not understand the request, and printing nothing
        # while exiting 0 would be the "button that accepts a click and says nothing" this mission
        # exists to remove. Said as ignorance rather than as an outcome: we do not know whether it
        # stopped, and neither does anybody reading this.
        console.print(
            "[red]No answer:[/red] the Gateway returned nothing that says what happened to "
            f"{gateway.short_id(sid)}, so this cannot report whether it was stopped. "
            "Run cc-devthrottle session list to see whether it is still there.",
            soft_wrap=True,
        )
        raise typer.Exit(1)

    if json_output:
        # Plain print, not console.print: Rich wraps to 80 columns when stdout is not a TTY and injects
        # newlines into long values, producing invalid JSON. The same reason `session list --json` does it.
        # This is the shape an AGENT reads, and Ruling 4 makes an agent the ordinary caller of a stop - so
        # the alternative was every agent parsing sentences that re-wrap with the console width.
        # The WHOLE answer is printed, verbatim, exactly as the Gateway folded it: this client no more
        # edits the JSON than it edits the sentences.
        print(json.dumps(body, indent=2))
        return body

    console.print(_text(headline), soft_wrap=True)
    details = body.get("details", body.get("Details"))
    if isinstance(details, list):
        # In the order the Gateway gave them. The order is part of the answer - the worktree line
        # before the reason line - and sorting or filtering here would be this client deciding what
        # matters, which is the one thing it must never do.
        for line in details:
            if isinstance(line, str) and line.strip():
                console.print(_text(line), soft_wrap=True)
    return body


def _say_gateway(label: str, sentence: Any) -> None:
    """Print a label this tool owns, then a sentence the Gateway wrote, exactly as the Gateway wrote it.

    The sentence is QUOTED TEXT - a refusal, a note, a warning - and is printed verbatim: escaped, so a
    bracket in it is not read as markup, and with highlighting off, so the console does not colour the
    numbers and paths inside it. With colour on, "the limit is 6" otherwise reaches the reader as
    "the limit is <colour>6<reset>", which is no longer the sentence the Gateway sent and no longer
    matches it. The label is ours, so it keeps its markup; an empty label prints the sentence alone.
    """
    text = escape(str(sentence))
    console.print(f"{label} {text}" if label else text, highlight=False)


def _report_queued(resp: Any, who: str) -> None:
    """Report what the Gateway did with ONE message: queued, dropped as a duplicate, or refused.

    QUEUED, NEVER DELIVERED. The message is a record in the recipient's inbox; nothing was typed into
    it, and it reads the message when it is next free. Saying "delivered" would claim the recipient
    has seen it, which nothing here knows.

    A refusal usually arrives as an HTTP error, which the caller turns into the same "Not queued"
    line; this also handles a refusal that arrives in a 200 body, so the sentence cannot drift.
    """
    status = ""
    if isinstance(resp, dict):
        status = str(resp.get("status", resp.get("Status", "")) or "")
    if status == "queued":
        mid = str(resp.get("messageId", resp.get("MessageId", "")) or "")
        console.print(
            f"[green]Queued[/green] for {escape(who)} (message {mid}). Nothing was typed into it; "
            "it reads the message from its inbox when it is free.",
            highlight=False,
        )
        return
    if status == "duplicate":
        note = resp.get("note") or resp.get("Note") or "an identical message is already waiting unread."
        _say_gateway("[yellow]Not queued again:[/yellow]", note)
        return
    err = None
    if isinstance(resp, dict):
        err = resp.get("error") or resp.get("Error")
    _say_gateway("[red]Not queued:[/red]", err or "the Gateway gave no answer this tool understands")
    raise typer.Exit(1)


def _report_broadcast(resp: Any, who: str) -> None:
    """Report a broadcast: one row per recipient, each queued, dropped or refused on its own.

    Counting is by OUTCOME, not by row - a broadcast where every row was refused queued nothing, and
    saying "sent to 4 sessions" about it would be the failure this report exists to prevent. Rows that
    were not queued are listed either way.

    BROADCAST_EXIT_RULE (inspection 1, ruling 6), the same words as `message send --help`:
    Exit code: 0 when the message was queued or an identical one is already waiting unread - for 'all', when that is true of at least one worker - and 1 when nothing was queued and nothing was waiting.
    So an all-duplicate broadcast exits 0, exactly as a duplicate single send does: the message is
    already in every one of those inboxes.
    """
    if not isinstance(resp, dict):
        console.print("[red]Not queued:[/red] the Gateway gave no answer this tool understands.")
        raise typer.Exit(1)
    if bool(resp.get("denied", resp.get("Denied", False))):
        reason = resp.get("deniedReason") or resp.get("DeniedReason") or "the broadcast was refused"
        _say_gateway("[red]Not queued:[/red]", reason)
        raise typer.Exit(1)
    warning = resp.get("warning") or resp.get("Warning")
    results = resp.get("results", resp.get("Results")) or []
    if not results:
        # Inspection 2, ruling 4: nothing queued and nothing waiting is a failure, even with nobody to
        # send to - BROADCAST_EXIT_RULE has no exception for an empty recipient list.
        line = "Not queued: no workers to send to."
        if warning:
            line += f" {warning}"
        _say_gateway("", line)
        raise typer.Exit(1)
    queued = [r for r in results if isinstance(r, dict) and r.get("status", r.get("Status")) == "queued"]
    dupes = [r for r in results if isinstance(r, dict) and r.get("status", r.get("Status")) == "duplicate"]
    refused = [r for r in results if r not in queued and r not in dupes]
    console.print(
        f"Queued for {len(queued)} of {len(results)} session(s) in {escape(who)}. Nothing was typed "
        "into any of them; each reads it from its inbox when it is free.",
        highlight=False,
    )
    for r in dupes:
        sid = gateway.short_id(str(r.get("recipientSessionId", r.get("RecipientSessionId", "")) or ""))
        _say_gateway(f"  [yellow]{sid} not queued again:[/yellow]", r.get("note") or r.get("Note") or "")
    for r in refused:
        sid = gateway.short_id(str(r.get("recipientSessionId", r.get("RecipientSessionId", "")) or "")) if isinstance(r, dict) else "?"
        why = (r.get("error") or r.get("Error")) if isinstance(r, dict) else None
        _say_gateway(f"  [red]{sid} not queued:[/red]", why or "refused")
    if not queued and not dupes:  # BROADCAST_EXIT_RULE, see the docstring
        raise typer.Exit(1)


def send_message(
    target: str,
    message: str,
    everyone: bool = False,
    reason: str | None = None,
    grant: str | None = None,
    kind: str = "message",
) -> None:
    """Queue a message for one session, or for each of your workers with target 'all'.

    The Gateway decides who you may write to: the session that started you, and the sessions you
    started. Anything else is refused with the reason. --everyone reaches the whole account and needs
    a human grant plus a reason (issue #1229); its copies are queued like any other message."""
    if target.strip().lower() == "all":
        # No sender field: the Gateway takes it from the session key that authenticated the call, so
        # the workers it finds and the sender it records are about the same session by construction.
        body: Dict[str, Any] = {"text": message}
        if everyone:
            body["everyone"] = True
            if reason:
                body["reason"] = reason
            if grant:
                body["grantId"] = grant
        try:
            resp = gateway.post_json("fleet/broadcast", body)
        except gateway.GatewayError as err:
            _say_gateway("[red]Not queued:[/red]", err)
            raise typer.Exit(1)
        _report_broadcast(resp, "the whole account" if everyone else "your workers")
        return

    chosen = _resolve_target(target, command_name="cc-devthrottle message send")
    target_sid = gateway.field(chosen, "sessionId", "SessionId")
    body = {"text": message}
    if kind != "message":
        body["kind"] = kind
    try:
        resp = gateway.post_json(f"sessions/{target_sid}/message", body)
    except gateway.GatewayError as err:
        _say_gateway("[red]Not queued:[/red]", err)
        raise typer.Exit(1)

    name = gateway.field(chosen, "name", "Name") or gateway.short_id(target_sid)
    _report_queued(resp, f"{name} ({gateway.short_id(target_sid)})")


INBOX_HELP = [
    "cc-devthrottle message inbox --all",
    'cc-devthrottle message send <id> "<message>"',
    'cc-devthrottle session report "<what you did>"',
]


def _inbox_block(m: Dict[str, Any], index: int, total: int) -> str:
    """One message, in full, as ASCII lines. The text is never cut: reading it marked it read, so a
    truncated body would be a message the recipient can never see the rest of."""
    sender_id = m.get("fromSessionId") or m.get("FromSessionId")
    sender_name = m.get("fromName") or m.get("FromName")
    machine = m.get("fromMachine") or m.get("FromMachine")
    if sender_id:
        who = f"{sender_name} ({sender_id})" if sender_name else str(sender_id)
        if machine:
            who += f" on {machine}"
    else:
        who = "the Gateway"
    text = str(m.get("text", m.get("Text", "")) or "")
    lines = [
        f"message {index} of {total}",
        f"  id: {axi_output.escape_ascii(str(m.get('messageId', m.get('MessageId', ''))))}",
        f"  from: {axi_output.escape_ascii(who)}",
        f"  kind: {axi_output.escape_ascii(str(m.get('kind', m.get('Kind', ''))))}",
        f"  sent: {axi_output.escape_ascii(str(m.get('sentAtUtc', m.get('SentAtUtc', ''))))}",
        "  text:",
    ]
    lines.extend("    " + axi_output.escape_ascii(line) for line in text.split("\n"))
    return "\n".join(lines)


def read_inbox(include_read: bool = False, json_output: bool = False) -> Dict[str, Any]:
    """Read THIS session's inbox: every unread message in full, each marked read by this call.

    Reading is the acknowledgement. A message stays open - and its sender can see it is still unread -
    until the recipient runs this. `--all` adds the newest 200 messages read in the last 24 hours:
    reading marks a message read before its text reaches you, so this is how a lost read is recovered.
    That interval is an accepted gap (inspection 2, ruling 2): after 24 hours a lost read is gone.
    """
    path = "fleet/inbox?all=true" if include_read else "fleet/inbox"
    try:
        resp = gateway.get_json(path)
    except gateway.GatewayError as err:
        _say_gateway("[red]Error:[/red]", err)
        raise typer.Exit(1)
    if not isinstance(resp, dict):
        console.print("[red]Error:[/red] the Gateway did not return an inbox.")
        raise typer.Exit(1)
    if json_output:
        print(json.dumps(resp, indent=2))
        return resp

    unread = [m for m in (resp.get("unread", resp.get("Unread")) or []) if isinstance(m, dict)]
    recent = [m for m in (resp.get("recent", resp.get("Recent")) or []) if isinstance(m, dict)]
    blocks = [f"count: {len(unread)} unread" + (" (now marked read)" if unread else "")]
    for n, m in enumerate(unread, 1):
        blocks.append(_inbox_block(m, n, len(unread)))
    if include_read:
        # Inspection 2, ruling 1: the Gateway returns at most the newest 200 read messages and says so. A
        # truncated answer must say it is one, or the reader takes 200 for the whole day.
        truncated = bool(resp.get("truncated", resp.get("Truncated", False)))
        total = resp.get("recentTotal", resp.get("RecentTotal"))
        if truncated:
            blocks.append(f"earlier: showing {len(recent)} of {total} read in the last 24 hours")
        else:
            blocks.append(f"earlier: {len(recent)} read before")
        for n, m in enumerate(recent, 1):
            blocks.append(_inbox_block(m, n, len(recent)))
    blocks.append(axi_output.format_help(INBOX_HELP))
    axi_output.write_blocks(sys.stdout, *blocks)
    return resp


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
        console.print(
            "[yellow]Warning:[/yellow] could not read the fleet list to inherit the controlling "
            f"session's mission, so the new session starts attached to no mission: {err}"
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
        console.print(
            "[red]Error:[/red] this spawn has to say who will OWN the new session."
        )
        console.print(
            """
You are spawning from inside a session, so there are two
possible owners and no safe default between them. Say which:

  --controlled-by self
      YOU own it. It stays quiet on the roster and reports
      back to you when it finishes.
      Use this for work you will collect.

  --standalone
      The USER owns it. It goes red and asks HIM when it
      finishes, and you will not hear from it.
      Use this for work you are starting on his behalf.

  (--controlled-by <another session's id> is refused by the
  Gateway: the owner is who the new session may message, so
  a session may only name itself.)

This used to default to 'self' silently. It no longer does:
a session that answers to a machine rather than to the user
is the user's decision, not a side effect of an environment
variable being set.
""",
            markup=False,
            highlight=False,
        )
        raise typer.Exit(1)

    # HANDING WORK TO THE USER IS A DELIBERATE ACT, SO IT STATES A REASON (owner's ruling, 2026-09-14).
    # --standalone and --controlled-by self cost the same to type, and on 14 September three of thirteen
    # agent-started sessions had chosen --standalone - two of them then sat red at the owner. Requiring a
    # reason does not forbid the choice; it makes an agent that cannot justify it collect its own work.
    #
    # THE REASON IS NOT YET CARRIED ON THE WIRE, and saying so is better than implying otherwise: there is
    # no field for it on the create, so it is printed here and lands in the SPAWNING session's transcript,
    # which is searchable and durable. A field on the session is the right home and is not built.
    if cc_session and opt_out and not (why or "").strip():
        console.print("[red]Error:[/red] --standalone gives this session to the USER. Say why.")
        console.print(
            """
You are handing work to the owner rather than collecting it
yourself, so it will go RED and ask him when it finishes.

  --why "he asked me to open this for him"
  --why "this needs his decision before anything else runs"

If you cannot say why it is his, it is probably yours:

  --controlled-by self
""",
            markup=False,
            highlight=False,
        )
        raise typer.Exit(1)

    if opt_out:
        controller_session_id = None
    elif controlled_by:
        if controlled_by.strip().lower() == "self":
            controller_session_id = cc_session
            if not controller_session_id:
                console.print(
                    "[red]Error:[/red] --controlled-by self requires CC_SESSION_ID to be set, but it "
                    "is not. Run this from inside a session, or pass an explicit controlling session id."
                )
                raise typer.Exit(1)
        else:
            controller_session_id = controlled_by
    # Issue #800: always name your session. On this fleet many sessions run in the same
    # checkout, so a session with neither a name nor a purpose still gets an auto-composed
    # name from the Director, but it reads better when you describe what it is FOR.
    if not name and not purpose:
        console.print(
            "[yellow]Warning:[/yellow] no --name or --purpose given; the session will get an "
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

    if target_director:
        # ONE named Director. The Gateway's machine route picks "some Director on that computer" and
        # has no way to be told which, so naming one has to be addressed to it BY ID - which is what
        # /directors/{id}/sessions is. Resolving the typed name against the Director list is the same
        # class of lookup `session list` already does for a session id or name; what a name MATCHES is
        # a client's job, what may be DONE with the result is the Gateway's.
        path = f"directors/{_resolve_director_id(target_director, target_machine)}/sessions"
    elif target_machine:
        # "Some Director on that computer", launching one if none is running.
        path = f"machines/{target_machine}/sessions"
    else:
        # HERE. This session's own Director, named from what the session was told at launch - no
        # roster lookup, no hostname read off the operating system, and no round trip to work out
        # something the session already knows.
        path = f"directors/{_my_director()}/sessions"

    try:
        resp = gateway.post_json(path, body)
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    sid = gateway.field(resp, "sessionId", "SessionId")
    if not sid:
        console.print("[red]Error:[/red] the Gateway did not return a session id.")
        raise typer.Exit(1)

    short = gateway.short_id(sid)
    # The Director names the session at birth (issue #800), so the response carries the final name.
    label = gateway.field(resp, "name", "Name") or name or short
    console.print(f"[green]Opened[/green] session {short} ({label}).")
    if opt_out and cc_session:
        console.print(f"[yellow]The USER owns it[/yellow] - it will go red and ask him. Reason given: {why.strip()}")
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
            f"Attached to mission [bold]{mission_label}[/bold], inherited from its controlling "
            f"session {controller_label}. Undo with: cc-devthrottle mission detach {short}"
        )
    console.print(
        f'Message it (queued, read when it is free):  cc-devthrottle message send {short} "<message>"'
    )


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
        rows = gateway.get_json("directors") or []
    except gateway.GatewayError as err:
        raise gateway.GatewayError(f"Cannot list this account's Directors to resolve '{name}': {err}") from err

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
            f"No Director matches '{name}'{where}. Run cc-devthrottle machine directors to see them."
        )
    if len(exact) > 1:
        raise gateway.GatewayError(
            f"'{name}' matches {len(exact)} Directors. Name it more precisely, or add --machine."
        )
    return gateway.field(exact[0], "directorId", "DirectorId")



def _spawn_selftest(repo: str, command_args: str, name: str) -> str:
    # Controlled by THIS session: a session may message only the sessions it started, so a throwaway
    # nobody owns could not be written to at all, and the test would be measuring the refusal.
    resp = gateway.post_json(
        f"directors/{_my_director()}/sessions",
        {
            "repoPath": repo,
            "agent": "RawCli",
            "command": "cmd",
            "commandArgs": command_args,
            "controllerSessionId": gateway.session_id(),
        },
    )
    sid = gateway.field(resp, "sessionId", "SessionId")
    if not sid:
        raise gateway.GatewayError("the Gateway did not return a session id when spawning.")
    try:
        gateway.patch_json(f"sessions/{sid}", {"name": name})
    except gateway.GatewayError:
        pass
    return sid


def _fleet_ids() -> List[str]:
    # Goes through the one shared fetch so the selftest reads the same roster every verb does. The
    # sessions it checks for are the ones it just spawned on THIS Director, which always reports its
    # own (issue #1019), so completeness cannot hide them - but reading a different route than the
    # rest of the tool is how a selftest ends up passing on a roster nobody else sees.
    sessions, _, _, _ = gateway.get_fleet()
    return [gateway.field(s, "sessionId", "SessionId") for s in sessions]


def selftest(timeout_ms: int) -> None:
    """Run the fleet messaging self-test against the local Director.

    It spawns one throwaway it controls, queues a message for it, and checks the Gateway answered
    "queued". It cannot read the throwaway's inbox - only the throwaway's own key can - so what it
    proves is that a message to your own worker is accepted and recorded, not that it was read.
    `timeout_ms` is kept for callers that still pass it; nothing waits any more.
    """
    repo = tempfile.gettempdir()
    results: List[Tuple[str, bool, str]] = []
    recipient: Optional[str] = None

    def record(step: str, ok: bool, detail: str = "") -> None:
        results.append((step, ok, detail))
        mark = "[green]PASS[/green]" if ok else "[red]FAIL[/red]"
        console.print(f"  {mark}  {step}{('  - ' + detail) if detail else ''}")

    try:
        recipient = _spawn_selftest(repo, "/k", "selftest-recipient")
        record("spawn a worker", True, f"recipient={gateway.short_id(recipient)}")
        time.sleep(2)

        ids = _fleet_ids()
        record("session list includes it", recipient in ids)

        send = gateway.post_json(
            f"sessions/{recipient}/message", {"text": "fleet self-test message"},
        )
        status = gateway.field(send, "status", "Status") if isinstance(send, dict) else ""
        record("message send queues", status == "queued", f"status={status}")

    except gateway.GatewayError as err:
        record("fleet messaging reachable", False, str(err))
    finally:
        for sid in (recipient,):
            if sid:
                try:
                    # request-deletion, not a hard DELETE: that is the verb an agent credential may
                    # call, and it is what `session done` uses. The reaper removes the session within
                    # about a minute, which is why the check below allows for a grace period.
                    gateway.post_json(f"sessions/{sid}/request-deletion", {})
                except gateway.GatewayError:
                    pass
        try:
            time.sleep(1)
            remaining = _fleet_ids()
            leaked = [s for s in (recipient,) if s and s in remaining]
            record("throwaway sessions cleaned up", not leaked, "" if not leaked else f"leaked {len(leaked)}")
        except gateway.GatewayError as err:
            record("throwaway sessions cleaned up", False, str(err))

    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    if passed == total and total > 0:
        console.print(f"[green]PASS[/green] - fleet messaging self-test: {passed}/{total} checks passed.")
        raise typer.Exit(0)
    console.print(f"[red]FAIL[/red] - fleet messaging self-test: {passed}/{total} checks passed.")
    raise typer.Exit(1)
