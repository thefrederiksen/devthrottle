"""Machines: find what is installed on another computer, find a file on it, and start something there.

Backed by the Gateway's /launchers and /machines routes, which reach that machine's cc-launcher. Every
call is scoped to the calling account by the Gateway, so a machine name only ever reaches a machine this
account registered - and since the Remove-the-network-port mission's phase 2 the credential presented is
this SESSION's own key, so it is scoped to one session inside that account as well.

Read the search commands as questions and `launch` as an instruction: `machine apps` and `machine files`
change nothing, while `machine launch` starts a program on a computer you may not be sitting at.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import typer
from rich import box
from rich.console import Console
from rich.table import Table

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output  # noqa: E402
from cc_shared import gateway  # noqa: E402

console = Console()


def _fail(message: str) -> None:
    console.print(f"[red]Error:[/red] {message}")
    raise typer.Exit(1)


def _call(path: str) -> Any:
    try:
        payload = gateway.get_json(path)
    except gateway.GatewayError as err:
        _fail(str(err))
    if isinstance(payload, dict) and payload.get("error"):
        _fail(str(payload["error"]))
    return payload


def _size(size: Any) -> str:
    try:
        count = int(size)
    except (TypeError, ValueError):
        return "-"
    if count >= 1_073_741_824:
        return f"{count / 1_073_741_824:.1f}G"
    if count >= 1_048_576:
        return f"{count / 1_048_576:.1f}M"
    if count >= 1024:
        return f"{count / 1024:.0f}K"
    return str(count)


# --- machine list and director list (AXI standard, docs/axi-standard.md; issue #2922) ---
#
# Both render through the shared output helper, the same way `session list` does: a count line with a
# breakdown by state, the list with full names and ids, and help[] lines. `--json` prints exactly what
# the Gateway sent when no filter is given, and a filter narrows the same bare array.
#
# RENDER, NEVER RULE. Neither state is decided here. A Director's state is the one the Gateway folds
# into the roster envelope for the Fleet Map (online, wobbly, offline, stopped); a machine's state is the
# launcher reach the Gateway folds into its Machines view. This only names that verdict in one word, and
# a value this tool does not know fails loudly instead of being guessed.

# A machine's plain state, in the order the count line lists them, and the Gateway reach each names.
# `machine list` lists the machines that have a launcher, so the Gateway's fourth reach, NoLauncher (a
# Director on a machine with no launcher), can never be a row's state: those machines are counted in one
# line after the list instead, and a launcher row the Gateway calls NoLauncher fails loudly as unknown.
MACHINE_STATES = ("online", "offline", "too-old")
_MACHINE_STATE_FOR_REACH = {
    "Connected": "online",
    "NotConnected": "offline",
    "NotStreamCapable": "too-old",
}
_NO_LAUNCHER_REACH = "NoLauncher"

MACHINE_LIST_FIELDS = ("name", "state", "version", "pid", "started", "last-seen")
MACHINE_LIST_DEFAULT_FIELDS = ("name", "state", "version")

# A Director's state, exactly as the Gateway names it (DirectorReachabilityDto), in count-line order.
DIRECTOR_STATES = ("online", "wobbly", "offline", "stopped")

DIRECTOR_LIST_FIELDS = ("id", "name", "machine", "state", "version", "pid", "user", "started", "last-seen")
DIRECTOR_LIST_DEFAULT_FIELDS = ("id", "name", "machine", "state")


def _usage_error(message: str) -> None:
    print(f"Error: {message}", file=sys.stderr)
    raise typer.Exit(axi_output.USAGE_ERROR_EXIT_CODE)


def _answer_error(message: str) -> None:
    """The Gateway's answer cannot be listed truthfully. Exit 1; --json never prints half an answer."""
    print(f"Error: {axi_output.escape_ascii(message)}", file=sys.stderr)
    raise typer.Exit(1)


def _get_or_exit(path: str) -> Any:
    try:
        payload = gateway.get_json(path)
    except gateway.GatewayError as err:
        _answer_error(str(err))
    if isinstance(payload, dict) and payload.get("error"):
        _answer_error(str(payload["error"]))
    return payload


