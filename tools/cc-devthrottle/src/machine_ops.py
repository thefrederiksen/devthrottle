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
import urllib.parse
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

from rich.console import Console

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output  # noqa: E402
from cc_shared import gateway  # noqa: E402
from . import axi_cli  # noqa: E402

from . import usage_errors  # noqa: E402

# soft_wrap: a sentence is never broken across lines at the console width. An id or a session name
# split in two cannot be read back or pasted; tables still fit their columns.
console = Console(soft_wrap=True)

#: The next step when a call about one machine failed: see which machines exist and are online.
_CHECK_MACHINE = "cc-devthrottle machine list"


def _fail(message: str, next_commands: Sequence[str] = (_CHECK_MACHINE,)) -> None:
    axi_cli.fail(message, next_commands)


def _call(path: str, what: str) -> Dict[str, Any]:
    """One machine question. The answer must be an object; anything else is refused, never read as empty."""
    try:
        payload = gateway.get_json(path)
    except gateway.GatewayError as err:
        _fail(f"could not {what}: {err}")
    if isinstance(payload, dict) and payload.get("error"):
        _fail(f"could not {what}: {payload['error']}")
    if not isinstance(payload, dict):
        shown = "nothing" if payload is None else f"a {type(payload).__name__}"
        _fail(f"could not {what}: the Gateway's answer was not an object (got {shown}).", [axi_cli.CHECK_GATEWAY])
    return payload


def _machine_path(machine: str, rest: str, query: Optional[Dict[str, Any]] = None) -> str:
    """A path under machines/<machine>/, with the machine kept as ONE path segment and the query encoded.

    Both are values the caller typed: a space, `&` or `#` in them would otherwise change what is asked."""
    if not machine.strip():
        axi_cli.usage_error("the machine name is blank. Pass a machine name from: cc-devthrottle machine list")
    path = f"machines/{gateway.path_segment(machine)}/{rest}"
    if query:
        path += "?" + urllib.parse.urlencode(query, quote_via=urllib.parse.quote)
    return path


def _require_positive(flag: str, value: int, example: str) -> None:
    if value < 1:
        axi_cli.usage_error(f"{flag} must be at least 1, not {value}. Pass a whole number, for example {example}.")


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


_usage_error = usage_errors.usage_error


def _answer_error(message: str) -> None:
    """The Gateway's answer cannot be listed truthfully. Exit 1; --json never prints half an answer."""
    axi_cli.fail(message, [axi_cli.CHECK_GATEWAY])


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
        listed = ", ".join("'" + axi_output.escape_ascii(name) + "'" for name in unknown)
        _usage_error(f"unknown --state value {listed}. Valid states: {', '.join(valid)}")
    return names


def _check_usage(json_output: bool, fields: Optional[str], valid: Tuple[str, ...],
                 default: Tuple[str, ...]) -> List[str]:
    # Usage errors come before the fetch: a bad flag is the caller's to fix, whatever the fleet holds.
    if json_output and fields is not None:
        _usage_error(axi_cli.FIELDS_WITH_JSON)
    return usage_errors.parse_fields(fields, valid, default)


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


# --- machine apps and machine files (AXI standard, docs/axi-standard.md; issue #2922) ---
#
# Rendered through the shared output helper as `session list` is: a count line, the list with full names
# and full paths - a path is how a launch names the application, so it is never cut or wrapped - and
# help[] lines. `--json` prints the Gateway's answer unchanged.

APPS_FIELDS = ("name", "source", "path")
APPS_DEFAULT_FIELDS = APPS_FIELDS

FILES_FIELDS = ("name", "size", "modified", "path")
FILES_DEFAULT_FIELDS = FILES_FIELDS

# Each field the plain output reads, and the kind the launcher's DTO sends it as (MachineQueryContracts.cs).
_APP_DISPLAYED = (
    ("name", "Name", _STRING),
    ("source", "Source", _STRING),
    ("path", "Path", _STRING),
)
_FILE_DISPLAYED = (
    ("name", "Name", _STRING),
    ("sizeBytes", "SizeBytes", _NUMBER),
    ("modifiedUtc", "ModifiedUtc", _STRING),
    ("path", "Path", _STRING),
)


