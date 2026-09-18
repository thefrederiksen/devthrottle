"""The per-repository pooled-worktree SETTING: `cc-devthrottle worktree pool status / on / off`.

THIS IS THE SWITCH THE DIRECTOR READS. It is not a second store and there must never be one. The
Director reads and writes the same value through `WorktreePoolSettings` (C#,
`src/CcDirector.Core/Git/WorktreePoolSettings.cs`): the same `config.json`, the same
`worktreePool.repoDefaults[<repository key>]` object, the same `enabled` / `poolSize` names, the same
key normalisation, and the same deep-merge write that leaves every other section of the file exactly
as it was. Two stores would be two answers to "is this repository pooled", and the one that is wrong
is the one that opens a session in the wrong directory.

Read that class before changing anything here. Four things are copied from it deliberately and each
one is load bearing:

  * THE KEY. The repository path, trimmed, with forward slashes turned into backslashes, without a
    trailing separator, and lower-cased on Windows. A key written any other way is a setting the
    Director never finds - it reads OFF and the whole command did nothing, silently.
  * THE DEFAULT. Off, with a pool size of four. A repository nobody has configured is off, and so is
    a stored value that cannot be read: nothing about this setting fails open.
  * THE WRITE. A deep merge into the document on disk, then an atomic replace. `config.json` is
    shared with the Director, with `cc-devthrottle settings`, and with the Gateway settings route;
    a writer that serialises its own model over the top DROPS every section it does not know about.
  * THE SIZE FLOOR. A pool size below one is refused rather than stored. A stored zero would be a
    pool that can never hand anything out, which reads as the tool being broken.

There is deliberately no application-wide level to fall back to: a repository opts in, one at a time.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import uuid
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_storage import CcStorage  # noqa: E402

# The names in config.json. They are the C# class's names and changing one here changes nothing on
# the Director's side - it changes whether the two agree.
SECTION = "worktreePool"
REPO_DEFAULTS = "repoDefaults"
ENABLED = "enabled"
POOL_SIZE = "poolSize"

# The pool size a repository gets when it turns the setting on without naming one. FOUR, matching
# WorktreePoolSettings.DefaultPoolSize: a slot is a full checkout on disk.
DEFAULT_POOL_SIZE = 4


class SettingsError(Exception):
    """This command will not do what it was asked. Carries the code and the help lines to print."""

    def __init__(self, code: str, message: str, help_lines):
        super().__init__(message)
        self.code = code
        self.message = message
        self.help_lines = list(help_lines)


def normalize_repo_key(repo_path: str) -> str:
    """The repository key, exactly as WorktreePoolSettings.NormalizeRepoKey computes it.

    One repository maps to one stored entry however its path was typed. The Windows lower-casing is
    part of the contract, not a nicety: the Director looks the setting up by this key and finds
    nothing at all if the two spellings differ.
    """
    normalized = repo_path.strip().replace("/", "\\").rstrip("\\")
    return normalized.lower() if os.name == "nt" else normalized


def config_path() -> Path:
    """The shared settings file, resolved the way every other cc-* tool resolves it."""
    return CcStorage.config_json()


def _read_document() -> Dict[str, Any]:
    """The whole of config.json as a dictionary.

    A missing or empty file is an empty document. A file that exists and cannot be parsed RAISES -
    it is never silently reset, because overwriting a document nobody could read destroys the user's
    settings to hide a problem. This is the C# reader's rule and the Python `CCDirectorConfig.save`
    rule both.
    """
    path = config_path()
    if not path.exists():
        return {}
    text = path.read_text(encoding="utf-8")
    if not text.strip():
        return {}
    try:
        document = json.loads(text)
    except json.JSONDecodeError as error:
        raise SettingsError(
            "unreadable-settings",
            f"{path} is not readable JSON, so the pooled-worktree setting was not changed: {error}",
            [f"Repair or remove {path}"],
        ) from error
    if not isinstance(document, dict):
        raise SettingsError(
            "unreadable-settings",
            f"{path} does not hold a JSON object, so the pooled-worktree setting was not changed",
            [f"Repair or remove {path}"],
        )
    return document


def _deep_merge(target: Dict[str, Any], patch: Dict[str, Any]) -> None:
    """CcDirectorConfigService.MergePatch, in Python: objects merge, everything else replaces."""
    for key, value in patch.items():
        if isinstance(value, dict) and isinstance(target.get(key), dict):
            _deep_merge(target[key], value)
        else:
            target[key] = value


def _write_document(document: Dict[str, Any]) -> None:
    """Write config.json through a temporary file and an atomic replace, as the Director does, so a
    crash part way through can never leave a half-written file that nothing can parse."""
    path = config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.parent / f".config.{uuid.uuid4().hex}.tmp"
    temporary.write_text(json.dumps(document, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def _merge_patch(patch: Dict[str, Any]) -> None:
    document = _read_document()
    _deep_merge(document, patch)
    _write_document(document)


def read(repo_path: str) -> Tuple[bool, int]:
    """(enabled, pool size) for `repo_path`, as the Director reads it.

    A repository that has never been configured, a blank path, and a stored value that is not the
    right shape all come back as the default, which is OFF.
    """
    if not repo_path or not repo_path.strip():
        return False, DEFAULT_POOL_SIZE

    key = normalize_repo_key(repo_path)
    document = _read_document()
    section = document.get(SECTION)
    repositories = section.get(REPO_DEFAULTS) if isinstance(section, dict) else None
    stored = repositories.get(key) if isinstance(repositories, dict) else None
    if not isinstance(stored, dict):
        return False, DEFAULT_POOL_SIZE

    enabled = stored.get(ENABLED)
    # A value that is not a boolean is not a "yes". Missing, null, a string, a number: all off.
    enabled = enabled if isinstance(enabled, bool) else False

    size = stored.get(POOL_SIZE)
    # bool is a subclass of int in Python, so it is excluded explicitly: `true` is not a pool size.
    if isinstance(size, bool) or not isinstance(size, int) or size < 1:
        size = DEFAULT_POOL_SIZE
    return enabled, size


def save(repo_path: str, enabled: bool, pool_size: int) -> None:
    """Store the setting for `repo_path`. Every other repository and every other section of
    config.json is left exactly as it was."""
    if not repo_path or not repo_path.strip():
        raise ValueError("Repository path is required")
    if pool_size < 1:
        raise ValueError("The pool size must be at least 1")
    _merge_patch({SECTION: {REPO_DEFAULTS: {normalize_repo_key(repo_path): {
        ENABLED: bool(enabled),
        POOL_SIZE: int(pool_size),
    }}}})


def clear(repo_path: str) -> None:
    """Forget the setting for `repo_path`, so it is back to the default, which is off.

    The entry is set to JSON null rather than deleted, which is exactly what
    `WorktreePoolSettings.Clear` does through its own merge patch, and what the Director's reader
    treats as "never configured".
    """
    if not repo_path or not repo_path.strip():
        raise ValueError("Repository path is required")
    _merge_patch({SECTION: {REPO_DEFAULTS: {normalize_repo_key(repo_path): None}}})


def repository_root(repo_path: str) -> str:
    """`repo_path` when it is a git repository's own root, or a refusal naming what it is instead.

    Two refusals, and the second one matters more than it looks. A path that is not in a repository
    at all is an obvious mistake. A path that is INSIDE one - a subdirectory, or one of the
    repository's linked worktrees - is the quiet one: the setting is keyed on the path, the Director
    opens sessions at the repository, and a setting stored under a subdirectory is one the Director
    never reads. It would look exactly like the command having worked.
    """
    path = Path(repo_path.strip()) if repo_path else Path("")
    if not path.is_dir():
        raise SettingsError(
            "not-a-repository",
            f"{repo_path} is not a directory",
            ["cc-devthrottle worktree pool status --repo <path to a git repository>"],
        )
    try:
        finished = subprocess.run(
            ["git", "-C", str(path), "rev-parse", "--path-format=absolute", "--git-common-dir"],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
    except OSError as error:
        raise SettingsError(
            "git-not-found",
            f"git could not be started, so {repo_path} could not be checked: {error}",
            ["Install git and put it on PATH"],
        ) from error
    if finished.returncode != 0:
        raise SettingsError(
            "not-a-repository",
            f"{repo_path} is not inside a git repository: {finished.stderr.strip() or 'git said nothing'}",
            ["cc-devthrottle worktree pool status --repo <path to a git repository>"],
        )

    common = Path(finished.stdout.strip())
    if common.name != ".git":
        raise SettingsError(
            "unsupported-repository",
            f"{repo_path} uses a git directory at {common}; a pool needs a normal clone with a .git directory",
            ["Use a normal (non-bare) clone"],
        )
    root = Path(os.path.realpath(common.parent))
    if os.path.normcase(os.path.realpath(path)) != os.path.normcase(str(root)):
        raise SettingsError(
            "not-the-repository-root",
            f"{repo_path} is inside the repository {root}, not the repository itself. The setting is "
            f"keyed on the repository the Director opens sessions in, so one stored here would never "
            f"be read",
            [f'cc-devthrottle worktree pool on --repo "{root}"'],
        )
    return str(root)


def local_directors() -> int:
    """How many Director processes are registered on this machine's instance home, or -1 when that
    cannot be told.

    The registrations are the Director's own (`<instance home>/config/director/instances/*.json`,
    each naming the process that wrote it), so this asks the same records the product asks. A
    registration whose process is gone does not count: the file outlives the process.
    """
    try:
        directory = CcStorage.tool_config("director") / "instances"
        if not directory.is_dir():
            return 0
        alive = 0
        for entry in directory.glob("*.json"):
            try:
                pid = json.loads(entry.read_text(encoding="utf-8")).get("Pid")
            except (OSError, json.JSONDecodeError, AttributeError):
                continue
            if isinstance(pid, int) and pid > 0 and _process_exists(pid):
                alive += 1
        return alive
    except OSError:
        return -1


def _process_exists(pid: int) -> bool:
    if os.name == "nt":
        finished = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}", "/NH"],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
        return finished.returncode == 0 and str(pid) in finished.stdout
    try:
        os.kill(pid, 0)
        return True
    except (OSError, ProcessLookupError):
        return False


def when_it_takes_effect(directors: Optional[int] = None) -> str:
    """One sentence saying WHEN the change is seen, and it states what is true rather than promising.

    The Director reads this setting on the create path, fresh from disk, every time it opens a
    session. So a running Director needs no restart and picks the change up at the NEXT session it
    opens - and a session already running is never moved, because where a session runs is decided
    once, at birth.
    """
    count = local_directors() if directors is None else directors
    if count > 0:
        running = f"{count} Director{'s are' if count != 1 else ' is'} running on this machine and "
    elif count == 0:
        running = "No Director is running on this machine; when one starts it "
    else:
        running = "Whether a Director is running here could not be read; a Director "
    return (running + "reads this setting fresh each time it opens a session, so the change applies "
            "to the next session opened in this repository. Sessions already running are not moved.")