def _parse_states(requested: Optional[str], valid: Tuple[str, ...]) -> Optional[List[str]]:
    """Turn a `--state` value (one state, or several separated by commas) into a list, or exit 2."""
    if requested is None:
        return None
    names = [part.strip() for part in requested.split(",")]
    unknown = [name for name in names if name not in valid]
    if unknown:
        listed = ", ".join(repr(axi_output.escape_ascii(name)) for name in unknown)
        _usage_error(f"unknown --state value {listed}. Valid states: {', '.join(valid)}")
    return names


def _check_usage(json_output: bool, fields: Optional[str], valid: Tuple[str, ...],
                 default: Tuple[str, ...]) -> List[str]:
    # Usage errors come before the fetch: a bad flag is the caller's to fix, whatever the fleet holds.
    if json_output and fields is not None:
        _usage_error("--fields does not apply to --json, which always carries every field. Drop one of them.")
    return axi_output.parse_fields_or_exit(fields, valid, default)


def _rows_or_exit(payload: Any, what: str, id_key: str, id_camel: str,
                  also_required: Tuple[Tuple[str, str], ...] = ()) -> List[Dict[str, Any]]:
    """The Gateway's list, checked. Absent is not empty, and a row with no identifier cannot be named.

    `also_required` names further (camelCase, PascalCase) fields every row must carry as a non-blank
    string - a row missing one would otherwise be silently dropped by a filter on that field."""
    if not isinstance(payload, list):
        shown = "nothing" if payload is None else f"a {type(payload).__name__}"
        _answer_error(f"the Gateway's {what} list answer was not a list (got {shown}).")
    for index, row in enumerate(payload):
        if not isinstance(row, dict):
            _answer_error(f"the Gateway's {what} list has a row that is not an object (row {index + 1}).")
        for camel, pascal in ((id_camel, id_key),) + also_required:
            if not _is_name(_value(row, camel, pascal)):
                _answer_error(
                    f"the Gateway returned a {what} with no {camel} (row {index + 1}). "
                    "This tool will not list what it cannot name or place."
                )
    return payload


# The kind of value each displayed field must hold, exactly as the Gateway's DTO serializes it
# (DirectorDto, LauncherDto). A value of any other kind is refused, never shown as a guess or a blank.
_STRING = "a string"
_NUMBER = "a whole number"
_STRING_OR_NULL = "a string or null"


def _value(row: Dict[str, Any], camel: str, pascal: str) -> Any:
    return row[camel] if camel in row else row.get(pascal)


def _is_name(value: Any) -> bool:
    return isinstance(value, str) and bool(value.strip())


def _kind_ok(value: Any, kind: str) -> bool:
    if kind == _NUMBER:
        return isinstance(value, int) and not isinstance(value, bool)
    if kind == _STRING_OR_NULL and value is None:
        return True
    return isinstance(value, str)


def _displayed_or_exit(rows: List[Dict[str, Any]], what: str,
                       spec: Tuple[Tuple[str, str, str], ...]) -> None:
    """Every displayed field of every row is present and of the kind the Gateway's DTO sends.

    A field that is absent, or holds another kind of value, would otherwise be printed as a blank or as
    Python's rendering of whatever arrived - an answer that looks read but was not."""
    for index, row in enumerate(rows):
        for camel, pascal, kind in spec:
            present = camel in row or pascal in row
            value = _value(row, camel, pascal)
            if not present or not _kind_ok(value, kind):
                shown = "missing" if not present else f"{type(value).__name__} {value!r}"
                _answer_error(
                    f"the Gateway returned a {what} whose {camel} is {shown}, not {kind} (row {index + 1}). "
                    "--json shows the raw rows."
                )


_DIRECTOR_DISPLAYED = (
    ("displayName", "DisplayName", _STRING),
    ("version", "Version", _STRING),
    ("pid", "Pid", _NUMBER),
    ("user", "User", _STRING),
    ("startedAt", "StartedAt", _STRING),
    ("lastSeen", "LastSeen", _STRING_OR_NULL),
)
_LAUNCHER_DISPLAYED = (
    ("version", "Version", _STRING),
    ("pid", "Pid", _NUMBER),
    ("startedAt", "StartedAt", _STRING),
    ("lastSeenAt", "LastSeenAt", _STRING),
)