_BOOLEAN = "true or false"
_LIST = "a list"


def _kind_ok_answer(value: Any, kind: str) -> bool:
    if kind == _BOOLEAN:
        return isinstance(value, bool)
    if kind == _LIST:
        return isinstance(value, list)
    return _kind_ok(value, kind)


def _answer_rows_or_exit(rows: List[Any], what: str, machine: str,
                         spec: Tuple[Tuple[str, str, str], ...], next_commands: Sequence[str]) -> None:
    for index, row in enumerate(rows):
        if not isinstance(row, dict):
            _fail(
                f"the Gateway's {what} answer for {machine} has a row that is not an object (row {index + 1}). "
                "--json shows the raw answer.",
                next_commands,
            )
        for camel, pascal, kind in spec:
            present = camel in row or pascal in row
            value = _value(row, camel, pascal)
            if not present or not _kind_ok(value, kind):
                shown = "missing" if not present else f"{type(value).__name__} {value!r}"
                _fail(
                    f"the Gateway's {what} answer for {machine} has a row whose {camel} is "
                    f"{axi_output.escape_ascii(shown)}, not {kind} (row {index + 1}). --json shows the raw answer.",
                    next_commands,
                )


def _check_answer(payload: Dict[str, Any], what: str, machine: str,
                  spec: Tuple[Tuple[str, str, str], ...], next_commands: Sequence[str]) -> None:
    for camel, pascal, kind in spec:
        present = camel in payload or pascal in payload
        value = _value(payload, camel, pascal)
        if not present or not _kind_ok_answer(value, kind):
            shown = "missing" if not present else f"{type(value).__name__} {value!r}"
            _fail(
                f"the Gateway's {what} answer for {machine} has {camel} {axi_output.escape_ascii(shown)}, "
                f"not {kind}. --json shows the raw answer.",
                next_commands,
            )


def list_apps(machine: str, query: Optional[str], limit: int, json_output: bool,
              fields: Optional[str] = None) -> None:
    """What is installed on one machine."""
    chosen_fields = _check_usage(json_output, fields, APPS_FIELDS, APPS_DEFAULT_FIELDS)
    _require_positive("--count", limit, "--count 100")
    path = _machine_path(machine, "apps", {"q": query or "", "limit": limit})
    payload = _call(path, f"list the applications on {machine}")
    raw_next = [f"cc-devthrottle machine apps {axi_cli.bare(machine, '<machine>')} --json", axi_cli.CHECK_GATEWAY]
    apps = payload.get("apps", payload.get("Apps"))
    if not isinstance(apps, list):
        _fail(
            f"the Gateway's answer for {machine} has no list of applications, so none can be shown. --json shows the raw answer.",
            raw_next,
        )

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    _answer_rows_or_exit(apps, "applications", machine, _APP_DISPLAYED, raw_next)
    _check_answer(payload, "applications", machine, (
        ("totalMatches", "TotalMatches", _NUMBER),
        ("truncated", "Truncated", _BOOLEAN),
        ("skipped", "Skipped", _LIST),
    ), raw_next)
    total = _value(payload, "totalMatches", "TotalMatches")
    if total < len(apps):
        _fail(
            f"the Gateway's applications answer for {machine} says {total} matched but returned {len(apps)}. "
            "--json shows the raw answer.",
            raw_next,
        )
    truncated = _value(payload, "truncated", "Truncated")
    skipped = _value(payload, "skipped", "Skipped")

    records = [{
        "name": _value(app, "name", "Name"),
        "source": _value(app, "source", "Source"),
        "path": _value(app, "path", "Path"),
    } for app in apps]
    blocks = [
        # "of N total" whenever the launcher matched more than it returned, so a short list never reads
        # as the whole catalogue.
        axi_output.format_count(len(records), total=total if total != len(records) else None),
        axi_output.render_list("apps", chosen_fields, records),
    ]
    if not records:
        # "Nothing matches" is a claim about the whole machine. A search that skipped a directory or was
        # cut short did not look everywhere, so it says it was incomplete instead, and why.
        wanted = query or "(everything)"
        gaps = []
        if skipped:
            gaps.append(f"{len(skipped)} directories could not be read")
        if truncated:
            gaps.append("the launcher returned fewer results than it found")
        if gaps:
            blocks.append(_note(
                f"No application was returned for {wanted} on {machine}, but the search was incomplete "
                f"({'; '.join(gaps)}), so this does not show that nothing matches."
            ))
        else:
            blocks.append(_note(f"Nothing on {machine} matches {wanted}."))
    if truncated:
        blocks.append("More matched than were returned; narrow the search or raise --count.")
    # An unreadable directory means the catalogue is short by an unknown amount. Say so: a quietly
    # incomplete list looks exactly like a machine with less installed on it.
    if skipped:
        blocks.append(f"{len(skipped)} directories could not be read, so this list may be incomplete.")

    m = axi_cli.bare(machine, "<machine>")
    commands = []
    if records:
        commands.append(f'cc-devthrottle machine launch {m} --app "<name>"')
    if list(chosen_fields) != list(APPS_FIELDS):
        commands.append(f"cc-devthrottle machine apps {m} --fields " + ",".join(APPS_FIELDS))
    commands.append(f'cc-devthrottle machine apps {m} "<query>"')
    commands.append(f"cc-devthrottle machine apps {m} --json")
    blocks.append(axi_output.format_help(commands))
    _write(blocks)


