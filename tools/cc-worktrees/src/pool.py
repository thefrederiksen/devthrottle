"""The worktree pool: state on disk, and the get / return / lease / destroy / list operations.

One JSON state file per repository, all under one machine-wide lock, written atomically. A slot is
free, in-use or held. Anything the state cannot vouch for is held, never free.

Portions adapted from treehouse (https://github.com/kunchenguid/treehouse), internal/pool/pool.go,
state.go and destroy.go: reusable slots on a detached HEAD, lease ids with a conditional return,
quarantine of lost state, and atomic state writes. Copyright (c) 2026 kunchenguid. MIT License -
see THIRD_PARTY_NOTICES.md.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import sys
import tempfile
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import gitrun
import landed
from errors import EXIT_HELD, EXIT_POOL_FULL, ToolError
from gitrun import GitError
from landed import NotLanded
from statelock import machine_lock

HOME_ENV = "CC_WORKTREES_HOME"
STATE_VERSION = 2
FREE, IN_USE, HELD = "free", "in-use", "held"
STATES = (FREE, IN_USE, HELD)
SLOT_NAME = re.compile(r"wt[0-9]{2,}")
STATE_LOST = "state lost, cannot verify"
STATE_MISSING = "state missing, cannot verify"
DEFAULT_POOL_SIZE = 4


# ---------------------------------------------------------------------------------------------------
# Where things live
# ---------------------------------------------------------------------------------------------------


def state_home() -> Path:
    override = os.environ.get(HOME_ENV)
    if override:
        return Path(override)
    if os.name == "nt":
        base = os.environ.get("LOCALAPPDATA")
        if not base:
            raise ToolError("no-state-home", "LOCALAPPDATA is not set, so there is nowhere to keep pool state",
                            [f"Set {HOME_ENV} to a directory for cc-worktrees state"])
        return Path(base) / "cc-worktrees"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "cc-worktrees"
    xdg = os.environ.get("XDG_DATA_HOME")
    return (Path(xdg) if xdg else Path.home() / ".local" / "share") / "cc-worktrees"


def _key(path: Path | str) -> str:
    return os.path.normcase(os.path.realpath(path))


def main_repo_root(path: Path | str) -> Path:
    """The main repository for a path inside it or inside one of its linked worktrees."""
    if not Path(path).is_dir():
        raise ToolError("not-a-repository", f"{path} is not a directory", ["Pass --repo <path to a git repository>"])
    try:
        common = gitrun.out(path, "rev-parse", "--path-format=absolute", "--git-common-dir")
    except GitError as ex:
        raise ToolError("not-a-repository", f"{path} is not inside a git repository: {ex.short()}",
                        ["Pass --repo <path to a git repository>"]) from ex
    common_path = Path(common)
    if common_path.name != ".git":
        raise ToolError("unsupported-repository",
                        f"{path} uses a git directory at {common}; cc-worktrees needs a normal clone with a .git directory",
                        ["Use a normal (non-bare) clone"])
    return Path(os.path.realpath(common_path.parent))


def slots_dir(repo: Path) -> Path:
    return repo.parent / f"{repo.name}.worktrees"


def pool_file(home: Path, repo: Path) -> Path:
    digest = hashlib.sha256(_key(repo).encode("utf-8")).hexdigest()[:12]
    name = re.sub(r"[^A-Za-z0-9_.-]", "_", repo.name) or "repo"
    return home / "pools" / f"{name}-{digest}.json"


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


# ---------------------------------------------------------------------------------------------------
# State
# ---------------------------------------------------------------------------------------------------


@dataclass
class Pool:
    home: Path
    repo: Path
    slots: dict[str, dict]

    @property
    def file(self) -> Path:
        return pool_file(self.home, self.repo)

    def entry(self, name: str) -> dict:
        if name not in self.slots:
            raise ToolError("unknown-slot", f"{name} is not a slot of the pool for {self.repo}",
                            [f"cc-worktrees list --repo {self.repo}"])
        return self.slots[name]

    def set(self, name: str, state: str, holder: str | None, lease: str | None, reason: str | None) -> dict:
        entry = self.slots[name]
        entry.update(state=state, holder=holder, lease=lease, reason=reason, updated=_now())
        return entry

    def save(self) -> None:
        data = {"version": STATE_VERSION, "repo": str(self.repo),
                "slots": {name: self.slots[name] for name in sorted(self.slots)}}
        _atomic_write(self.file, json.dumps(data, indent=2) + "\n")
        register(self.home, self.repo)


def _atomic_write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="ascii", newline="\n") as f:
            f.write(text)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, path)
    except BaseException:
        if os.path.exists(tmp):
            os.remove(tmp)
        raise


def _lost_entry(path: Path, reason: str = STATE_LOST) -> dict:
    return {"path": str(path), "state": HELD, "holder": None, "lease": None, "reason": reason,
            "updated": _now(), "gitdir": None, "reflog_count": None, "reflog_newest": None}


ENTRY_KEYS = {"path", "state", "holder", "lease", "reason", "updated", "gitdir", "reflog_count", "reflog_newest"}


class _InvalidState(Exception):
    """The state file parsed, but what it says cannot be trusted."""


def _optional_text(value: object) -> bool:
    return value is None or (isinstance(value, str) and value != "")


def _check_entry(repo: Path, name: str, entry: object) -> None:
    if not isinstance(entry, dict) or set(entry) != ENTRY_KEYS:
        raise _InvalidState(f"{name}: unexpected fields")
    if not isinstance(entry["path"], str) or _key(entry["path"]) != _key(slots_dir(repo) / name):
        raise _InvalidState(f"{name}: the path is not this pool's {name}")
    if not isinstance(entry["updated"], str):
        raise _InvalidState(f"{name}: updated is not text")
    if not all(_optional_text(entry[key]) for key in ("holder", "lease", "reason", "gitdir")):
        raise _InvalidState(f"{name}: holder, lease, reason or gitdir is not text")
    count, newest = entry["reflog_count"], entry["reflog_newest"]
    if count is None:
        if newest is not None:
            raise _InvalidState(f"{name}: a newest reflog entry without a count")
    elif isinstance(count, bool) or not isinstance(count, int) or count < 0 or not _optional_text(newest) \
            or (count == 0) != (newest is None):
        raise _InvalidState(f"{name}: the reflog count and newest entry do not fit together")
    state, holder, lease, reason = entry["state"], entry["holder"], entry["lease"], entry["reason"]
    if state == FREE:
        # A free slot was proven landed, so its git metadata was proven then too.
        ok = (holder is None and lease is None and reason is None and entry["gitdir"] is not None
              and count is not None)
    elif state == IN_USE:
        ok = holder is not None and lease is not None and reason is None
    elif state == HELD:
        ok = reason is not None and (holder is None) == (lease is None)
    else:
        ok = False
    if not ok:
        raise _InvalidState(f"{name}: state {state!r} with a holder, lease and reason that do not fit it")


def _validated_slots(data: object, repo: Path) -> dict[str, dict]:
    """Every field is checked. One thing wrong makes the whole file untrustworthy."""
    if not isinstance(data, dict) or set(data) != {"version", "repo", "slots"}:
        raise _InvalidState("not a pool state object")
    version = data["version"]
    if isinstance(version, bool) or version != STATE_VERSION:
        raise _InvalidState(f"version {version!r}, this tool reads version {STATE_VERSION}")
    if not isinstance(data["repo"], str) or _key(data["repo"]) != _key(repo):
        raise _InvalidState("it names a different repository")
    if not isinstance(data["slots"], dict):
        raise _InvalidState("slots is not an object")
    for name, entry in data["slots"].items():
        if not SLOT_NAME.fullmatch(name):
            raise _InvalidState(f"{name!r} is not a slot name")
        _check_entry(repo, name, entry)
    return data["slots"]


def load(home: Path, repo: Path) -> Pool:
    """Read the pool, quarantining anything it cannot vouch for. Call only under the lock.

    A state file that cannot be parsed, or that parses but is wrong in any way, is set aside whole and
    every slot directory on disk comes back held. So does a slot directory the state does not know.
    """
    path = pool_file(home, repo)
    slots: dict[str, dict] = {}
    changed = False
    missing_reason = STATE_LOST
    if path.exists():
        try:
            slots = _validated_slots(json.loads(path.read_text(encoding="utf-8")), repo)
        except (OSError, UnicodeDecodeError, ValueError, _InvalidState):
            # Keep the damaged file for inspection; every slot on disk comes back held below.
            aside = path.with_name(f"{path.name}.corrupt-{datetime.now(timezone.utc):%Y%m%dT%H%M%S%f}")
            os.replace(path, aside)
            changed = True
            slots = {}
    elif is_registered(home, repo):
        missing_reason = STATE_MISSING
    directory = slots_dir(repo)
    if directory.is_dir():
        for child in directory.iterdir():
            if SLOT_NAME.fullmatch(child.name) and child.is_dir() and child.name not in slots:
                slots[child.name] = _lost_entry(child, missing_reason)
                changed = True
    pool = Pool(home, repo, slots)
    if changed:
        pool.save()
    return pool


# ---------------------------------------------------------------------------------------------------
# The registry: every repository that has a pool on this machine
# ---------------------------------------------------------------------------------------------------


REGISTRY_VERSION = 1


def registry_file(home: Path) -> Path:
    return home / "registry.json"


def read_registry(home: Path) -> list[str] | None:
    """The repositories with a pool on this machine, or None when there is no registry file at all."""
    path = registry_file(home)
    if not path.exists():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        valid = (isinstance(data, dict) and set(data) == {"version", "repos"}
                 and not isinstance(data["version"], bool) and data["version"] == REGISTRY_VERSION
                 and isinstance(data["repos"], list)
                 and all(isinstance(r, str) and r for r in data["repos"]))
    except (OSError, UnicodeDecodeError, ValueError):
        valid = False
    if not valid:
        raise ToolError("unreadable-registry",
                        f"the pool registry {path} cannot be read, so the pools on this machine are unknown",
                        [f"Move {path} aside; the next cc-worktrees get rebuilds it from the pool state files",
                         "cc-worktrees list --repo <path>"])
    return data["repos"]


def is_registered(home: Path, repo: Path) -> bool:
    repos = read_registry(home)
    return repos is not None and any(_key(r) == _key(repo) for r in repos)


def _state_file_repos(home: Path) -> list[str]:
    repos = []
    for file in sorted((home / "pools").glob("*.json")):
        try:
            repo = json.loads(file.read_text(encoding="utf-8"))["repo"]
            if not isinstance(repo, str) or not repo:
                raise ValueError("repo is not text")
        except (OSError, UnicodeDecodeError, ValueError, KeyError, TypeError) as ex:
            raise ToolError("unreadable-state",
                            f"pool state {file} cannot be read ({type(ex).__name__}), so its repository is unknown",
                            [f"Inspect {file}, then move it aside"]) from ex
        repos.append(repo)
    return repos


def register(home: Path, repo: Path) -> None:
    """Record that `repo` has a pool. A missing registry is rebuilt from the state files that exist;
    a pool whose state file is gone as well cannot be found that way."""
    repos = read_registry(home)
    if repos is not None and any(_key(r) == _key(repo) for r in repos):
        return
    if repos is None:
        repos = _state_file_repos(home)
    unique: dict[str, str] = {}
    for r in [*repos, str(repo)]:
        unique.setdefault(_key(r), r)
    _atomic_write(registry_file(home),
                  json.dumps({"version": REGISTRY_VERSION, "repos": sorted(unique.values())}, indent=2) + "\n")


def inventory(home: Path) -> list[Path]:
    """Every pool on this machine, or an error. Never an empty answer that cannot be backed."""
    repos = read_registry(home)
    if repos is None:
        raise ToolError("no-inventory",
                        f"there is no pool registry at {registry_file(home)}: either no pool was ever made on "
                        "this machine or the registry was lost, so an empty list could not be trusted",
                        ["cc-worktrees list --repo <path>"])
    registered = {pool_file(home, Path(r)).name for r in repos}
    for file in sorted((home / "pools").glob("*.json")):
        if file.name not in registered:
            raise ToolError("no-inventory",
                            f"pool state {file} is not in the registry {registry_file(home)}, so the registry "
                            "is incomplete",
                            [f"Move {registry_file(home)} aside; the next cc-worktrees get rebuilds it"])
    return [Path(r) for r in repos]


# ---------------------------------------------------------------------------------------------------
# Finding a slot
# ---------------------------------------------------------------------------------------------------


def resolve_target(home: Path, target: str, repo_opt: str | None) -> tuple[Path, str]:
    """A slot name (wt01, with --repo or the current directory's repository) or a worktree path."""
    if SLOT_NAME.fullmatch(target):
        return main_repo_root(repo_opt or os.getcwd()), target
    if Path(target).is_dir():
        repo = main_repo_root(target)
        for name, entry in load(home, repo).slots.items():
            if _key(entry["path"]) == _key(target):
                return repo, name
    else:
        for repo in inventory(home):
            if not repo.is_dir():
                continue
            for name, entry in load(home, repo).slots.items():
                if _key(entry["path"]) == _key(target):
                    return repo, name
    raise ToolError("not-a-pooled-worktree", f"{target} is not a worktree in any cc-worktrees pool",
                    ["cc-worktrees list"])


# ---------------------------------------------------------------------------------------------------
# Operations
# ---------------------------------------------------------------------------------------------------


def _slot_view(pool: Pool, name: str) -> dict:
    e = pool.slots[name]
    return {"repo": str(pool.repo), "slot": name, "path": e["path"], "state": e["state"],
            "holder": e["holder"], "reason": e["reason"], "updated": e.get("updated")}


def _lease_view(pool: Pool, name: str, tip: landed.RemoteTip, reused: bool) -> dict:
    e = pool.slots[name]
    return {"repo": str(pool.repo), "slot": name, "path": e["path"], "lease": e["lease"],
            "holder": e["holder"], "base": tip.branch, "commit": tip.commit, "reused": reused}


def _reset_to_tip(pool: Pool, name: str, tip: landed.RemoteTip | None) -> tuple[landed.RemoteTip | None, str | None]:
    """Prove the slot's work landed and reset it. Returns (tip, None) or (None, reason)."""
    entry = pool.slots[name]
    path = Path(entry["path"])
    try:
        checked = landed.check(path, pool.repo, tip, entry["gitdir"], _mark(entry))
        landed.reset(path, pool.repo, checked)
        mark = landed.reflog_mark(path)
    except NotLanded as ex:
        return None, str(ex)
    entry.update(gitdir=str(checked.gitdir), reflog_count=mark.count, reflog_newest=mark.newest)
    return checked.tip, None


def _mark(entry: dict) -> landed.ReflogMark | None:
    """Where the reflog stood when the slot was last proven or made. None: nothing on record, so every
    reflog entry must pass the check."""
    if entry["reflog_count"] is None:
        return None
    return landed.ReflogMark(entry["reflog_count"], entry["reflog_newest"])


def _hold(pool: Pool, name: str, reason: str) -> None:
    e = pool.slots[name]
    pool.set(name, HELD, e["holder"], e["lease"], reason)


def _new_lease() -> str:
    return uuid.uuid4().hex


def get(repo_path: str, holder: str, pool_size: int) -> dict:
    home = state_home()
    repo = main_repo_root(repo_path)
    with machine_lock(home):
        pool = load(home, repo)
        try:
            tip = landed.fetch_default(repo)
        except NotLanded as ex:
            raise ToolError("cannot-fetch", f"no worktree handed out: {ex}",
                            [f"git -C {repo} fetch origin"]) from ex
        for name in sorted(n for n, e in pool.slots.items() if e["state"] == FREE):
            ready, reason = _reset_to_tip(pool, name, tip)
            if ready is None:
                pool.set(name, HELD, None, None, reason)
                pool.save()
                continue
            pool.set(name, IN_USE, holder, _new_lease(), None)
            pool.save()
            return _lease_view(pool, name, tip, reused=True)

        if len(pool.slots) >= pool_size:
            taken = ", ".join(
                f"{n} {e['state']}" + (f" by {e['holder']}" if e["holder"] else "")
                + (f" ({e['reason']})" if e["reason"] else "")
                for n, e in sorted(pool.slots.items()))
            raise ToolError("pool-full", f"pool full ({len(pool.slots)} of {pool_size}): {taken}",
                            [f"cc-worktrees list --repo {repo}", "cc-worktrees return <path> --lease <lease>"],
                            exit_code=EXIT_POOL_FULL)

        number = 1
        while f"wt{number:02d}" in pool.slots or (slots_dir(repo) / f"wt{number:02d}").exists():
            number += 1
        name = f"wt{number:02d}"
        path = slots_dir(repo) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        try:
            gitrun.run(repo, "worktree", "add", "--detach", str(path), tip.commit)
        except GitError as ex:
            raise ToolError("create-failed", f"could not create {name}: {ex.short()}",
                            [f"cc-worktrees list --repo {repo}"]) from ex
        try:
            gitdir = landed.require_bound(path, repo, None)
            mark = landed.reflog_mark(path)
        except NotLanded as ex:
            pool.slots[name] = {**_lost_entry(path, f"created, but not proven sound: {ex}")}
            pool.save()
            raise ToolError("create-failed", f"{name} was created but is held: {ex}",
                            [f"cc-worktrees list --repo {repo}"]) from ex
        pool.slots[name] = {"path": str(path), "state": IN_USE, "holder": holder, "lease": _new_lease(),
                            "reason": None, "updated": _now(), "gitdir": str(gitdir),
                            "reflog_count": mark.count, "reflog_newest": mark.newest}
        pool.save()
        return _lease_view(pool, name, tip, reused=False)


def return_slot(target: str, lease: str, repo_opt: str | None) -> dict:
    home = state_home()
    with machine_lock(home):
        repo, name = resolve_target(home, target, repo_opt)
        pool = load(home, repo)
        entry = pool.entry(name)
        if entry["state"] == FREE:
            raise ToolError("not-in-use", f"{name} is already free; there is nothing to return",
                            [f"cc-worktrees list --repo {repo}"])
        if not lease or entry["lease"] != lease:
            raise ToolError("lease-mismatch",
                            f"{name} is no longer held under that lease; nothing was changed",
                            [f"cc-worktrees list --repo {repo}"])
        ready, reason = _reset_to_tip(pool, name, None)
        if ready is None:
            _hold(pool, name, reason)
            pool.save()
            view = _slot_view(pool, name)
            raise ToolError("held", f"{name} was not returned and is held: {reason}",
                            [f"git -C {entry['path']} status",
                             f"cc-worktrees return {entry['path']} --lease {entry['lease']}"],
                            exit_code=EXIT_HELD, details=view)
        pool.set(name, FREE, None, None, None)
        pool.save()
        return {**_slot_view(pool, name), "base": ready.branch, "commit": ready.commit}


def lease_slot(target: str, holder: str, reclaim_held: bool, repo_opt: str | None) -> dict:
    home = state_home()
    with machine_lock(home):
        repo, name = resolve_target(home, target, repo_opt)
        pool = load(home, repo)
        entry = pool.entry(name)
        if entry["state"] == IN_USE:
            raise ToolError("in-use", f"{name} is in use by {entry['holder']}",
                            [f"cc-worktrees get --repo {repo} --holder <holder>"])
        if entry["state"] == HELD:
            if not reclaim_held:
                raise ToolError("held", f"{name} is held: {entry['reason']}",
                                [f"cc-worktrees lease {name} --repo {repo} --holder <holder> --reclaim-held"])
            pool.set(name, IN_USE, holder, _new_lease(), None)
            pool.save()
            view = {**_slot_view(pool, name), "lease": pool.slots[name]["lease"], "reused": True,
                    "base": None, "commit": None}
            return view
        ready, reason = _reset_to_tip(pool, name, None)
        if ready is None:
            pool.set(name, HELD, None, None, reason)
            pool.save()
            raise ToolError("held", f"{name} was not leased and is now held: {reason}",
                            [f"cc-worktrees list --repo {repo}"], exit_code=EXIT_HELD,
                            details=_slot_view(pool, name))
        pool.set(name, IN_USE, holder, _new_lease(), None)
        pool.save()
        return _lease_view(pool, name, ready, reused=True)


def destroy_slot(target: str, yes: bool, allow_held: bool, allow_in_use: bool, repo_opt: str | None) -> dict:
    """Remove one slot. The landed check runs NOW, whatever the state says: a free slot is only free as
    of its last check, and anyone can have committed in it since. No flag skips that check."""
    home = state_home()
    with machine_lock(home):
        repo, name = resolve_target(home, target, repo_opt)
        pool = load(home, repo)
        entry = pool.entry(name)
        if entry["state"] == IN_USE and not allow_in_use:
            raise ToolError("in-use", f"{name} is in use by {entry['holder']}; not destroyed",
                            [f"cc-worktrees return {entry['path']} --lease <lease>"])
        if entry["state"] == HELD and not allow_held:
            raise ToolError("held", f"{name} is held ({entry['reason']}); not destroyed",
                            [f"cc-worktrees destroy {name} --repo {repo} --allow-held"])
        path = Path(entry["path"])

        def refuse(reason: str) -> ToolError:
            _hold(pool, name, reason)
            pool.save()
            return ToolError("held", f"{name} was not destroyed and is held: {reason}",
                             [f"git -C {path} status", f"git -C {path} log --oneline -5"], exit_code=EXIT_HELD,
                             details={**_slot_view(pool, name), "dry_run": not yes, "removed": False})

        try:
            checked = landed.check(path, repo, None, entry["gitdir"], _mark(entry))
        except NotLanded as ex:
            raise refuse(str(ex)) from ex
        view = {**_slot_view(pool, name), "dry_run": not yes, "removed": False}
        if not yes:
            return view
        try:
            landed.remove(path, repo, checked)
        except NotLanded as ex:
            raise refuse(str(ex)) from ex
        del pool.slots[name]
        pool.save()
        view["removed"] = True
        return view


def list_slots(repo_opt: str | None) -> list[dict]:
    home = state_home()
    with machine_lock(home):
        repos = [main_repo_root(repo_opt)] if repo_opt else inventory(home)
        rows: list[dict] = []
        for repo in repos:
            pool = load(home, repo)
            rows.extend(_slot_view(pool, name) for name in sorted(pool.slots))
        return rows