def _text(row: Dict[str, Any], camel: str, pascal: str) -> Optional[str]:
    value = _value(row, camel, pascal)
    return None if value is None else str(value)


def _count_line(states: List[str], order: Tuple[str, ...], total: Optional[int]) -> str:
    # No rows means no breakdown at all: the helper refuses an empty one, and "count: 0" says it all.
    breakdown = [(name, n) for name in order if (n := states.count(name))] or None
    return axi_output.format_count(len(states), total=total, breakdown=breakdown)


def _note(text: str) -> str:
    """A free-text note or caution for plain output, escaped so it is always one physical line of printable
    ASCII - a newline or tab in a name the Gateway sent must not split it."""
    return axi_output.escape_ascii(text)


def _write(blocks: List[str]) -> None:
    # Every block is already rendered: lists and help by the shared helper, notes through _note.
    axi_output.write_blocks(sys.stdout, *blocks)


def _machine_states(launchers: List[Dict[str, Any]]) -> Tuple[List[str], List[str]]:
    """Each launcher's plain state, from the Gateway's Machines view, and the names of the machines that
    view reports with no launcher. Fails loudly on anything unknown.

    Every entry the view returns is checked, not only the ones matched to a launcher: an entry that is
    skipped is a machine turned into silence, and on a fleet with no launchers that silence reads as
    "No machines are registered"."""
    view = _get_or_exit("machines")
    machines = view.get("machines") if isinstance(view, dict) else None
    if not isinstance(machines, list):
        _answer_error("the Gateway's machines view has no list of machines, so no machine state can be shown.")
    reach_by_name: Dict[str, str] = {}
    name_by_key: Dict[str, str] = {}
    no_launcher: List[str] = []
    for index, entry in enumerate(machines):
        # A row that cannot be named is refused, never skipped: skipping it turns a machine into silence.
        name = entry.get("machine") if isinstance(entry, dict) else None
        if not _is_name(name):
            _answer_error(
                f"the Gateway's machines view has an entry with no machine name (entry {index + 1}). "
                "This tool will not list what it cannot name."
            )
        reach = entry.get("reach")
        if reach != _NO_LAUNCHER_REACH and (not isinstance(reach, str) or reach not in _MACHINE_STATE_FOR_REACH):
            shown = "missing" if reach is None else repr(reach)
            _answer_error(
                f"machine {name} has a launcher reach that is {shown}; this tool knows only "
                f"{', '.join(_MACHINE_STATE_FOR_REACH)} and {_NO_LAUNCHER_REACH}. "
                "If the Gateway has added one, update cc-devthrottle; --json shows the raw rows."
            )
        # The Gateway folds one entry per machine, ignoring case; a second one leaves the state undecided.
        key = name.lower()
        if key in reach_by_name:
            _answer_error(f"the Gateway's machines view lists machine {name} more than once (entry {index + 1}).")
        reach_by_name[key] = reach
        name_by_key[key] = name
        if reach == _NO_LAUNCHER_REACH:
            no_launcher.append(name)

    listed = set()
    states = []
    for row in launchers:
        name = gateway.field(row, "machineName", "MachineName")
        key = name.lower()
        if key not in reach_by_name:
            _answer_error(
                f"the Gateway's machines view does not include {name}, so its state is unknown. "
                "The machine may have registered a moment ago; run the command again."
            )
        if key in listed:
            _answer_error(f"the Gateway's launcher list names machine {name} more than once.")
        listed.add(key)
        state = _MACHINE_STATE_FOR_REACH.get(reach_by_name[key])
        if state is None:
            # Both answers are read from one launcher registry, so a listed launcher the view calls
            # NoLauncher is a contradiction, not a state.
            _answer_error(
                f"machine {name} has a launcher reach that is {_NO_LAUNCHER_REACH!r}, yet the Gateway lists "
                "its launcher. Run the command again; --json shows the raw rows."
            )
        states.append(state)

    # The view is folded from that same registry, so a launcher it reports that the list did not is a
    # machine this list would silently leave out.
    for key, reach in reach_by_name.items():
        if reach != _NO_LAUNCHER_REACH and key not in listed:
            _answer_error(
                f"the Gateway's machines view reports a launcher on {name_by_key[key]}, but its launcher list "
                "does not include it. The launcher may have stopped a moment ago; run the command again."
            )
    return states, no_launcher