def search_files(machine: str, query: str, limit: int, timeout_seconds: int, json_output: bool,
                 fields: Optional[str] = None) -> None:
    """Find files by name on one machine."""
    chosen_fields = _check_usage(json_output, fields, FILES_FIELDS, FILES_DEFAULT_FIELDS)
    if not query.strip():
        axi_cli.usage_error(
            "the file name to find is blank. "
            'Pass a name or a pattern: cc-devthrottle machine files <machine> "<name>"',
        )
    _require_positive("--count", limit, "--count 200")
    _require_positive("--seconds", timeout_seconds, "--seconds 20")
    path = _machine_path(
        machine, "files", {"q": query, "limit": limit, "timeoutMilliseconds": timeout_seconds * 1000}
    )
    payload = _call(path, f"search for files on {machine}")
    m = axi_cli.bare(machine, "<machine>")
    raw_next = [f'cc-devthrottle machine files {m} "<name>" --json', axi_cli.CHECK_GATEWAY]
    files = payload.get("files", payload.get("Files"))
    if not isinstance(files, list):
        _fail(
            f"the Gateway's answer for {machine} has no list of files, so no result can be shown. --json shows the raw answer.",
            [axi_cli.CHECK_GATEWAY],
        )

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    _answer_rows_or_exit(files, "file search", machine, _FILE_DISPLAYED, raw_next)
    # abandonedRoots is newer than the rest (a launcher before it does not send it), so only its kind is
    # checked when present; every other field is on every launcher that answers this query.
    _check_answer(payload, "file search", machine, (
        ("directoriesVisited", "DirectoriesVisited", _NUMBER),
        ("elapsedMilliseconds", "ElapsedMilliseconds", _NUMBER),
        ("truncated", "Truncated", _BOOLEAN),
        ("truncationReason", "TruncationReason", _STRING_OR_NULL),
        ("unreadableDirectories", "UnreadableDirectories", _NUMBER),
    ), raw_next)
    abandoned_present = "abandonedRoots" in payload or "AbandonedRoots" in payload
    if abandoned_present:
        _check_answer(payload, "file search", machine, (("abandonedRoots", "AbandonedRoots", _NUMBER),), raw_next)

    records = [{
        "name": _value(hit, "name", "Name"),
        # Bytes, as the launcher sent them: an exact number reads back exactly; "1.2M" does not.
        "size": _value(hit, "sizeBytes", "SizeBytes"),
        "modified": _value(hit, "modifiedUtc", "ModifiedUtc"),
        "path": _value(hit, "path", "Path"),
    } for hit in files]
    elapsed = _value(payload, "elapsedMilliseconds", "ElapsedMilliseconds")
    visited = _value(payload, "directoriesVisited", "DirectoriesVisited")
    blocks = [
        axi_output.format_count(len(records)),
        _note(f"Searched {visited} directories on {machine} in {elapsed} ms."),
        axi_output.render_list("files", chosen_fields, records),
    ]
    truncated = _value(payload, "truncated", "Truncated")
    unreadable = _value(payload, "unreadableDirectories", "UnreadableDirectories")
    abandoned = _value(payload, "abandonedRoots", "AbandonedRoots") if abandoned_present else 0
    if not records:
        # "No file matches" is a claim about the whole machine. A search that skipped, lost or stopped
        # short of part of it did not establish that, so it says it was incomplete instead, and why.
        gaps = []
        if truncated:
            gaps.append(f"it stopped early ({_value(payload, 'truncationReason', 'TruncationReason') or 'unknown'})")
        if unreadable:
            gaps.append(f"{unreadable} directories could not be read")
        if abandoned:
            gaps.append(f"{abandoned} search roots never answered")
        if gaps:
            blocks.append(_note(
                f"No file was found for {query} on {machine}, but the search was incomplete "
                f"({'; '.join(gaps)}), so this does not show that no file matches."
            ))
        else:
            blocks.append(_note(f"No file on {machine} matches {query}."))

    # The whole point of the truncation fields: a partial answer must never read as a complete one, and the
    # advice differs by reason - a ceiling wants a narrower search, a deadline wants more time.
    if truncated:
        reason = _value(payload, "truncationReason", "TruncationReason") or "unknown"
        if reason == "limit":
            blocks.append("Stopped at the result limit - this is NOT the whole answer. Narrow the search or raise --count.")
        elif reason == "timeout":
            blocks.append("Stopped at the time limit - this is NOT the whole answer. Narrow the search or raise --seconds.")
        else:
            blocks.append(_note(f"Stopped early ({reason}) - this is NOT the whole answer."))

    if unreadable:
        blocks.append(f"{unreadable} directories could not be read and were not searched.")
    if abandoned:
        # A root that never answered is silent, not forbidden (on macOS, a privacy-protected folder), so
        # this count is the only sign its contents are missing.
        blocks.append(
            f"Search roots given up on because they never answered: {abandoned}. Their files are missing "
            "from this answer."
        )

    commands = []
    if list(chosen_fields) != list(FILES_FIELDS):
        commands.append(f'cc-devthrottle machine files {m} "<name>" --fields ' + ",".join(FILES_FIELDS))
    commands.append(f'cc-devthrottle machine files {m} "<name>" --seconds <seconds>')
    commands.append(f'cc-devthrottle machine files {m} "<name>" --json')
    blocks.append(axi_output.format_help(commands))
    _write(blocks)


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
    payload = _call(_machine_path(machine, "restart-capability"), f"ask whether {machine} can restart its Director")

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
    console.print(f"{headline[0]}{headline[1]}[/] {axi_cli.shown(machine)}")
    console.print(f"  {axi_cli.shown(reason)}")
    console.print("")

    # One fact per line, "name: value", never a table: a long root key or command list wrapped inside a
    # table cell cannot be read back or pasted.
    facts: List[Tuple[str, str]] = []
    facts.append(("launcher version", str(gateway.field(payload, "launcherVersion", "LauncherVersion") or "-")))
    facts.append(("reach", str(gateway.field(payload, "reach", "Reach") or "-")))
    facts.append(("declaration", str(gateway.field(payload, "declaration", "Declaration") or "-")))
    facts.append(("restart signal", str(gateway.field(payload, "restartSignal", "RestartSignal") or "-")))
    root_key = gateway.field(payload, "servingRootKey", "ServingRootKey")
    facts.append(("serving root key", str(root_key or "(not declared)")))
    # RAW values, not gateway.field: that helper turns every value into text, so false became the
    # string "False" - which is truthy, and printed "YES - this is the fault" for a healthy machine -
    # and a list became its printed form, which was then joined one character at a time.
    instance_home = _value(payload, "servingRootIsInstanceHome", "ServingRootIsInstanceHome")
    if instance_home is None:
        home_text = "(not declared)"
    elif isinstance(instance_home, bool):
        home_text = "YES - this is the fault" if instance_home else "no"
    else:
        _fail(
            f"the Gateway's servingRootIsInstanceHome for {machine} is {instance_home!r}, not true, false or null. --json shows the raw answer.",
            [f"cc-devthrottle machine restart-capability {axi_cli.bare(machine, '<machine>')} --json", axi_cli.CHECK_GATEWAY],
        )
    facts.append(("serving an instance home", home_text))
    declared = _value(payload, "declaredCommands", "DeclaredCommands")
    if declared is None:
        declared = []
    if not isinstance(declared, list):
        _fail(
            f"the Gateway's declaredCommands for {machine} is not a list. --json shows the raw answer.",
            [f"cc-devthrottle machine restart-capability {axi_cli.bare(machine, '<machine>')} --json", axi_cli.CHECK_GATEWAY],
        )
    facts.append(("declares", ", ".join(str(d) for d in declared) if declared else "(nothing)"))
    facts.append(("seconds since heartbeat", str(gateway.field(payload, "quietForSeconds", "QuietForSeconds") or 0)))
    for fact, value in facts:
        console.print(f"  {fact}: {axi_cli.shown(value)}")

    # The guarded restart is printed as its own line and never folded into the verdict above. A
    # machine can be perfectly restartable and offer no guard - which is what every launcher built
    # before the guard existed looks like - so one sentence cannot carry both answers.
    console.print("")
    console.print(f"Guarded restart (refuse while sessions are live): {axi_cli.shown(guard)}")
    console.print(f"  {axi_cli.shown(guard_reason)}")


