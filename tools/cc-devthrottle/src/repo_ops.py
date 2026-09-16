"""Repository and worktree listing - the fleet fact any agent can ask for in one call.

Backed by the Gateway's /repositories and /worktrees, which aggregate every machine in the account.
Read-only - reaping always runs on the owning Director with a live re-verify.

Remove-the-network-port mission, phase 2: this used to go through the Director's /fleet/* relay on the
same machine, which forwarded here. The middleman is gone; there is no standalone answer any more,
because there is no local path - a machine with no Gateway has no fleet tooling, which is the cost the
mission accepts and the error message names.

Both lists follow the AXI standard (docs/axi-standard.md, issue #2922) and render the way
`session list` does: a count line with a breakdown, a list whose names and paths are never shortened,
`help[]` next commands, and filters that narrow `--json` without changing its shape.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

import typer

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output  # noqa: E402
from cc_shared import gateway  # noqa: E402

# --- repo list ---

# The two repository states, in the order the count line lists them. They name the Gateway's isClean
# verbatim; nothing else is read into them.
REPO_STATES = ("dirty", "clean")

REPO_LIST_FIELDS = (
    "name", "path", "machine", "state", "branch", "uncommitted", "ahead", "behind", "behind-main",
    "worktrees", "safe-to-reap", "worktree-bytes", "provider", "org", "remote", "director", "provisional",
)
REPO_LIST_DEFAULT_FIELDS = ("name", "path", "machine", "state")

# --- worktree list ---

# The Gateway's folded worktree states (FleetWorktreeFold / RepositoryDtoMapper), rendered verbatim, in
# the order the count line lists them. "verifying" is what a still-verifying repository's worktrees are
# served as.
WORKTREE_STATES = ("needs-attention", "in-use", "safe-to-reap", "verifying")

WORKTREE_LIST_FIELDS = (
    "path", "repo", "machine", "state", "branch", "reason", "sessions", "bytes", "last-activity",
    "repo-path", "director", "data-age", "provisional",
)
WORKTREE_LIST_DEFAULT_FIELDS = ("path", "repo", "machine", "state")

# A worktree can hold several sessions; the list format has one value per field, so their names are
# joined with this. --json carries the exact list.
SESSION_NAME_SEPARATOR = "; "

# The Gateway leaves out a Director whose report is not fresh, without saying so. An empty answer is a
# negative answer, so it carries that qualification.
_STALE_DIRECTORS_CAUTION = (
    "Only Directors with a recent report are included, so a machine whose Director is not reporting "
    "is missing from this answer."
)


class GatewayShapeError(Exception):
    """The Gateway's answer is not the shape this tool reads. Never guessed around."""


# ---------------------------------------------------------------------------------------------------
# Shared plumbing
# ---------------------------------------------------------------------------------------------------


def _fail(message: str) -> None:
    print(_ascii_text(f"Error: {message}"), file=sys.stderr)
    raise typer.Exit(1)


def _usage_error(message: str) -> None:
    print(_ascii_text(f"Error: {message}"), file=sys.stderr)
    raise typer.Exit(axi_output.USAGE_ERROR_EXIT_CODE)


def _ascii_text(block: str) -> str:
    """The rendered blocks are ASCII already; a sentence carrying a Gateway value may not be."""
    return block if block.isascii() else axi_output.escape_ascii(block)


def _get(path: str, noun: str) -> List[Dict[str, Any]]:
    """Fetch a list from the Gateway, or print why not and exit 1.

    Absent is not empty: an answer that is not a list of objects is refused, never read as "none".
    """
    try:
        rows = gateway.get_json(path)
    except gateway.GatewayError as err:
        _fail(str(err))
    if isinstance(rows, dict) and rows.get("error"):
        _fail(str(rows["error"]))
    if not isinstance(rows, list):
        _fail(
            f"the Gateway answered /{path} with no list of {noun} "
            f"(it sent {type(rows).__name__}). This tool will not read that as an empty list."
        )
    for index, row in enumerate(rows):
        if not isinstance(row, dict):
            _fail(f"the Gateway's list of {noun} has a row that is not an object (row {index + 1}).")
    return rows


def _raw(dto: Dict[str, Any], key: str) -> Any:
    """The RAW value for a camelCase key, or its PascalCase twin (gateway.field stringifies - wrong
    for booleans, numbers and lists)."""
    if key in dto:
        return dto[key]
    return dto.get(key[0].upper() + key[1:])