def _no_launcher_line(names: List[str]) -> str:
    """One plain line for the machines that run a Director but no launcher, which this list cannot show."""
    if len(names) == 1:
        head = f"1 more machine has a Director but no launcher, so it is not listed: {names[0]}."
    else:
        head = (f"{len(names)} more machines have a Director but no launcher, so they are not listed: "
                f"{', '.join(names)}.")
    return _note(head + " See them with: cc-devthrottle director list")


def list_machines(json_output: bool, *, state: Optional[str] = None, fields: Optional[str] = None) -> None:
    """Every machine this account can search and start things on, optionally narrowed by state."""
    chosen_fields = _check_usage(json_output, fields, MACHINE_LIST_FIELDS, MACHINE_LIST_DEFAULT_FIELDS)
    wanted = _parse_states(state, MACHINE_STATES)

    launchers = _rows_or_exit(_get_or_exit("launchers"), "machine", "MachineName", "machineName")
    filtered = wanted is not None
    if json_output and not filtered:
        # Exactly what the Gateway sent: an unfiltered --json never depends on the state lookup.
        print(json.dumps(launchers, indent=2))
        return

    states, no_launcher = _machine_states(launchers)
    rows = [(r, st) for r, st in zip(launchers, states) if wanted is None or st in wanted]
    if json_output:
        print(json.dumps([r for r, _ in rows], indent=2))
        return

    _displayed_or_exit(launchers, "machine", _LAUNCHER_DISPLAYED)
    records = [
        {
            "name": gateway.field(r, "machineName", "MachineName"),
            "state": st,
            "version": _text(r, "version", "Version"),
            "pid": _text(r, "pid", "Pid"),
            "started": _text(r, "startedAt", "StartedAt"),
            "last-seen": _text(r, "lastSeenAt", "LastSeenAt"),
        }
        for r, st in rows
    ]
    blocks = [
        _count_line([st for _, st in rows], MACHINE_STATES, len(launchers) if filtered else None),
        axi_output.render_list("machines", chosen_fields, records),
    ]
    if not rows:
        if filtered and launchers:
            blocks.append(_note("No machine matches the filter."))
        else:
            blocks.append(_note(
                "No machines are registered. A machine appears here once cc-launcher is running on it "
                "and has registered with the Gateway."
            ))
    if no_launcher:
        blocks.append(_no_launcher_line(no_launcher))
    blocks.append(axi_output.format_help(_machine_list_help(bool(rows), filtered, chosen_fields)))
    _write(blocks)


def _machine_list_help(any_rows: bool, filtered: bool, chosen_fields: List[str]) -> List[str]:
    """Concrete next commands. Runtime values are placeholders, never guessed."""
    if not any_rows:
        if filtered:
            return ["cc-devthrottle machine list", "cc-devthrottle machine list --help"]
        return ["cc-devthrottle director list", "cc-devthrottle machine list --help"]
    commands = []
    if not filtered:
        commands.append("cc-devthrottle machine list --state offline")
    if list(chosen_fields) == list(MACHINE_LIST_DEFAULT_FIELDS):
        commands.append("cc-devthrottle machine list --fields " + ",".join(MACHINE_LIST_FIELDS))
    commands.append("cc-devthrottle director list --machine <name>")
    commands.append('cc-devthrottle machine apps <name> "<query>"')
    commands.append("cc-devthrottle machine restart-capability <name>")
    return commands


