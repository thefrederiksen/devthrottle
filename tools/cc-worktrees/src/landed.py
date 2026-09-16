"""The landed-work check, and the guarded reset that may only follow it.

A worktree may be reset only when every one of these is positively proven:

1. It has no uncommitted changes and no untracked files that are not ignored.
2. The remote was fetched successfully just now.
3. The default branch was read from the remote itself (never assumed, never the local origin/HEAD).
4. Every commit reachable from HEAD and not in the default branch is on a remote branch, or its
   content is already in the default branch (a rebase: same patch; a squash: same resulting files).

Anything that cannot be proven is NOT landed. The caller holds the worktree with the reason.

Portions adapted from treehouse (https://github.com/kunchenguid/treehouse),
internal/vcs/gitvcs/gitvcs.go: the remote default branch read, the squash content check, and the
HEAD.lock-guarded reset. Copyright (c) 2026 kunchenguid. MIT License - see THIRD_PARTY_NOTICES.md.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

import gitrun
from gitrun import GitError

REMOTE = "origin"


class NotLanded(Exception):
    """The work in a worktree is not proven landed. The message is the plain reason."""


@dataclass(frozen=True)
class RemoteTip:
    branch: str   # the remote's default branch name, e.g. "main" or "develop"
    commit: str   # the commit the fetched tracking ref points at


def cannot_verify(detail: str) -> NotLanded:
    return NotLanded(f"cannot verify: {detail}")


def fetch_default(cwd: Path) -> RemoteTip:
    """Read the default branch from the remote, then fetch every remote branch just now."""
    try:
        listing = gitrun.out(cwd, "ls-remote", "--symref", REMOTE, "HEAD")
    except GitError as ex:
        raise cannot_verify(ex.short()) from ex
    branch = None
    advertised = None
    for line in listing.splitlines():
        parts = line.split()
        if len(parts) == 3 and parts[0] == "ref:" and parts[2] == "HEAD":
            if parts[1].startswith("refs/heads/"):
                branch = parts[1][len("refs/heads/"):]
        elif len(parts) == 2 and parts[1] == "HEAD":
            advertised = parts[0]
    if not branch or not advertised:
        raise cannot_verify(f"the remote '{REMOTE}' did not say which branch is its default")

    # An explicit refspec, so the answer does not depend on how this clone's fetch is configured.
    # --prune drops tracking refs for branches deleted on the remote: a stale tracking ref would
    # otherwise count a commit as "on a remote branch" after the remote threw that branch away.
    try:
        gitrun.run(cwd, "fetch", "--prune", "--no-tags", REMOTE,
                   f"+refs/heads/*:refs/remotes/{REMOTE}/*")
    except GitError as ex:
        raise cannot_verify(ex.short()) from ex

    tracking = f"refs/remotes/{REMOTE}/{branch}"
    try:
        tip = gitrun.out(cwd, "rev-parse", "--verify", "--quiet", f"{tracking}^{{commit}}")
    except GitError as ex:
        raise cannot_verify(f"{tracking} is missing after the fetch") from ex
    # The remote may move between the two calls; it must only have moved forward.
    if tip != advertised:
        ahead = gitrun.run(cwd, "merge-base", "--is-ancestor", advertised, tip, check=False)
        if ahead.returncode != 0:
            raise cannot_verify(f"{tracking} does not match what the remote advertised")
    return RemoteTip(branch, tip)


def same_path(a: Path | str, b: Path | str) -> bool:
    return os.path.normcase(os.path.realpath(a)) == os.path.normcase(os.path.realpath(b))


def require_own_worktree(worktree: Path) -> None:
    """Refuse a directory that is not itself the top of a git worktree. Without this, git would
    walk up and answer for whatever repository happens to contain the directory."""
    try:
        top = gitrun.out(worktree, "rev-parse", "--show-toplevel")
    except GitError as ex:
        raise cannot_verify(f"not a git worktree: {ex.short()}") from ex
    if not same_path(top, worktree):
        raise cannot_verify("the directory is not the top of its own git worktree")


def head_commit(worktree: Path) -> str:
    try:
        return gitrun.out(worktree, "rev-parse", "--verify", "HEAD^{commit}")
    except GitError as ex:
        raise cannot_verify(f"HEAD cannot be read: {ex.short()}") from ex


def dirty_entries(worktree: Path) -> list[str]:
    try:
        text = gitrun.run(worktree, "status", "--porcelain", "--untracked-files=all",
                          "--ignore-submodules=none").stdout
    except GitError as ex:
        raise cannot_verify(f"git status failed: {ex.short()}") from ex
    return [line for line in text.splitlines() if line.strip()]


def require_clean(worktree: Path) -> None:
    entries = dirty_entries(worktree)
    if not entries:
        return
    untracked = sum(1 for e in entries if e.startswith("??"))
    changed = len(entries) - untracked
    parts = []
    if changed:
        parts.append(f"{changed} uncommitted change{'s' if changed != 1 else ''}")
    if untracked:
        parts.append(f"{untracked} untracked file{'s' if untracked != 1 else ''}")
    raise NotLanded(" and ".join(parts))


def _read_tree(worktree: Path, rev: str) -> dict[str, str]:
    raw = gitrun.run(worktree, "ls-tree", "-r", "-z", "--full-tree", rev).stdout
    tree: dict[str, str] = {}
    for record in raw.split("\0"):
        if not record:
            continue
        meta, sep, path = record.partition("\t")
        if not sep:
            raise GitError(["ls-tree", rev], 0, f"malformed ls-tree entry: {record!r}")
        tree[path] = meta
    return tree


def _content_in_default(worktree: Path, tip: str) -> bool:
    """True when every file HEAD changed since it split from the default branch is identical in the
    default branch tip. That is what a squash merge leaves behind. No change at all proves nothing."""
    base = gitrun.run(worktree, "merge-base", "HEAD", tip, check=False)
    if base.returncode != 0 or not base.stdout.strip():
        return False
    base_tree = _read_tree(worktree, base.stdout.strip())
    head_tree = _read_tree(worktree, "HEAD")
    tip_tree = _read_tree(worktree, tip)
    changed = {p for p in base_tree.keys() | head_tree.keys() if base_tree.get(p) != head_tree.get(p)}
    if not changed:
        return False
    return all(head_tree.get(p) == tip_tree.get(p) for p in changed)


def require_commits_landed(worktree: Path, tip: RemoteTip) -> None:
    try:
        stray = gitrun.out(worktree, "rev-list", "HEAD", "--not", f"--remotes={REMOTE}").split()
        if not stray:
            return
        # "- <sha>" means the same patch is already in the default branch (a rebase landed it).
        # A commit git cherry does not mark that way stays unproven.
        cherry = gitrun.out(worktree, "cherry", tip.commit, "HEAD")
        same_patch = {line.split()[1] for line in cherry.splitlines() if line.startswith("- ")}
        remaining = [c for c in stray if c not in same_patch]
        if not remaining:
            return
        if _content_in_default(worktree, tip.commit):
            return
    except GitError as ex:
        raise cannot_verify(ex.short()) from ex
    count = len(remaining)
    noun = "1 commit is" if count == 1 else f"{count} commits are"
    raise NotLanded(f"{noun} on no remote (newest {remaining[0][:12]})")


def check(worktree: Path, tip: RemoteTip | None = None) -> tuple[RemoteTip, str]:
    """Prove the worktree's work landed. Returns the remote tip and the HEAD that was checked.

    Pass `tip` only when the remote was fetched moments ago by the same command.
    Raises NotLanded with the plain reason otherwise.
    """
    if not worktree.is_dir():
        raise NotLanded("the worktree directory is missing")
    require_own_worktree(worktree)
    require_clean(worktree)
    head = head_commit(worktree)
    if tip is None:
        tip = fetch_default(worktree)
    require_commits_landed(worktree, tip)
    return tip, head


def _git_path(worktree: Path, name: str) -> Path:
    return Path(gitrun.out(worktree, "rev-parse", "--path-format=absolute", "--git-path", name))


def reset(worktree: Path, target: str, expected_head: str) -> None:
    """Reset a worktree whose work was just proven landed to `target`, keeping ignored files.

    Takes git's own HEAD.lock first, so no commit, checkout or rebase can move HEAD while it runs,
    then re-checks HEAD and cleanliness under that lock. Raises NotLanded with the reason if the
    worktree changed, or if the reset fails (for example a file locked by another process).
    """
    try:
        head_path = _git_path(worktree, "HEAD")
    except GitError as ex:
        raise NotLanded(f"reset failed: {ex.short()}") from ex
    lock_path = head_path.with_name(head_path.name + ".lock")
    try:
        fd = os.open(lock_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o666)
    except FileExistsError as ex:
        raise NotLanded(f"reset refused: {lock_path.name} exists, another git command is running") from ex
    except OSError as ex:
        raise NotLanded(f"reset refused: cannot lock HEAD: {ex.strerror}") from ex
    committed = False
    try:
        if head_commit(worktree) != expected_head:
            raise NotLanded("HEAD moved after the check")
        require_clean(worktree)
        try:
            gitrun.run(worktree, "read-tree", "--reset", "-u", target)
            # No -x: ignored build output stays, which is the point of a pool.
            gitrun.run(worktree, "clean", "-fd")
        except GitError as ex:
            raise NotLanded(f"reset failed part way: {ex.short()}") from ex
        os.write(fd, f"{target}\n".encode("ascii"))
        os.fsync(fd)
        os.close(fd)
        fd = -1
        os.replace(lock_path, head_path)
        committed = True
    finally:
        if fd != -1:
            os.close(fd)
        if not committed:
            try:
                os.remove(lock_path)
            except FileNotFoundError:
                pass