def _text(dto: Dict[str, Any], key: str, what: str, index: int) -> str:
    """A string the list cannot do without (an identity). Missing, blank or not a string fails."""
    value = _raw(dto, key)
    if not isinstance(value, str) or not value.strip():
        shown = "missing" if value is None else repr(value)
        raise GatewayShapeError(f"row {index + 1} has no {what} ({key} is {axi_output.escape_ascii(shown)}).")
    return value


def _optional_text(dto: Dict[str, Any], key: str) -> Optional[str]:
    value = _raw(dto, key)
    if value is None:
        return None
    if not isinstance(value, str):
        raise GatewayShapeError(f"{key} should be text but is {axi_output.escape_ascii(repr(value))}.")
    return value


def _number(dto: Dict[str, Any], key: str) -> Optional[object]:
    """A number as the Gateway sent it; None only when it sent none. Anything else fails."""
    value = _raw(dto, key)
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise GatewayShapeError(f"{key} should be a number but is {axi_output.escape_ascii(repr(value))}.")
    return value


def _flag(dto: Dict[str, Any], key: str) -> Optional[bool]:
    value = _raw(dto, key)
    if value is None:
        return None
    if not isinstance(value, bool):
        raise GatewayShapeError(f"{key} should be true or false but is {axi_output.escape_ascii(repr(value))}.")
    return value


def _norm_path(path: str) -> str:
    return path.replace("\\", "/").rstrip("/").lower()


def _parse_states(requested: Optional[str], valid: Sequence[str]) -> Optional[List[str]]:
    """Turn a `--state` value (one state, or several separated by commas; case ignored) into a list,
    or exit 2 listing the valid states."""
    if requested is None:
        return None
    names = [part.strip().lower() for part in requested.split(",")]
    unknown = [name for name in names if name not in valid]
    if unknown:
        listed = ", ".join(repr(axi_output.escape_ascii(name)) for name in unknown)
        _usage_error(f"unknown --state value {listed}. Valid states: {', '.join(valid)}")
    return names


def _check_flags(json_output: bool, fields: Optional[str], valued: Sequence[Tuple[str, Optional[str]]]) -> None:
    if json_output and fields is not None:
        _usage_error("--fields does not apply to --json, which always carries every field. Drop one of them.")
    for flag, value in valued:
        if value is not None and not value.strip():
            _usage_error(f"{flag} needs a value.")


def _size_text(size: int) -> str:
    if size >= 1_073_741_824:
        return f"{size / 1_073_741_824:.1f} GB"
    if size >= 1_048_576:
        return f"{size / 1_048_576:.0f} MB"
    return f"{size / 1024:.0f} KB" if size > 0 else "0 KB"


def _breakdown(states: Sequence[str], order: Sequence[str]) -> Optional[List[Tuple[str, int]]]:
    # No rows means no breakdown at all: the helper refuses an empty one, and "count: 0" says it all.
    return [(name, n) for name in order if (n := sum(1 for st in states if st == name))] or None


def _repeats_line(
    rows: Sequence[Tuple[int, Dict[str, Any], str]], noun: str, chosen_fields: Sequence[str]
) -> Optional[str]:
    """Two Directors on one machine each report what they see, so the same path can be listed twice.
    Nothing is merged or dropped - that would be this tool deciding which report is true - but the
    reader is told, because the default fields cannot tell the rows apart."""
    seen = set()
    repeats = 0
    for _, row, _ in rows:
        key = (str(_raw(row, "machineName")).lower(), _norm_path(str(_raw(row, "path"))))
        if key in seen:
            repeats += 1
        seen.add(key)
    if not repeats:
        return None
    line = (
        f"repeated: {repeats} of these rows name a {noun} path already listed on the same machine, "
        "reported by another Director there."
    )
    if "director" not in chosen_fields:
        line += " Add director to --fields to tell them apart."
    return line


def _record(fields: Sequence[str], getters: Dict[str, Callable[[], object]]) -> Dict[str, object]:
    """Only the chosen fields are read, so a field nobody asked for cannot fail the list."""
    return {field: getters[field]() for field in fields}


# ---------------------------------------------------------------------------------------------------
# repo list
# ---------------------------------------------------------------------------------------------------