def _director_states(directors: List[Dict[str, Any]]) -> List[str]:
    """Each Director's state, as the Gateway folded it into the roster envelope. Fails loudly on anything
    missing or unknown - in every entry the roster returns, not only the ones matched to a Director."""
    envelope = _get_or_exit("sessions?envelope=true")
    reach = envelope.get("directors") if isinstance(envelope, dict) else None
    if not isinstance(reach, list):
        _answer_error(
            "the Gateway's roster answer has no list of Director states, so no Director state can be shown. "
            "--json shows the raw rows."
        )
    state_by_id: Dict[str, str] = {}
    for index, entry in enumerate(reach):
        did = entry.get("directorId") if isinstance(entry, dict) else None
        if not _is_name(did):
            _answer_error(
                f"the Gateway's roster has a Director state with no directorId (entry {index + 1}), "
                "so it cannot be matched to a Director. --json shows the raw rows."
            )
        state = entry.get("state")
        if not isinstance(state, str) or state not in DIRECTOR_STATES:
            shown = "missing" if state is None else repr(state)
            _answer_error(
                f"Director {did} has a state that is {shown}; this tool knows only "
                f"{', '.join(DIRECTOR_STATES)}. "
                "If the Gateway has added one, update cc-devthrottle; --json shows the raw rows."
            )
        if did.lower() in state_by_id:
            _answer_error(f"the Gateway's roster gives Director {did} more than one state (entry {index + 1}).")
        state_by_id[did.lower()] = state
    states = []
    for row in directors:
        did = gateway.field(row, "directorId", "DirectorId")
        if did.lower() not in state_by_id:
            _answer_error(
                f"the Gateway's roster does not include a state for Director {did}. "
                "It may have registered a moment ago; run the command again."
            )
        states.append(state_by_id[did.lower()])
    return states


def _director_name(row: Dict[str, Any]) -> str:
    # An unnamed instance is shown by its machine name, exactly as the Director's own toolbar does and as
    # DirectorDto documents - the alternative is a blank name in the list you pick a Director from.
    # The name itself is shown exactly as registered, never trimmed or shortened.
    display = gateway.field(row, "displayName", "DisplayName")
    return display if display.strip() else gateway.field(row, "machineName", "MachineName")


def list_directors(
    json_output: bool,
    *,
    state: Optional[str] = None,
    machine: Optional[str] = None,
    fields: Optional[str] = None,
) -> None:
    """Every Director this account is running, on every machine - and how to name one.

    A machine can appear several times: each named Director instance registers its own row. The name is
    what a person reads; the id is what `session spawn --director` should carry, because it survives a
    rename and cannot collide with a second Director called the same thing.
    """
    chosen_fields = _check_usage(json_output, fields, DIRECTOR_LIST_FIELDS, DIRECTOR_LIST_DEFAULT_FIELDS)
    wanted = _parse_states(state, DIRECTOR_STATES)
    if machine is not None and not machine.strip():
        _usage_error("--machine needs a value.")

    # Every row must carry its machine, or --machine would silently drop it and answer "none".
    directors = _rows_or_exit(_get_or_exit("directors"), "Director", "DirectorId", "directorId",
                              also_required=(("machineName", "MachineName"),))
    filtered = wanted is not None or machine is not None
    if json_output and not filtered:
        # Exactly what the Gateway sent: an unfiltered --json never depends on the state lookup.
        print(json.dumps(directors, indent=2))
        return

    # The state is looked up only where it is needed, so --machine --json does not depend on it either.
    need_states = not json_output or wanted is not None
    states = _director_states(directors) if need_states else [""] * len(directors)
    wanted_machine = machine.strip().lower() if machine is not None else None
    rows = [
        (r, st)
        for r, st in zip(directors, states)
        if (wanted is None or st in wanted)
        and (wanted_machine is None or gateway.field(r, "machineName", "MachineName").lower() == wanted_machine)
    ]
    if json_output:
        print(json.dumps([r for r, _ in rows], indent=2))
        return

    _displayed_or_exit(directors, "Director", _DIRECTOR_DISPLAYED)
    records = [
        {
            "id": gateway.field(r, "directorId", "DirectorId"),
            "name": _director_name(r),
            "machine": _text(r, "machineName", "MachineName"),
            "state": st,
            "version": _text(r, "version", "Version"),
            "pid": _text(r, "pid", "Pid"),
            "user": _text(r, "user", "User"),
            "started": _text(r, "startedAt", "StartedAt"),
            "last-seen": _text(r, "lastSeen", "LastSeen"),
        }
        for r, st in rows
    ]
    blocks = [
        _count_line([st for _, st in rows], DIRECTOR_STATES, len(directors) if filtered else None),
        axi_output.render_list("directors", chosen_fields, records),
    ]
    if not rows:
        if filtered and directors:
            blocks.append(_note("No Director matches the filter."))
        else:
            blocks.append(_note(
                "No Directors are registered. A Director appears here once it is running and has "
                "connected to the Gateway."
            ))
    blocks.append(axi_output.format_help(_director_list_help(bool(rows), filtered, chosen_fields)))
    _write(blocks)