def launch(machine: str, app: Optional[str], path: Optional[str], args: Optional[str],
           cwd: Optional[str], headless: bool, json_output: bool) -> None:
    """Start an application on one machine, by catalogue name or by absolute path."""
    if not app and not path:
        axi_cli.usage_error(
            "nothing to start was named. "
            'Pass --app "<name>" (see cc-devthrottle machine apps <machine>) or --path <absolute-path>.',
        )
    if app and path:
        axi_cli.usage_error(
            "--app and --path cannot be used together: each names what to start. "
            "Drop one of them.",
        )

    # confirmProtected carries this command's explicit intent through the relay: the Gateway refuses any
    # launch without it (tenant-boundary hardening, CR-5). Typing `machine launch` IS the confirmation -
    # the flag exists to stop programs being started as a side effect of something else.
    body = {"app": app, "path": path, "args": args, "cwd": cwd, "headless": headless,
            "confirmProtected": True}
    what = app or path
    try:
        payload = gateway.post_json(_machine_path(machine, "launch"), body, timeout=60)
    except gateway.GatewayError as err:
        _fail(f"{what} was not started on {machine}: {err}")

    # An error answer is an error with or without --json: it exits non-zero and goes to standard error,
    # where it used to be printed as JSON with exit 0 and read as a success.
    if isinstance(payload, dict) and payload.get("error"):
        _fail(
            f"{what} was not started on {machine}: {payload['error']}",
            [f'cc-devthrottle machine apps {axi_cli.bare(machine, "<machine>")} "<query>"'],
        )
    if not isinstance(payload, dict):
        _fail(
            f"the Gateway's answer to starting {what} on {machine} was not an object, so whether it started is unknown. Check before starting it again.",
            [f"cc-devthrottle machine apps {axi_cli.bare(machine, '<machine>')}"],
        )

    # The Gateway relays the launch and answers with the launcher's own status (a RelayResult). That
    # status is the only word that the launcher took the request: an answer without a success status -
    # {} included - cannot be reported as started.
    axi_cli.confirmed(
        payload, ("relayStatus", "RelayStatus"), f"starting {what} on {machine}",
        [f"cc-devthrottle machine apps {axi_cli.bare(machine, '<machine>')}"],
        accept=lambda v: isinstance(v, int) and not isinstance(v, bool) and 200 <= v < 300,
    )

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    console.print(f"Started {axi_cli.shown(what)} on {axi_cli.shown(machine)}.")
    axi_cli.print_next([
        f'cc-devthrottle machine apps {axi_cli.bare(machine, "<machine>")} "<query>"',
        f'cc-devthrottle machine files {axi_cli.bare(machine, "<machine>")} "<name>"',
    ])