def repo_state(r: Dict[str, Any], index: int = 0) -> str:
    """dirty or clean, named from the Gateway's isClean. A missing or non-boolean isClean fails."""
    clean = _raw(r, "isClean")
    if clean is True:
        return "clean"
    if clean is False:
        return "dirty"
    shown = "missing" if clean is None else repr(clean)
    name = _raw(r, "name")
    raise GatewayShapeError(
        f"repository {axi_output.escape_ascii(repr(name))} (row {index + 1}) has an isClean that is "
        f"{axi_output.escape_ascii(shown)}; this tool reads only true or false."
    )


def _repo_record(r: Dict[str, Any], state: str, fields: Sequence[str], index: int) -> Dict[str, object]:
    getters: Dict[str, Callable[[], object]] = {
        "name": lambda: _text(r, "name", "repository name", index),
        "path": lambda: _text(r, "path", "repository path", index),
        "machine": lambda: _text(r, "machineName", "machine name", index),
        "state": lambda: state,
        "branch": lambda: _optional_text(r, "branch"),
        "uncommitted": lambda: _number(r, "uncommittedCount"),
        "ahead": lambda: _number(r, "aheadCount"),
        "behind": lambda: _number(r, "behindCount"),
        "behind-main": lambda: _number(r, "behindMainCount"),
        "worktrees": lambda: _number(r, "worktreeCount"),
        "safe-to-reap": lambda: _number(r, "worktreesSafeToReap"),
        "worktree-bytes": lambda: _number(r, "worktreeBytes"),
        "provider": lambda: _optional_text(r, "provider"),
        "org": lambda: _optional_text(r, "org"),
        "remote": lambda: _optional_text(r, "remoteUrl"),
        "director": lambda: _optional_text(r, "directorId"),
        "provisional": lambda: _flag(r, "provisional"),
    }
    return _record(fields, getters)


def _matches_repo(name: Any, path: Any, wanted: str) -> bool:
    """--repo matches the repository folder name or its full path, ignoring case and slash direction."""
    target = _norm_path(wanted)
    candidates = []
    if isinstance(name, str):
        candidates.append(name.strip().lower())
    if isinstance(path, str):
        candidates.append(_norm_path(path))
    return target in candidates


def _matches_machine(row: Dict[str, Any], machine: str) -> bool:
    value = _raw(row, "machineName")
    return isinstance(value, str) and value.lower() == machine.strip().lower()


def list_repositories(
    json_output: bool,
    dirty_only: bool = False,
    *,
    state: Optional[str] = None,
    repo: Optional[str] = None,
    machine: Optional[str] = None,
    fields: Optional[str] = None,
) -> None:
    """List the fleet's repositories, optionally narrowed by state, repository or machine."""
    # Usage errors come before the fetch: a bad flag is the caller's to fix, whatever the fleet holds.
    _check_flags(json_output, fields, (("--repo", repo), ("--machine", machine)))
    if dirty_only and state is not None:
        _usage_error("--dirty is the same as --state dirty. Give one of them.")
    chosen_fields = axi_output.parse_fields_or_exit(fields, REPO_LIST_FIELDS, REPO_LIST_DEFAULT_FIELDS)
    wanted_states = ["dirty"] if dirty_only else _parse_states(state, REPO_STATES)

    repos = _get("repositories", "repositories")
    filtered = wanted_states is not None or repo is not None or machine is not None

    # The state is named only where it is needed, so an unfiltered --json prints exactly what the
    # Gateway sent, as it always has.
    need_states = not json_output or wanted_states is not None
    try:
        states = [repo_state(r, i) for i, r in enumerate(repos)] if need_states else [""] * len(repos)
        rows = [
            (i, r, st)
            for i, (r, st) in enumerate(zip(repos, states))
            if (wanted_states is None or st in wanted_states)
            and (repo is None or _matches_repo(_raw(r, "name"), _raw(r, "path"), repo))
            and (machine is None or _matches_machine(r, machine))
        ]
        if json_output:
            # A filter narrows the same bare array; it never changes its shape.
            print(json.dumps([r for _, r, _ in rows] if filtered else repos, indent=2))
            if not rows:
                print(f"WARNING: {_STALE_DIRECTORS_CAUTION}", file=sys.stderr)
            return
        records = [_repo_record(r, st, chosen_fields, i) for i, r, st in rows]
        # Every row shown must be nameable, whatever fields were asked for.
        for i, r, _ in rows:
            _text(r, "name", "repository name", i)
            _text(r, "path", "repository path", i)
        repeats = _repeats_line(rows, "repository", chosen_fields)
        safe = [_number(r, "worktreesSafeToReap") for _, r, _ in rows]
        worktrees = [_number(r, "worktreeCount") for _, r, _ in rows]
    except GatewayShapeError as err:
        _fail(f"the Gateway's list of repositories is not readable: {err} --json shows the raw rows.")

    blocks = [
        axi_output.format_count(
            len(rows),
            total=len(repos) if filtered else None,
            breakdown=_breakdown([st for _, _, st in rows], REPO_STATES),
        ),
        axi_output.render_list("repositories", chosen_fields, records),
    ]
    if rows:
        blocks.append(
            f"worktrees in these repositories: {sum(n or 0 for n in worktrees)}, "
            f"safe to reap: {sum(n or 0 for n in safe)}"
        )
    if repeats:
        blocks.append(repeats)
    if not rows:
        if filtered and repos:
            blocks.append("No repository matches the filter.")
        else:
            blocks.append("No repositories were returned.")
        blocks.append(_STALE_DIRECTORS_CAUTION)
    blocks.append(axi_output.format_help(_repo_list_help(bool(rows), filtered, chosen_fields)))
    axi_output.write_blocks(sys.stdout, *(_ascii_text(block) for block in blocks))