def _director_list_help(any_rows: bool, filtered: bool, chosen_fields: List[str]) -> List[str]:
    """Concrete next commands. Runtime values are placeholders, never guessed."""
    if not any_rows:
        if filtered:
            return ["cc-devthrottle director list", "cc-devthrottle director list --help"]
        return ["cc-devthrottle machine list", "cc-devthrottle director list --help"]
    commands = []
    if not filtered:
        commands.append("cc-devthrottle director list --state offline")
    if list(chosen_fields) == list(DIRECTOR_LIST_DEFAULT_FIELDS):
        commands.append("cc-devthrottle director list --fields " + ",".join(DIRECTOR_LIST_FIELDS))
    commands.append("cc-devthrottle session spawn <repo> --director <id> --controlled-by self")
    commands.append("cc-devthrottle session list --machine <machine>")
    return commands


def list_apps(machine: str, query: Optional[str], limit: int, json_output: bool) -> None:
    """What is installed on one machine."""
    path = f"machines/{machine}/apps?q={query or ''}&limit={limit}"
    payload: Dict[str, Any] = _call(path) or {}
    apps: List[Dict[str, Any]] = payload.get("apps") or payload.get("Apps") or []

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    if not apps:
        console.print(f"Nothing on {machine} matches {query or '(everything)'}.")
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("APPLICATION", "SOURCE", "PATH"):
        table.add_column(column)
    for app in apps:
        table.add_row(
            str(gateway.field(app, "name", "Name") or "-"),
            str(gateway.field(app, "source", "Source") or "-"),
            str(gateway.field(app, "path", "Path") or "-"),
        )
    console.print(table)

    total = payload.get("totalMatches", payload.get("TotalMatches", len(apps)))
    line = f"{len(apps)} of {total} on {machine}"
    if payload.get("truncated") or payload.get("Truncated"):
        line += " - more matched than were returned; narrow the search or raise --count"
    console.print(line)

    # An unreadable directory means the catalogue is short by an unknown amount. Say so: a quietly
    # incomplete list looks exactly like a machine with less installed on it.
    skipped = payload.get("skipped") or payload.get("Skipped") or []
    if skipped:
        console.print(f"[yellow]{len(skipped)} directories could not be read, so this list may be incomplete.[/yellow]")


def search_files(machine: str, query: str, limit: int, timeout_seconds: int, json_output: bool) -> None:
    """Find files by name on one machine."""
    path = (
        f"machines/{machine}/files?q={query}"
        f"&limit={limit}&timeoutMilliseconds={timeout_seconds * 1000}"
    )
    payload: Dict[str, Any] = _call(path) or {}
    files: List[Dict[str, Any]] = payload.get("files") or payload.get("Files") or []

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    if not files:
        console.print(f"No file on {machine} matches {query}.")
    else:
        table = Table(show_header=True, header_style="bold", box=box.ASCII)
        for column in ("FILE", "SIZE", "MODIFIED", "PATH"):
            table.add_column(column)
        for hit in files:
            modified = str(gateway.field(hit, "modifiedUtc", "ModifiedUtc") or "-")
            table.add_row(
                str(gateway.field(hit, "name", "Name") or "-"),
                _size(hit.get("sizeBytes", hit.get("SizeBytes"))),
                modified[:19].replace("T", " "),
                str(gateway.field(hit, "path", "Path") or "-"),
            )
        console.print(table)

    elapsed = payload.get("elapsedMilliseconds", payload.get("ElapsedMilliseconds", 0))
    visited = payload.get("directoriesVisited", payload.get("DirectoriesVisited", 0))
    console.print(f"{len(files)} files - searched {visited} directories on {machine} in {elapsed} ms")

    # The whole point of the truncation fields: a partial answer must never read as a complete one, and the
    # advice differs by reason - a ceiling wants a narrower search, a deadline wants more time.
    if payload.get("truncated") or payload.get("Truncated"):
        reason = payload.get("truncationReason") or payload.get("TruncationReason") or "unknown"
        if reason == "limit":
            console.print(
                "[yellow]Stopped at the result limit - this is NOT the whole answer. "
                "Narrow the search or raise --count.[/yellow]"
            )
        elif reason == "timeout":
            console.print(
                "[yellow]Stopped at the time limit - this is NOT the whole answer. "
                "Narrow the search or raise --seconds.[/yellow]"
            )
        else:
            console.print(f"[yellow]Stopped early ({reason}) - this is NOT the whole answer.[/yellow]")

    unreadable = payload.get("unreadableDirectories", payload.get("UnreadableDirectories", 0))
    if unreadable:
        console.print(f"[yellow]{unreadable} directories could not be read and were not searched.[/yellow]")