def restart_request(machine: str, reason: str, director_id: Optional[str], json_output: bool) -> None:
    """Ask for a Director restart. The machine scrutinises; the owner accepts once; then it runs alone.

    This is a REQUEST and nothing more: it restarts nothing and grants nothing. The Gateway first asks
    the same capability question `machine restart-capability` asks and refuses on the spot, in that
    answer's own words, when the machine cannot be restarted - so the owner is never shown an approval
    for a restart that cannot work. It is also refused while another request for that machine is
    pending, and a request nobody accepts expires after thirty minutes.

    The direct restart route stays refused to a session key. Asking is not doing.
    """
    if not reason.strip():
        axi_cli.usage_error(
            "the reason is blank. The owner decides on this sentence. "
            f'Pass it: cc-devthrottle machine restart-request {machine} --reason "<why>"',
        )
    body: Dict[str, Any] = {"reason": reason}
    if director_id:
        body["directorId"] = director_id
    try:
        payload = gateway.post_json(_machine_path(machine, "director/restart-requests"), body)
    except gateway.GatewayError as err:
        _fail(
            f"no restart was requested for {machine}: {err}",
            [f"cc-devthrottle machine restart-capability {axi_cli.bare(machine, '<machine>')}"],
        )
    if isinstance(payload, dict) and payload.get("error"):
        _fail(
            f"no restart was requested for {machine}: {payload['error']}",
            [f"cc-devthrottle machine restart-capability {axi_cli.bare(machine, '<machine>')}"],
        )
    request_id = gateway.field(payload, "id", "Id") if isinstance(payload, dict) else ""
    if not request_id:
        _fail(
            f"the Gateway's answer for {machine} carried no request id, so whether a request exists is unknown. Ask again only after checking.",
            [f"cc-devthrottle machine restart-capability {axi_cli.bare(machine, '<machine>')}"],
        )

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    console.print(f"[green]REQUESTED[/] {axi_cli.shown(machine)} - request {request_id}")
    for sentence in (
        gateway.field(payload, "title", "Title"),
        gateway.field(payload, "askedBySentence", "AskedBySentence"),
        gateway.field(payload, "liveSessionsSentence", "LiveSessionsSentence"),
    ):
        console.print(f"  {axi_cli.shown(sentence)}")
    capability = payload.get("capability")
    if isinstance(capability, dict):
        console.print(f"  {axi_cli.shown(gateway.field(capability, 'reason', 'Reason'))}")
        console.print(f"  {axi_cli.shown(gateway.field(capability, 'guardedRestartReason', 'GuardedRestartReason'))}")
    console.print(
        f"  Expires at {axi_cli.shown(gateway.field(payload, 'expiresAtUtc', 'ExpiresAtUtc'))} UTC unless the owner accepts."
    )
    axi_cli.print_next([
        f"cc-devthrottle machine restart-request-status {axi_cli.bare(machine, '<machine>')} {axi_cli.bare(request_id, '<request-id>')}",
        f"cc-devthrottle director list --machine {axi_cli.bare(machine, '<machine>')}",
    ])


