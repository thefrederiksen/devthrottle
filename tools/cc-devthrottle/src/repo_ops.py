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
import math
import re
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


# A key the Gateway left out. The Gateway writes every field, nulls included, so an absent key is
# never read as null.
_ABSENT = object()


def _raw(dto: Dict[str, Any], key: str) -> Any:
    """The RAW value for a camelCase key, or its PascalCase twin (gateway.field stringifies - wrong
    for booleans, numbers and lists). _ABSENT when the Gateway sent neither."""
    if key in dto:
        return dto[key]
    return dto.get(key[0].upper() + key[1:], _ABSENT)


def _shown(value: Any) -> str:
    if value is _ABSENT:
        return "missing"
    if value is None:
        return "null"
    return axi_output.escape_ascii(repr(value))


def _wrong(index: int, key: str, expected: str, value: Any) -> GatewayShapeError:
    return GatewayShapeError(f"row {index + 1}: {key} should be {expected} but is {_shown(value)}.")


def _text(dto: Dict[str, Any], key: str, what: str, index: int) -> str:
    """A string the list cannot do without (an identity). Missing, blank or not a string fails."""
    value = _raw(dto, key)
    if not isinstance(value, str) or not value.strip():
        raise GatewayShapeError(f"row {index + 1} has no {what} ({key} is {_shown(value)}).")
    return value


def _any_text(dto: Dict[str, Any], key: str, index: int) -> str:
    """Text that is never null on the Gateway (RepoStatusDto.Branch, FleetWorktreeDto.Reason), but may
    be empty."""
    value = _raw(dto, key)
    if not isinstance(value, str):
        raise _wrong(index, key, "text", value)
    return value


def _nullable_text(dto: Dict[str, Any], key: str, index: int) -> Optional[str]:
    value = _raw(dto, key)
    if value is not None and not isinstance(value, str):
        raise _wrong(index, key, "text or null", value)
    return value


def _one_of(dto: Dict[str, Any], key: str, valid: Sequence[str], index: int) -> str:
    value = _raw(dto, key)
    if not isinstance(value, str) or value not in valid:
        raise _wrong(index, key, "one of " + ", ".join(valid), value)
    return value