def restart_capability(machine: str, json_output: bool) -> None:
    """Can this computer complete a Director restart? Ask BEFORE draining it, not after.

    This changes nothing on the machine. It sends no command, opens no connection and raises no
    signal - which matters, because the only other way to find out whether a launcher is listening
    for the restart signal is to RAISE that signal, and raising it restarts a Director as a side
    effect of the question.

    Why it exists: on 2026-09-06 a Director was drained of seventeen sessions and only then did it
    turn out that no route could restart it. The launcher was two days older than the code that lets
    a launcher be told anything, and it was running, registered and heartbeating the whole time.
    Every liveness check on that machine said yes.

    The Gateway computes the verdict and writes both sentences; this command renders them and does
    not re-derive anything. A verdict this printed itself is the one an agent acts on.
    """
    payload: Dict[str, Any] = _call(f"machines/{machine}/restart-capability") or {}

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    verdict = str(gateway.field(payload, "verdict", "Verdict") or "Unknown")
    reason = str(gateway.field(payload, "reason", "Reason") or "")
    guard = str(gateway.field(payload, "guardedRestart", "GuardedRestart") or "Unknown")
    guard_reason = str(gateway.field(payload, "guardedRestartReason", "GuardedRestartReason") or "")

    # ASCII only, and no colour carrying meaning on its own: this output is read in terminals, piped
    # into logs, and quoted into reports. The word is the answer; the colour only decorates it.
    headline = {
        "CanRestart": ("[green]", "CAN RESTART"),
        "CannotRestart": ("[red]", "CANNOT RESTART"),
    }.get(verdict, ("[yellow]", "UNKNOWN"))
    console.print(f"{headline[0]}{headline[1]}[/] {machine}")
    console.print(f"  {reason}")
    console.print("")

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("FACT", "VALUE"):
        table.add_column(column)
    table.add_row("launcher version", str(gateway.field(payload, "launcherVersion", "LauncherVersion") or "-"))
    table.add_row("reach", str(gateway.field(payload, "reach", "Reach") or "-"))
    table.add_row("declaration", str(gateway.field(payload, "declaration", "Declaration") or "-"))
    table.add_row("restart signal", str(gateway.field(payload, "restartSignal", "RestartSignal") or "-"))
    root_key = gateway.field(payload, "servingRootKey", "ServingRootKey")
    table.add_row("serving root key", str(root_key or "(not declared)"))
    instance_home = gateway.field(payload, "servingRootIsInstanceHome", "ServingRootIsInstanceHome")
    table.add_row(
        "serving an instance home",
        "(not declared)" if instance_home is None else ("YES - this is the fault" if instance_home else "no"),
    )
    declared: List[str] = gateway.field(payload, "declaredCommands", "DeclaredCommands") or []
    table.add_row("declares", ", ".join(str(d) for d in declared) if declared else "(nothing)")
    table.add_row("seconds since heartbeat", str(gateway.field(payload, "quietForSeconds", "QuietForSeconds") or 0))
    console.print(table)

    # The guarded restart is printed as its own line and never folded into the verdict above. A
    # machine can be perfectly restartable and offer no guard - which is what every launcher built
    # before the guard existed looks like - so one sentence cannot carry both answers.
    console.print("")
    console.print(f"Guarded restart (refuse while sessions are live): {guard}")
    console.print(f"  {guard_reason}")