def restart_request_status(machine: str, request_id: str, json_output: bool) -> None:
    """Where one restart request stands: pending, accepted and running, declined, expired, abandoned
    with the Director's reason, or completed."""
    if not request_id.strip():
        axi_cli.usage_error(
            "the request id is blank. "
            "Pass the id that cc-devthrottle machine restart-request printed.",
        )
    payload = _call(
        _machine_path(machine, f"director/restart-requests/{gateway.path_segment(request_id)}"),
        f"read restart request {request_id} for {machine}",
    )
    if json_output:
        print(json.dumps(payload, indent=2))
        return
    state = gateway.field(payload, "state", "State")
    if not state:
        _fail(
            f"the Gateway's answer for restart request {request_id} carried no state. --json shows the raw answer.",
            [axi_cli.CHECK_GATEWAY],
        )
    console.print(f"{axi_cli.shown(state.upper())} {axi_cli.shown(machine)} - request {axi_cli.shown(request_id)}")
    for key in ("title", "askedBySentence", "liveSessionsSentence", "stateReason", "progress"):
        value = gateway.field(payload, key)
        if value:
            console.print(f"  {axi_cli.shown(value)}")
    workspace = gateway.field(payload, "workspaceId", "WorkspaceId")
    if workspace:
        console.print(f"  Record: workspace {axi_cli.shown(workspace)}")


