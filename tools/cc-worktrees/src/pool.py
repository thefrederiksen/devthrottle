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
STATE_VERSION = 1
FREE, IN_USE, HELD = "free", "in-use", "held"
STATES = (FREE, IN_USE, HELD)
SLOT_NAME = re.compile(r"wt[0-9]{2,}")
STATE_LOST = "state lost, cannot verify"
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


def _lost_entry(path: Path) -> dict:
    return {"path": str(path), "state": HELD, "holder": None, "lease": None, "reason": STATE_LOST,
            "updated": _now()}


def _valid_entry(entry: object) -> bool:
    if not isinstance(entry, dict):
        return False
    if entry.get("state") not in STATES or not isinstance(entry.get("path"), str):
        return False
    for key in ("holder", "lease", "reason"):
        if key not in entry or not (entry[key] is None or isinstance(entry[key], str)):
            return False
    if entry["state"] == IN_USE and not (entry["holder"] and entry["lease"]):
        return False
    return True


def load(home: Path, repo: Path) -> Pool:
    """Read the pool, quarantining anything it cannot vouch for. Call only under the lock."""
    path = pool_file(home, repo)
    slots: dict[str, dict] = {}
    changed = False
    if path.exists():
        raw_slots = None
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            if isinstance(data, dict) and isinstance(data.get("slots"), dict):
                raw_slots = data["slots"]
        except (OSError, UnicodeDecodeError, ValueError):
            raw_slots = None
        if raw_slots is None:
            # Keep the damaged file for inspection; every slot on disk comes back held below.
            aside = path.with_name(f"{path.name}.corrupt-{datetime.now(timezone.utc):%Y%m%dT%H%M%S}")
            os.replace(path, aside)
            changed = True
            raw_slots = {}
        for name, entry in raw_slots.items():
            if not isinstance(name, str) or not SLOT_NAME.fullmatch(name):
                changed = True
                continue
            if _valid_entry(entry):
                slots[name] = entry
            else:
                slots[name] = _lost_entry(slots_dir(repo) / name)
                changed = True
    directory = slots_dir(repo)
    if directory.is_dir():
        for child in directory.iterdir():
            if SLOT_NAME.fullmatch(child.name) and child.is_dir() and child.name not in slots:
                slots[child.name] = _lost_entry(child)
                changed = True
    pool = Pool(home, repo, slots)
    if changed:
        pool.save()
    return pool


def known_repos(home: Path) -> list[Path]:
    repos = []
    for file in sorted((home / "pools").glob("*.json")):
        try:
            data = json.loads(file.read_text(encoding="utf-8"))
            repo = data["repo"]
        except (OSError, UnicodeDecodeError, ValueError, KeyError, TypeError) as ex:
            raise ToolError("unreadable-state",
                            f"pool state {file} cannot be read ({type(ex).__name__}), so its repository is unknown",
                            ["Run a command with --repo <path> for that repository to quarantine its slots"]) from ex
        repos.append(Path(repo))
    return repos


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
        for repo in known_repos(home):
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
    path = Path(pool.slots[name]["path"])
    try:
        checked_tip, head = landed.check(path, tip)
        landed.reset(path, checked_tip.commit, head)
    except NotLanded as ex:
        return None, str(ex)
    return checked_tip, None


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
        pool.slots[name] = {"path": str(path), "state": IN_USE, "holder": holder, "lease": _new_lease(),
                            "reason": None, "updated": _now()}
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
    home = state_home()
    with machine_lock(home):
        repo, name = resolve_target(home, target, repo_opt)
        pool = load(home, repo)
        entry = pool.entry(name)
        if entry["state"] == IN_USE and not allow_in_use:
            raise ToolError("in-use", f"{name} is in use by {entry['holder']}; not destroyed",
                            [f"cc-worktrees return {entry['path']}"])
        if entry["state"] == HELD and not allow_held:
            raise ToolError("held", f"{name} is held ({entry['reason']}); not destroyed",
                            [f"cc-worktrees destroy {name} --repo {repo} --allow-held"])
        path = Path(entry["path"])
        view = {**_slot_view(pool, name), "dry_run": not yes, "removed": False}
        if not yes:
            return view
        if path.exists():
            try:
                # Never --force: git refuses a worktree with modified or untracked files.
                gitrun.run(repo, "worktree", "remove", str(path))
            except GitError as ex:
                raise ToolError("destroy-failed", f"{name} was not removed: {ex.short()}",
                                [f"git -C {path} status"]) from ex
        del pool.slots[name]
        pool.save()
        view["removed"] = True
        return view


def list_slots(repo_opt: str | None) -> list[dict]:
    home = state_home()
    with machine_lock(home):
        repos = [main_repo_root(repo_opt)] if repo_opt else known_repos(home)
        rows: list[dict] = []
        for repo in repos:
            pool = load(home, repo)
            rows.extend(_slot_view(pool, name) for name in sorted(pool.slots))
        return rows