def _is_whole(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _count(dto: Dict[str, Any], key: str, index: int, minimum: int = 0) -> int:
    """A whole number the Gateway always sends (an int or long, never null), at least `minimum`."""
    value = _raw(dto, key)
    if not _is_whole(value) or value < minimum:
        raise _wrong(index, key, f"a whole number of {minimum} or more", value)
    return value


def _nullable_count(dto: Dict[str, Any], key: str, index: int) -> Optional[int]:
    """A whole number of 0 or more, or null when the Director did not measure it (SizeBytes)."""
    value = _raw(dto, key)
    if value is None:
        return None
    if not _is_whole(value) or value < 0:
        raise _wrong(index, key, "a number of 0 or more, or null", value)
    return value


def _seconds(dto: Dict[str, Any], key: str, index: int) -> float:
    value = _raw(dto, key)
    if (
        isinstance(value, bool)
        or not isinstance(value, (int, float))
        or not math.isfinite(value)
        or value < 0
    ):
        raise _wrong(index, key, "a number of seconds, 0 or more", value)
    return value


def _flag(dto: Dict[str, Any], key: str, index: int) -> bool:
    value = _raw(dto, key)
    if not isinstance(value, bool):
        raise _wrong(index, key, "true or false", value)
    return value


# How System.Text.Json writes a DateTime: ISO 8601, with a zone only when the value carries one.
_TIMESTAMP = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?$")


def _nullable_timestamp(dto: Dict[str, Any], key: str, index: int) -> Optional[str]:
    value = _raw(dto, key)
    if value is None:
        return None
    if not isinstance(value, str) or not _TIMESTAMP.match(value):
        raise _wrong(index, key, "a date and time or null", value)
    return value


def _labels(dto: Dict[str, Any], key: str, index: int) -> List[str]:
    """A list of text the Gateway always sends (empty when there is none)."""
    value = _raw(dto, key)
    if not isinstance(value, list) or not all(isinstance(label, str) for label in value):
        raise _wrong(index, key, "a list of text", value)
    return value


# A path is Windows-shaped ONLY when it starts with a drive letter, a colon and a slash of either
# kind (C:\ or C:/), or with two backslashes (a network share). A backslash anywhere else is an
# ordinary filename character on macOS and Linux, so such a path is not Windows-shaped and matches
# exactly.
_WINDOWS_START = re.compile(r"^([A-Za-z]:[\\/]|\\\\)")


def is_windows_path(path: str) -> bool:
    return bool(_WINDOWS_START.match(path))


def _fold_windows(path: str) -> str:
    return path.replace("\\", "/").rstrip("/").lower()


def _path_key(path: str) -> str:
    """What a path is compared by. Windows ignores case and slash direction, so a Windows path is
    folded; any other path is kept exactly, because /home/A/proj and /home/a/proj can be two different
    repositories on a filesystem that minds case."""
    return _fold_windows(path) if is_windows_path(path) else path


def path_matches(row_path: str, wanted: str) -> bool:
    """Whether a full path given to --repo names `row_path`. Shared by repo list and worktree list.

    Case and slash direction are ignored ONLY when the row's path is a Windows path; any other path
    must match exactly."""
    if is_windows_path(row_path):
        return _fold_windows(row_path) == _fold_windows(wanted)
    return row_path == wanted


def matches_repo(name: str, path: str, wanted: str) -> bool:
    """--repo names a repository by its folder name (ignoring case) or by its full path."""
    return name.strip().lower() == wanted.strip().lower() or path_matches(path, wanted)


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


def _repeats_line(rows: Sequence[Dict[str, Any]], noun: str, chosen_fields: Sequence[str]) -> Optional[str]:
    """Two Directors on one machine each report what they see, so the same path can be listed twice.
    Nothing is merged or dropped - that would be this tool deciding which report is true - but the
    reader is told, because the default fields cannot tell the rows apart."""
    seen = set()
    repeats = 0
    for row in rows:
        key = (str(row["machine"]).lower(), _path_key(str(row["path"])))
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


def _read_all(rows: Sequence[Dict[str, Any]], reader: Callable[[Dict[str, Any], int], Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Every row is read and checked BEFORE any filter runs, so a row the Gateway sent wrong fails the
    command instead of being quietly filtered out and reported as no match."""
    return [reader(row, index) for index, row in enumerate(rows)]


# ---------------------------------------------------------------------------------------------------
# repo list
# ---------------------------------------------------------------------------------------------------

# RepoProvider on the Director, sent by name.
REPO_PROVIDERS = ("None", "GitHub", "AzureDevOps", "Other")


def repo_state(r: Dict[str, Any], index: int = 0) -> str:
    """dirty or clean, named from the Gateway's isClean. A missing or non-boolean isClean fails."""
    clean = _raw(r, "isClean")
    if clean is True:
        return "clean"
    if clean is False:
        return "dirty"
    name = _raw(r, "name")
    raise GatewayShapeError(
        f"repository {_shown(name)} (row {index + 1}) has an isClean that is "
        f"{_shown(clean)}; this tool reads only true or false."
    )


def _read_repo(r: Dict[str, Any], index: int) -> Dict[str, Any]:
    """Every RepoStatusDto field this command reads, checked against what the Gateway sends, keyed by
    the --fields names. Any other value exits 1; nothing is defaulted."""
    row = {
        "name": _text(r, "name", "repository name", index),
        "path": _text(r, "path", "repository path", index),
        "machine": _text(r, "machineName", "machine name", index),
        "state": repo_state(r, index),
        "branch": _any_text(r, "branch", index),
        "uncommitted": _count(r, "uncommittedCount", index),
        "ahead": _count(r, "aheadCount", index),
        "behind": _count(r, "behindCount", index),
        # -1 is the Director's "on main already, or unknown".
        "behind-main": _count(r, "behindMainCount", index, minimum=-1),
        "worktrees": _count(r, "worktreeCount", index),
        "safe-to-reap": _count(r, "worktreesSafeToReap", index),
        "worktree-bytes": _count(r, "worktreeBytes", index),
        "provider": _one_of(r, "provider", REPO_PROVIDERS, index),
        "org": _nullable_text(r, "org", index),
        "remote": _nullable_text(r, "remoteUrl", index),
        "director": _text(r, "directorId", "Director id", index),
        "provisional": _flag(r, "provisional", index),
    }
    # The safe count is part of the worktree count (RepositoryStatusService), and a repository still
    # verifying is served with a safe count of zero (FleetWorktreeFold.FoldRepositoryForServe).
    if row["safe-to-reap"] > row["worktrees"]:
        raise GatewayShapeError(
            f"row {index + 1}: worktreesSafeToReap ({row['safe-to-reap']}) is more than "
            f"worktreeCount ({row['worktrees']})."
        )
    if row["provisional"] and row["safe-to-reap"] != 0:
        raise GatewayShapeError(
            f"row {index + 1}: a repository still verifying has worktreesSafeToReap "
            f"{row['safe-to-reap']}; the Gateway serves it as 0."
        )
    return row


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

    if json_output and not filtered:
        # Unfiltered --json reads nothing: it prints exactly what the Gateway sent, as it always has.
        print(json.dumps(repos, indent=2))
        if not repos:
            print(f"WARNING: {_STALE_DIRECTORS_CAUTION}", file=sys.stderr)
        return

    try:
        read = _read_all(repos, _read_repo)
    except GatewayShapeError as err:
        _fail(f"the Gateway's list of repositories is not readable: {err} --json shows the raw rows.")
    shown = [
        (raw, row)
        for raw, row in zip(repos, read)
        if (wanted_states is None or row["state"] in wanted_states)
        and (repo is None or matches_repo(row["name"], row["path"], repo))
        and (machine is None or row["machine"].lower() == machine.strip().lower())
    ]
    if json_output:
        # A filter narrows the same bare array; it never changes its shape.
        print(json.dumps([raw for raw, _ in shown], indent=2))
        if not shown:
            print(f"WARNING: {_STALE_DIRECTORS_CAUTION}", file=sys.stderr)
        return
    rows = [row for _, row in shown]

    blocks = [
        axi_output.format_count(
            len(rows),
            total=len(repos) if filtered else None,
            breakdown=_breakdown([row["state"] for row in rows], REPO_STATES),
        ),
        axi_output.render_list("repositories", chosen_fields, [{f: row[f] for f in chosen_fields} for row in rows]),
    ]
    if rows:
        blocks.append(
            f"worktrees in these repositories: {sum(row['worktrees'] for row in rows)}, "
            f"safe to reap: {sum(row['safe-to-reap'] for row in rows)}"
        )
    repeats = _repeats_line(rows, "repository", chosen_fields)
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
    raise GatewayShapeError(
        f"worktree {_shown(_raw(w, 'path'))} (row {index + 1}) has a state that is "
        f"{_shown(value)}; this tool knows only {', '.join(WORKTREE_STATES)}. "
        "If the Gateway has added a state, update cc-devthrottle."
    )


def _read_worktree(w: Dict[str, Any], index: int) -> Dict[str, Any]:
    """Every FleetWorktreeDto field this command reads, checked against what the Gateway sends, keyed
    by the --fields names. Any other value exits 1; nothing is defaulted."""
    labels = _labels(w, "sessionLabels", index)
    row = {
        "path": _text(w, "path", "worktree path", index),
        "repo": _text(w, "repoName", "repository name", index),
        "machine": _text(w, "machineName", "machine name", index),
        "state": worktree_state(w, index),
        "branch": _nullable_text(w, "branch", index),
        "reason": _any_text(w, "reason", index),
        "sessions": SESSION_NAME_SEPARATOR.join(labels) if labels else None,
        "bytes": _nullable_count(w, "sizeBytes", index),
        "last-activity": _nullable_timestamp(w, "lastActivityUtc", index),
        "repo-path": _text(w, "repoPath", "repository path", index),
        "director": _text(w, "directorId", "Director id", index),
        "data-age": _seconds(w, "dataAgeSeconds", index),
        "provisional": _flag(w, "provisional", index),
    }
    # FleetWorktreeFold.Flatten serves a worktree as "verifying" exactly when its repository is
    # provisional; the Director never folds to "verifying" itself.
    if row["provisional"] != (row["state"] == "verifying"):
        raise GatewayShapeError(
            f"worktree {_shown(row['path'])} (row {index + 1}) has state {row['state']} with provisional "
            f"{str(row['provisional']).lower()}; the Gateway serves verifying exactly when provisional is true."
        )
    return row


def _reclaimable_line(rows: Sequence[Dict[str, Any]]) -> Optional[str]:
    """How much disk the safe-to-reap worktrees shown would give back. A size the Gateway did not
    measure is counted as unknown, never as zero."""
    sizes = [row["bytes"] for row in rows if row["state"] == "safe-to-reap"]
    if not sizes:
        return None
    known = [size for size in sizes if size is not None]
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

    if json_output and not filtered:
        print(json.dumps(worktrees, indent=2))
        if not worktrees:
            print(f"WARNING: {_STALE_DIRECTORS_CAUTION}", file=sys.stderr)
        return

    try:
        read = _read_all(worktrees, _read_worktree)
    except GatewayShapeError as err:
        _fail(f"the Gateway's list of worktrees is not readable: {err} --json shows the raw rows.")
    shown = [
        (raw, row)
        for raw, row in zip(worktrees, read)
        if (wanted_states is None or row["state"] in wanted_states)
        and (repo is None or matches_repo(row["repo"], row["repo-path"], repo))
        and (machine is None or row["machine"].lower() == machine.strip().lower())
    ]
    if json_output:
        print(json.dumps([raw for raw, _ in shown], indent=2))
        if not shown:
            print(f"WARNING: {_STALE_DIRECTORS_CAUTION}", file=sys.stderr)
        return
    rows = [row for _, row in shown]

    blocks = [
        axi_output.format_count(
            len(rows),
            total=len(worktrees) if filtered else None,
            breakdown=_breakdown([row["state"] for row in rows], WORKTREE_STATES),
        ),
        axi_output.render_list("worktrees", chosen_fields, [{f: row[f] for f in chosen_fields} for row in rows]),
    ]
    repeats = _repeats_line(rows, "worktree", chosen_fields)
    if repeats:
        blocks.append(repeats)
    reclaimable = _reclaimable_line(rows)
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