# --- Restoring a drained fleet (the Message Load mission, slice 6) ------------------------------------

#: How often the restore's progress is read back from the workspace while waiting.
RESTORE_POLL_SECONDS = 3.0

#: The placeholder the drain writes where the NEW Director's id goes. Passing it verbatim is a mistake.
NEW_DIRECTOR_PLACEHOLDER = "<the NEW director id>"


def _seat_attempt(seat: Dict[str, Any]) -> Tuple[str, str, str]:
    """(restoredSessionId, failure, attemptedAtUtc) for one workspace seat, each "" when absent."""
    restore = seat.get("restore") if isinstance(seat.get("restore"), dict) else {}
    return (
        gateway.field(seat, "restoredSessionId", "RestoredSessionId"),
        gateway.field(restore, "failure", "Failure"),
        gateway.field(restore, "attemptedAtUtc", "AttemptedAtUtc"),
    )


def _read_workspace(workspace: str) -> Dict[str, str]:
    """Map captured session id -> the seat's attempt stamp, for telling a NEW attempt from an old one."""
    doc = gateway.get_json(f"gateway/workspaces/{gateway.path_segment(workspace)}")
    if not isinstance(doc, dict) or not isinstance(doc.get("seats"), list):
        raise gateway.GatewayError(f"the Gateway's answer for workspace {workspace} carried no list of seats.")
    return {str(s.get("sessionId", "")).lower(): _seat_attempt(s)[2] for s in doc["seats"] if isinstance(s, dict)}


def _parse_seeds(seeds: Sequence[str]) -> Dict[str, str]:
    parsed: Dict[str, str] = {}
    for entry in seeds:
        seat, sep, path = entry.partition("=")
        if not sep or not seat.strip() or not path.strip():
            axi_cli.usage_error(
                f"--seed '{entry}' is not <captured session id>=<path>. "
                "Example: --seed 8f894218-0000-4000-8000-000000000000=/data/handovers/SEED-8f894218.md"
            )
        parsed[seat.strip()] = path.strip()
    return parsed


