"""The landed-work check, and the guarded reset that may only follow it.

A worktree may be reset only when every one of these is positively proven:

1. It has no uncommitted changes and no untracked files that are not ignored.
2. The remote was fetched successfully just now.
3. The default branch was read from the remote itself (never assumed, never the local origin/HEAD).
4. Every commit reachable from HEAD and on no remote branch is, on its own, the same patch as a commit
   in the default branch (a rebase). A matching final snapshot is never proof for the commits behind
   it, so work landed only by a squash is not proven here.

Anything that cannot be proven is NOT landed. The caller holds the worktree with the reason.

Portions adapted from treehouse (https://github.com/kunchenguid/treehouse),
internal/vcs/gitvcs/gitvcs.go: the remote default branch read and the HEAD.lock-guarded reset. Copyright (c) 2026 kunchenguid. MIT License - see THIRD_PARTY_NOTICES.md.
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


def git_metadata_dir(repo: Path, worktree: Path) -> Path:
    """The linked-worktree record in the MAIN repository whose back-link names this directory.

    This is found from the repository's side, so it cannot be redirected by editing the slot's own
    .git file. Exactly one record must name the directory.
    """
    records = repo / ".git" / "worktrees"
    matches = []
    if records.is_dir():
        for record in sorted(records.iterdir()):
            link = record / "gitdir"
            if not link.is_file():
                continue
            try:
                text = link.read_text(encoding="utf-8").strip()
            except (OSError, UnicodeDecodeError) as ex:
                raise cannot_verify(f"cannot read {link}: {ex}") from ex
            target = Path(text) if os.path.isabs(text) else record / text
            if same_path(target, worktree / ".git"):
                matches.append(record)
    if len(matches) != 1:
        raise cannot_verify(f"the repository has {len(matches)} worktree records for this directory, not exactly 1")
    return matches[0]


def require_bound(worktree: Path, repo: Path, recorded_gitdir: str | None) -> Path:
    """Prove the slot's .git leads to this slot's own record in this repository, before any git answer
    from inside the slot is trusted. Returns that record's directory.

    A .git file copied from another slot would make status, HEAD and the reset all act on the other
    slot's git state.
    """
    require_own_worktree(worktree)
    expected = git_metadata_dir(repo, worktree)
    try:
        actual = gitrun.out(worktree, "rev-parse", "--path-format=absolute", "--git-dir")
        common = gitrun.out(worktree, "rev-parse", "--path-format=absolute", "--git-common-dir")
    except GitError as ex:
        raise cannot_verify(f"cannot read the worktree's git directory: {ex.short()}") from ex
    if not same_path(common, repo / ".git"):
        raise NotLanded(f"its .git points into another repository ({common})")
    if not same_path(actual, expected):
        raise NotLanded(f"its .git points at the git metadata of another worktree ({Path(actual).name}, "
                        f"expected {expected.name})")
    if recorded_gitdir is not None and not same_path(recorded_gitdir, expected):
        raise NotLanded(f"its .git record {expected.name} is not the one recorded when the slot was made")
    return expected


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


def _short_list(commits: list[str], limit: int = 5) -> str:
    shown = ", ".join(c[:12] for c in commits[:limit])
    return shown + (f" and {len(commits) - limit} more" if len(commits) > limit else "")


def unproven_commits(worktree: Path, tip: RemoteTip, tips: list[str]) -> list[str]:
    """Every commit reachable from `tips` that is not individually proven landed, newest first.

    A commit is landed only when it is on a remote branch, or when it is the same patch as a commit in
    the default branch (git cherry marks it "-"). Nothing else counts: a squash, or a final snapshot
    that happens to match, says nothing about the commits behind it. An empty git answer where commits
    exist proves nothing, so a tip with no stray commits must also be positively found on a remote.
    """
    unproven: list[str] = []
    seen: set[str] = set()
    for start in tips:
        stray = gitrun.out(worktree, "rev-list", start, "--not", f"--remotes={REMOTE}").split()
        if not stray:
            on_remote = gitrun.out(worktree, "for-each-ref", "--format=%(refname)", "--contains", start,
                                   f"refs/remotes/{REMOTE}/")
            if not on_remote:
                raise cannot_verify(f"{start[:12]} lists no commit to check and is on no remote branch")
            continue
        # "- <sha>": the same patch is already in the default branch. "+ <sha>", a merge commit (git
        # cherry never lists one) or a commit git cherry does not mention at all stays unproven.
        cherry = gitrun.out(worktree, "cherry", tip.commit, start)
        same_patch = {line.split()[1] for line in cherry.splitlines() if line.startswith("- ")}
        for commit in stray:
            if commit in seen:
                continue
            seen.add(commit)
            if commit not in same_patch:
                unproven.append(commit)
    return unproven


def require_commits_landed(worktree: Path, tip: RemoteTip, tips: list[str]) -> None:
    try:
        unproven = unproven_commits(worktree, tip, tips)
    except GitError as ex:
        raise cannot_verify(ex.short()) from ex
    if unproven:
        count = len(unproven)
        noun = "1 commit is" if count == 1 else f"{count} commits are"
        raise NotLanded(f"{noun} on no remote: {_short_list(unproven)}")


@dataclass(frozen=True)
class Checked:
    tip: RemoteTip   # the remote default branch the work was checked against
    head: str        # the HEAD that was proven landed
    gitdir: Path     # the slot's own git metadata directory, proven bound to it


def check(worktree: Path, repo: Path, tip: RemoteTip | None, recorded_gitdir: str | None) -> Checked:
    """Prove the worktree's work landed, at this moment. Raises NotLanded with the plain reason.

    Pass `tip` only when the remote was fetched moments ago by the same command.
    """
    if not worktree.is_dir():
        raise NotLanded("the worktree directory is missing")
    gitdir = require_bound(worktree, repo, recorded_gitdir)
    require_clean(worktree)
    head = head_commit(worktree)
    if tip is None:
        tip = fetch_default(worktree)
    require_commits_landed(worktree, tip, [head])
    return Checked(tip, head, gitdir)


def reset(worktree: Path, repo: Path, checked: Checked) -> None:
    """Reset a worktree whose work was just proven landed to `target`, keeping ignored files.

    Takes git's own HEAD.lock first, so no commit, checkout or rebase can move HEAD while it runs,
    then re-checks HEAD and cleanliness under that lock. Raises NotLanded with the reason if the
    worktree changed, or if the reset fails (for example a file locked by another process).
    """
    target, expected_head = checked.tip.commit, checked.head
    head_path = checked.gitdir / "HEAD"
    lock_path = head_path.with_name(head_path.name + ".lock")
    try:
        fd = os.open(lock_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o666)
    except FileExistsError as ex:
        raise NotLanded(f"reset refused: {lock_path.name} exists, another git command is running") from ex
    except OSError as ex:
        raise NotLanded(f"reset refused: cannot lock HEAD: {ex.strerror}") from ex
    committed = False
    try:
        if not same_path(require_bound(worktree, repo, str(checked.gitdir)), checked.gitdir):
            raise NotLanded("the worktree's git metadata changed after the check")
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