def _repo_list_help(any_rows: bool, filtered: bool, chosen_fields: Sequence[str]) -> List[str]:
    """Concrete next commands. Runtime values are placeholders, never guessed."""
    if not any_rows:
        if filtered:
            return ["cc-devthrottle repo list", "cc-devthrottle repo list --help"]
        return ["cc-devthrottle director list"]
    commands = []
    if not filtered:
        commands.append("cc-devthrottle repo list --state dirty")
    if list(chosen_fields) == list(REPO_LIST_DEFAULT_FIELDS):
        commands.append("cc-devthrottle repo list --fields " + ",".join(REPO_LIST_FIELDS))
    commands.append("cc-devthrottle worktree list --repo <name>")
    commands.append("cc-devthrottle repo list --json")
    return commands


# ---------------------------------------------------------------------------------------------------
# worktree list
# ---------------------------------------------------------------------------------------------------


def worktree_state(w: Dict[str, Any], index: int = 0) -> str:
    """The Gateway's folded state, verbatim. A state this tool does not know fails, naming it."""
    value = _raw(w, "state")
    if isinstance(value, str) and value in WORKTREE_STATES:
        return value
    shown = "missing" if value is None else repr(value)
    path = _raw(w, "path")
    raise GatewayShapeError(
        f"worktree {axi_output.escape_ascii(repr(path))} (row {index + 1}) has a state that is "
        f"{axi_output.escape_ascii(shown)}; this tool knows only {', '.join(WORKTREE_STATES)}. "
        "If the Gateway has added a state, update cc-devthrottle."
    )


def _session_names(w: Dict[str, Any]) -> Optional[str]:
    labels = _raw(w, "sessionLabels")
    if labels is None:
        return None
    if not isinstance(labels, list) or not all(isinstance(label, str) for label in labels):
        raise GatewayShapeError(f"sessionLabels should be a list of text but is {axi_output.escape_ascii(repr(labels))}.")
    return SESSION_NAME_SEPARATOR.join(labels) if labels else None


def _worktree_record(w: Dict[str, Any], state: str, fields: Sequence[str], index: int) -> Dict[str, object]:
    getters: Dict[str, Callable[[], object]] = {
        "path": lambda: _text(w, "path", "worktree path", index),
        "repo": lambda: _text(w, "repoName", "repository name", index),
        "machine": lambda: _text(w, "machineName", "machine name", index),
        "state": lambda: state,
        "branch": lambda: _optional_text(w, "branch"),
        "reason": lambda: _optional_text(w, "reason"),
        "sessions": lambda: _session_names(w),
        "bytes": lambda: _number(w, "sizeBytes"),
        "last-activity": lambda: _optional_text(w, "lastActivityUtc"),
        "repo-path": lambda: _optional_text(w, "repoPath"),
        "director": lambda: _optional_text(w, "directorId"),
        "data-age": lambda: _number(w, "dataAgeSeconds"),
        "provisional": lambda: _flag(w, "provisional"),
    }
    return _record(fields, getters)