def launch(machine: str, app: Optional[str], path: Optional[str], args: Optional[str],
           cwd: Optional[str], headless: bool, json_output: bool) -> None:
    """Start an application on one machine, by catalogue name or by absolute path."""
    if not app and not path:
        _fail("Name what to start: --app \"Chrome\" or --path \"C:\\\\Tools\\\\thing.exe\".")

    # confirmProtected carries this command's explicit intent through the relay: the Gateway refuses any
    # launch without it (tenant-boundary hardening, CR-5). Typing `machine launch` IS the confirmation -
    # the flag exists to stop programs being started as a side effect of something else.
    body = {"app": app, "path": path, "args": args, "cwd": cwd, "headless": headless,
            "confirmProtected": True}
    try:
        payload = gateway.post_json(f"machines/{machine}/launch", body, timeout=60)
    except gateway.GatewayError as err:
        _fail(str(err))

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    if isinstance(payload, dict) and payload.get("error"):
        _fail(str(payload["error"]))

    console.print(f"Started {app or path} on {machine}.")


def restart_request(machine: str, reason: str, director_id: Optional[str], json_output: bool) -> None:
    """Ask for a Director restart. The machine scrutinises; the owner accepts once; then it runs alone.

    This is a REQUEST and nothing more: it restarts nothing and grants nothing. The Gateway first asks
    the same capability question `machine restart-capability` asks and refuses on the spot, in that
    answer's own words, when the machine cannot be restarted - so the owner is never shown an approval
    for a restart that cannot work. It is also refused while another request for that machine is
    pending, and a request nobody accepts expires after thirty minutes.

    The direct restart route stays refused to a session key. Asking is not doing.
    """
    body: Dict[str, Any] = {"reason": reason}
    if director_id:
        body["directorId"] = director_id
    try:
        payload = gateway.post_json(f"machines/{machine}/director/restart-requests", body)
    except gateway.GatewayError as err:
        _fail(str(err))
    if isinstance(payload, dict) and payload.get("code") and payload.get("error"):
        _fail(str(payload["error"]))

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    request_id = str(gateway.field(payload, "id", "Id") or "-")
    console.print(f"[green]REQUESTED[/] {machine} - request {request_id}")
    console.print(f"  {gateway.field(payload, 'title', 'Title')}")
    console.print(f"  {gateway.field(payload, 'askedBySentence', 'AskedBySentence')}")
    console.print(f"  {gateway.field(payload, 'liveSessionsSentence', 'LiveSessionsSentence')}")
    capability = payload.get("capability") if isinstance(payload, dict) else None
    if isinstance(capability, dict):
        console.print(f"  {gateway.field(capability, 'reason', 'Reason')}")
        console.print(f"  {gateway.field(capability, 'guardedRestartReason', 'GuardedRestartReason')}")
    console.print(f"  Expires at {gateway.field(payload, 'expiresAtUtc', 'ExpiresAtUtc')} UTC unless the owner accepts.")
    console.print(f"  Read it back with: cc-devthrottle machine restart-request-status {machine} {request_id}")


def restart_request_status(machine: str, request_id: str, json_output: bool) -> None:
    """Where one restart request stands: pending, accepted and running, declined, expired, abandoned
    with the Director's reason, or completed."""
    payload: Dict[str, Any] = _call(f"machines/{machine}/director/restart-requests/{request_id}") or {}
    if json_output:
        print(json.dumps(payload, indent=2))
        return
    state = str(gateway.field(payload, "state", "State") or "-")
    console.print(f"{state.upper()} {machine} - request {request_id}")
    for key in ("title", "askedBySentence", "liveSessionsSentence", "stateReason", "progress"):
        value = gateway.field(payload, key)
        if value:
            console.print(f"  {value}")
    workspace = gateway.field(payload, "workspaceId", "WorkspaceId")
    if workspace:
        console.print(f"  Record: workspace {workspace}")