def restore_workspace(
    workspace: str,
    director_id: str,
    seats: Sequence[str],
    seeds: Sequence[str],
    wait_seconds: int,
    json_output: bool,
) -> None:
    """Ask a Director to bring a drained fleet back, and report each seat.

    The DIRECTOR starts every seat, on its own credential, naming the owner each seat had - read from the
    seat facts the Gateway captured. An owner restarted in the same drain is started first and named by its
    new id. This command names no owner and cannot: a session key may name only itself or the user as the
    owner of what it starts, which is why the restore moved to the Director.

    Each seat's result is written on the workspace by the Director as it happens; this command reads it back
    until every seat asked for has an answer or the wait runs out. Exit 0 only when every seat came back.
    """
    if not director_id.strip() or director_id.strip() == NEW_DIRECTOR_PLACEHOLDER:
        axi_cli.usage_error(
            "--director must be the id of the Director that brings the seats back - after a restart, the NEW one. "
            "Find it with: cc-devthrottle director list"
        )
    if wait_seconds < 0:
        axi_cli.usage_error("--wait-seconds cannot be negative. Pass 0 to ask and not wait.")
    director_id = director_id.strip()
    next_commands = [
        "cc-devthrottle director list --machine <machine>",
        f"cc-devthrottle director restore {axi_cli.bare(workspace, '<workspace>')} --director <id>",
    ]

    body: Dict[str, Any] = {"directorId": director_id}
    if seats:
        body["seats"] = list(seats)
    parsed_seeds = _parse_seeds(seeds)
    if parsed_seeds:
        body["seeds"] = parsed_seeds

    try:
        before = _read_workspace(workspace) if wait_seconds > 0 else {}
        accepted = gateway.post_json(f"gateway/workspaces/{gateway.path_segment(workspace)}/restore", body)
    except gateway.GatewayError as err:
        _fail(f"nothing was restored from workspace {workspace}: {err}", next_commands)
    if not isinstance(accepted, dict) or accepted.get("taken") is not True:
        reason = accepted.get("error") if isinstance(accepted, dict) else None
        _fail(f"nothing was restored from workspace {workspace}: "
              f"{reason or 'the Gateway did not say the Director took the restore'}", next_commands)
    asked = [str(s) for s in accepted.get("seats") or []]
    again = (f"cc-devthrottle director restore {axi_cli.bare(workspace, '<workspace>')} "
             f"--director {director_id}   # brings back only what has not come back; refused while one runs")

    if wait_seconds == 0:
        if json_output:
            print(json.dumps({"workspaceId": workspace, "directorId": director_id, "taken": True,
                              "count": len(asked), "seats": asked}, indent=2))
            return
        console.print(f"[green]TAKEN[/] workspace {axi_cli.shown(workspace)} by Director {axi_cli.shown(director_id)}: "
                      f"count: {len(asked)}")
        for sid in asked:
            console.print(f"  {sid}")
        console.print("  Not waiting. Each seat's result is written on the workspace as the Director gets to it.")
        axi_cli.print_next([again])
        return

    outcomes: Dict[str, Tuple[str, str, str]] = {}
    names: Dict[str, str] = {}
    deadline = _monotonic() + wait_seconds
    while True:
        try:
            doc = gateway.get_json(f"gateway/workspaces/{gateway.path_segment(workspace)}")
        except gateway.GatewayError as err:
            _fail(f"the restore was taken, but its progress could not be read: {err}", [again])
        by_id = {str(s.get("sessionId", "")).lower(): s for s in (doc.get("seats") or []) if isinstance(s, dict)}
        for sid in asked:
            seat = by_id.get(sid.lower(), {})
            names[sid] = gateway.field(seat, "name", "Name") or sid
            restored, failure, attempted = _seat_attempt(seat)
            fresh = attempted and attempted != before.get(sid.lower(), "")
            if restored:
                outcomes[sid] = ("restored", restored, "")
            elif failure and fresh:
                outcomes[sid] = ("failed", "", failure)
            else:
                outcomes[sid] = ("pending", "", "")
        if all(o[0] != "pending" for o in outcomes.values()) or _monotonic() >= deadline:
            break
        _sleep(RESTORE_POLL_SECONDS)

    rows = [
        {
            "sessionId": sid,
            "name": names.get(sid, sid),
            "outcome": outcomes[sid][0],
            "restoredSessionId": outcomes[sid][1] or None,
            "failure": outcomes[sid][2] or None,
        }
        for sid in asked
    ]
    ok = all(r["outcome"] == "restored" for r in rows)
    if json_output:
        print(json.dumps({"workspaceId": workspace, "directorId": director_id, "count": len(rows), "seats": rows}, indent=2))
    else:
        console.print(f"Workspace {axi_cli.shown(workspace)} onto Director {axi_cli.shown(director_id)}: "
                      f"count: {len(rows)}")
        for r in rows:
            if r["outcome"] == "restored":
                console.print(f"  [green]RESTORED[/] {axi_cli.shown(r['name'])} {r['sessionId']} -> {r['restoredSessionId']}")
            elif r["outcome"] == "failed":
                console.print(f"  [red]FAILED[/] {axi_cli.shown(r['name'])} {r['sessionId']}: {axi_cli.shown(r['failure'])}")
            else:
                console.print(f"  [yellow]PENDING[/] {axi_cli.shown(r['name'])} {r['sessionId']}: "
                              "no answer yet; the Director is still working or the wait ran out")
        axi_cli.print_next([
            "cc-devthrottle session list --json   # read promptDeliveryUnresolved on each restored seat",
            again,
        ])
    if not ok:
        raise SystemExit(1)


def _monotonic() -> float:
    import time
    return time.monotonic()


def _sleep(seconds: float) -> None:
    import time
    time.sleep(seconds)