def _reclaimable_line(rows: Sequence[Tuple[int, Dict[str, Any], str]]) -> Optional[str]:
    """How much disk the safe-to-reap worktrees shown would give back. A size the Gateway did not
    measure is counted as unknown, never as zero."""
    sizes = [_number(w, "sizeBytes") for _, w, st in rows if st == "safe-to-reap"]
    if not sizes:
        return None
    known = [int(size) for size in sizes if size is not None]
    line = f"reclaimable: {_size_text(sum(known))} in {len(sizes)} safe-to-reap worktrees"
    if len(known) != len(sizes):
        line += f" ({len(sizes) - len(known)} of unknown size)"
    return line + ". Reaping runs on the owning Director."


def list_worktrees(
    json_output: bool,
    repo: str | None = None,
    state: str | None = None,
    *,
    machine: Optional[str] = None,
    fields: Optional[str] = None,
) -> None:
    """List the fleet's worktrees, optionally narrowed by repository, state or machine."""
    _check_flags(json_output, fields, (("--repo", repo), ("--state", state), ("--machine", machine)))
    chosen_fields = axi_output.parse_fields_or_exit(fields, WORKTREE_LIST_FIELDS, WORKTREE_LIST_DEFAULT_FIELDS)
    wanted_states = _parse_states(state, WORKTREE_STATES)

    worktrees = _get("worktrees", "worktrees")
    filtered = wanted_states is not None or repo is not None or machine is not None

    need_states = not json_output or wanted_states is not None
    try:
        states = [worktree_state(w, i) for i, w in enumerate(worktrees)] if need_states else [""] * len(worktrees)
        rows = [
            (i, w, st)
            for i, (w, st) in enumerate(zip(worktrees, states))
            if (wanted_states is None or st in wanted_states)
            and (repo is None or _matches_repo(_raw(w, "repoName"), _raw(w, "repoPath"), repo))
            and (machine is None or _matches_machine(w, machine))
        ]
        if json_output:
            print(json.dumps([w for _, w, _ in rows] if filtered else worktrees, indent=2))
            if not rows:
                print(f"WARNING: {_STALE_DIRECTORS_CAUTION}", file=sys.stderr)
            return
        records = [_worktree_record(w, st, chosen_fields, i) for i, w, st in rows]
        for i, w, _ in rows:
            _text(w, "path", "worktree path", i)
            _text(w, "repoName", "repository name", i)
        repeats = _repeats_line(rows, "worktree", chosen_fields)
        reclaimable = _reclaimable_line(rows)
    except GatewayShapeError as err:
        _fail(f"the Gateway's list of worktrees is not readable: {err} --json shows the raw rows.")

    blocks = [
        axi_output.format_count(
            len(rows),
            total=len(worktrees) if filtered else None,
            breakdown=_breakdown([st for _, _, st in rows], WORKTREE_STATES),
        ),
        axi_output.render_list("worktrees", chosen_fields, records),
    ]
    if repeats:
        blocks.append(repeats)
    if reclaimable:
        blocks.append(reclaimable)
    if not rows:
        if filtered and worktrees:
            blocks.append("No worktree matches the filter.")
        else:
            blocks.append("No worktrees were returned.")
        blocks.append(_STALE_DIRECTORS_CAUTION)
    blocks.append(axi_output.format_help(_worktree_list_help(bool(rows), filtered, chosen_fields)))
    axi_output.write_blocks(sys.stdout, *(_ascii_text(block) for block in blocks))


def _worktree_list_help(any_rows: bool, filtered: bool, chosen_fields: Sequence[str]) -> List[str]:
    """Concrete next commands. Runtime values are placeholders, never guessed."""
    if not any_rows:
        if filtered:
            return ["cc-devthrottle worktree list", "cc-devthrottle worktree list --help"]
        return ["cc-devthrottle repo list"]
    commands = []
    if not filtered:
        commands.append("cc-devthrottle worktree list --state safe-to-reap")
    if list(chosen_fields) == list(WORKTREE_LIST_DEFAULT_FIELDS):
        commands.append("cc-devthrottle worktree list --fields " + ",".join(WORKTREE_LIST_FIELDS))
    commands.append("cc-devthrottle worktree list --json")
    commands.append("cc-devthrottle repo list")
    return commands
