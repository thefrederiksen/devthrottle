"""The landed-work check, and the guarded reset that may only follow it.

A worktree may be reset only when every one of these is positively proven:

1. It has no uncommitted changes and no untracked files that are not ignored.
2. The remote was fetched successfully just now.
3. The default branch was read from the remote itself (never assumed, never the local origin/HEAD).
4. Every commit reachable from HEAD and on no remote branch is, on its own, the same patch as a commit
   in the default branch (a rebase), and that content is in the CURRENT default tip. A matching final
   snapshot is never proof for the commits behind it, so work landed only by a squash is not proven here.

Anything that cannot be proven is NOT landed. The caller holds the worktree with the reason.

Portions adapted from treehouse (https://github.com/kunchenguid/treehouse),
internal/vcs/gitvcs/gitvcs.go: the remote default branch read and the HEAD.lock-guarded reset. Copyright (c) 2026 kunchenguid. MIT License - see THIRD_PARTY_NOTICES.md.
"""

from __future__ import annotations

import hashlib
import os
import re
import stat
import sys
import time
import uuid
from collections.abc import Sequence
from dataclasses import dataclass, field
from pathlib import Path

import gitrun
from errors import ToolError
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


NETWORK_TIMEOUT_ENV = "CC_WORKTREES_NETWORK_TIMEOUT"
DEFAULT_NETWORK_TIMEOUT_SECONDS = 120.0


def network_timeout() -> float:
    raw = os.environ.get(NETWORK_TIMEOUT_ENV)
    if raw is None:
        return DEFAULT_NETWORK_TIMEOUT_SECONDS
    try:
        value = float(raw)
    except ValueError:
        value = 0.0
    if not value > 0:
        raise ToolError("bad-setting", f"{NETWORK_TIMEOUT_ENV}={raw!r} is not a number of seconds above 0",
                        [f"Unset {NETWORK_TIMEOUT_ENV}, or set it to e.g. 120"])
    return value


def fetch_default(cwd: Path) -> RemoteTip:
    """Read the default branch from the remote, then fetch every remote branch just now. Both calls
    give up after the network timeout: a remote that does not answer is "cannot verify"."""
    timeout = network_timeout()
    try:
        listing = gitrun.run(cwd, "ls-remote", "--symref", REMOTE, "HEAD", timeout=timeout).stdout.strip()
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
                   f"+refs/heads/*:refs/remotes/{REMOTE}/*", timeout=timeout)
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


def tracking_tip(cwd: Path, branch: str) -> RemoteTip:
    """The default branch's tracking ref as it stands at this moment. Read under the machine-wide lock,
    so the proof and the reset use the refs as they are, not the commit a fetch returned earlier."""
    tracking = f"refs/remotes/{REMOTE}/{branch}"
    try:
        return RemoteTip(branch, gitrun.out(cwd, "rev-parse", "--verify", "--quiet", f"{tracking}^{{commit}}"))
    except GitError as ex:
        raise cannot_verify(f"{tracking} cannot be read") from ex


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
        try:
            children = sorted(records.iterdir())
        except OSError as ex:
            raise cannot_verify(f"cannot list the worktree records in {records}: {ex}") from ex
        for record in children:
            # A record whose back-link cannot be read could be a second record naming this slot, so it is
            # never skipped: missing, not a regular file, or unreadable is cannot verify.
            link = record / "gitdir"
            try:
                if not stat.S_ISREG(link.stat().st_mode):
                    raise cannot_verify(f"{link} is not a regular file, so it cannot be read")
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


def require_no_hidden_flags(worktree: Path) -> None:
    """Hold a slot with any tracked file flagged assume-unchanged or skip-worktree. Both flags make git
    status, update-index --refresh and git worktree remove skip the file, so a local edit to it is
    invisible to every cleanliness answer. `git ls-files -v` reads the flags themselves, so it cannot be
    fooled by them. Whether the flagged file really changed is never decided: the flag alone holds. A
    sparse checkout marks the files it leaves out skip-worktree, so a sparse slot is held too."""
    try:
        text = gitrun.run(worktree, "ls-files", "-v", "-z").stdout
    except GitError as ex:
        raise cannot_verify(f"cannot read the index flags: {ex.short()}") from ex
    flagged = []
    for item in text.split("\0"):
        if not item:
            continue
        if len(item) < 3 or item[1] != " ":
            raise cannot_verify("git ls-files -v gave an entry that could not be read")
        tag, path = item[0], item[2:]
        # A lower-case tag is assume-unchanged; S is skip-worktree (s: both).
        if tag.islower() or tag == "S":
            flagged.append(path)
    if flagged:
        count = len(flagged)
        raise NotLanded(f"{count} tracked file{'s are' if count != 1 else ' is'} flagged assume-unchanged or "
                        f"skip-worktree, which hides local edits from git status: {_names(flagged)}")


_CASE_FOLDS_NAMES = os.name == "nt" or sys.platform == "darwin"
_REPARSE_POINT = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)


def _is_git_name(name: str) -> bool:
    return (name.casefold() if _CASE_FOLDS_NAMES else name) == ".git"


def nested_repositories(worktree: Path) -> list[str]:
    """Every entry named .git anywhere inside the worktree other than its own top-level .git, as paths
    relative to the worktree. A repository under an ignored path is invisible to git status, and its
    commits are on no ref of this repository, so git worktree remove would delete it with its history.

    The walk never follows a symlink, a junction or any other reparse point out of the slot, but an entry
    NAMED .git counts whatever it is. A directory that cannot be listed, or an entry that cannot be stat-ed,
    is cannot verify."""
    found: list[str] = []
    pending: list[tuple[str, str]] = [(str(worktree), "")]
    while pending:
        directory, relative = pending.pop()
        try:
            with os.scandir(directory) as listing:
                entries = list(listing)
        except OSError as ex:
            raise cannot_verify(f"cannot list {relative or 'the worktree'} to look for nested repositories: "
                                f"{ex.strerror or ex}") from ex
        for entry in entries:
            path = f"{relative}/{entry.name}" if relative else entry.name
            if _is_git_name(entry.name):
                if relative:
                    found.append(path)
                continue
            try:
                info = entry.stat(follow_symlinks=False)
            except OSError as ex:
                raise cannot_verify(f"cannot read {path} to look for nested repositories: "
                                    f"{ex.strerror or ex}") from ex
            if not stat.S_ISDIR(info.st_mode) or getattr(info, "st_file_attributes", 0) & _REPARSE_POINT:
                continue
            pending.append((entry.path, path))
    return sorted(found)


def require_no_nested_repositories(worktree: Path) -> None:
    """Hold a slot with a git repository anywhere inside it. That includes a registered submodule: the
    status check proves a submodule matches the commit this repository records, not that the submodule's
    own HEAD, or the commits abandoned in it, are on its remote."""
    found = nested_repositories(worktree)
    if found:
        count = len(found)
        raise NotLanded(f"{count} git repositor{'ies are' if count != 1 else 'y is'} inside the worktree, and "
                        f"its commits cannot be proven landed: {_names(found)}")


# ---------------------------------------------------------------------------------------------------
# The repository's stash
# ---------------------------------------------------------------------------------------------------

STASH_REF = "refs/stash"


@dataclass(frozen=True)
class StashEntry:
    commit: str
    selector: str   # stash@{0}
    subject: str    # "On main: <the message>"

    def line(self) -> str:
        """The entry as `git stash list` prints it."""
        return f"{self.selector}: {self.subject}"


def stash_value(repo: Path) -> str | None:
    """`refs/stash` in the MAIN repository as it stands now, or None when there is no stash at all.

    `refs/stash` is a common ref: one stash stack is shared by the developer's own checkout and every
    worktree of the repository, so its mere existence says nothing about any slot. What says something
    is that it MOVED while a slot was held - `git stash` leaves the tree clean, HEAD where it was and
    the reflog untouched, so nothing else the check reads can see the work.
    """
    answer = gitrun.run(repo, "rev-parse", "--verify", "--quiet", f"{STASH_REF}^{{commit}}", check=False)
    text = answer.stdout.strip()
    if answer.returncode == 0 and _OBJECT_ID.fullmatch(text):
        return text
    if answer.returncode == 1 and not text:
        return None
    detail = (answer.stderr or answer.stdout).strip()[:200] or f"exit code {answer.returncode}"
    raise cannot_verify(f"cannot read {STASH_REF}: {detail}")


def stash_entries(repo: Path) -> list[StashEntry]:
    """Every entry of the repository's stash, newest first, as `git stash list` shows them."""
    try:
        text = gitrun.run(repo, "stash", "list", "-z", "--format=%H%x00%gd%x00%gs").stdout
    except GitError as ex:
        raise cannot_verify(f"cannot read the stash: {ex.short()}") from ex
    if not text:
        return []
    fields = text.removesuffix("\0").split("\0")
    if len(fields) % 3:
        raise cannot_verify("the stash could not be read entry by entry")
    return [StashEntry(*fields[i:i + 3]) for i in range(0, len(fields), 3)]


def stash_added(recorded: str | None, entries: list[StashEntry]) -> list[StashEntry]:
    """The entries pushed since `recorded` was the value of `refs/stash`: the ones above it in the
    stash. With nothing recorded, every entry is new. When the recorded entry is not in the stack at
    all it was dropped, and which entries are new cannot be said, so every one is named."""
    if recorded is None:
        return list(entries)
    for index, entry in enumerate(entries):
        if entry.commit == recorded:
            return entries[:index]
    return list(entries)


def _short_ref(commit: str | None) -> str:
    return commit[:12] if commit else "none"


def stash_first_parent(repo: Path, commit: str) -> str | None:
    """The commit a stash was made from, or None when the repository cannot say.

    A stash commit's first parent is the HEAD its worktree was at, and it is the only thing the
    repository records about where the entry came from. None means the entry cannot be attributed at
    all - a first parent git will not read, or an entry with no first parent - and an entry that
    cannot be attributed always holds the slot.
    """
    answer = gitrun.run(repo, "rev-parse", "--verify", "--quiet", f"{commit}^1^{{commit}}", check=False)
    text = answer.stdout.strip()
    if answer.returncode == 0 and _OBJECT_ID.fullmatch(text):
        return text
    return None


def stash_attribution(repo: Path, commits: Sequence[str],
                      entries: Sequence[StashEntry]) -> tuple[list[StashEntry], list[StashEntry]]:
    """Split `entries` into (this slot's, nobody's): the ones whose first parent is a commit the slot
    was at, and the ones nothing can attribute. An entry whose first parent is a commit the repository
    can read and the slot was never at was made in another worktree and is in neither list."""
    known = set(commits)
    owned: list[StashEntry] = []
    unknown: list[StashEntry] = []
    for entry in entries:
        parent = stash_first_parent(repo, entry.commit)
        if parent is None:
            unknown.append(entry)
        elif parent in known:
            owned.append(entry)
    return owned, unknown


def slot_commits(head: str, entries: Sequence[ReflogEntry]) -> list[str]:
    """Every commit this slot is known to have been at: its HEAD now, and every commit its own HEAD
    reflog names. The reflog is the tool's existing record of where the slot has been; there is no
    second record and none is wanted."""
    commits = [head]
    for entry in entries:
        if entry.commit not in commits:
            commits.append(entry.commit)
    return commits


def require_stash_unmoved(repo: Path, recorded: str | None, commits: Sequence[str]) -> None:
    """Hold the slot when the repository's stash gained an entry that is this slot's, or one that
    cannot be attributed to any worktree. Stashed work has landed nowhere.

    `refs/stash` is repository-wide, so a move alone says nothing: the developer's own checkout and
    every other slot push onto the same stack. `commits` is every commit this slot was at, and an
    added entry belongs to the slot when its first parent is one of them. An entry the repository
    cannot attribute holds the slot - never free on an unknown.
    """
    current = stash_value(repo)
    if current == recorded:
        return
    moved = f"the repository's stash moved while the slot was held ({_short_ref(recorded)} to " \
            f"{_short_ref(current)})"
    entries = stash_entries(repo)
    if current is not None and (not entries or entries[0].commit != current):
        raise NotLanded(f"{moved} and the stash log does not agree with {STASH_REF}, so the entries "
                        f"added cannot be attributed to the worktree they came from")
    if recorded is not None and all(entry.commit != recorded for entry in entries):
        raise NotLanded(f"{moved} and the entry cc-worktrees recorded is no longer in it, so the "
                        f"entries added cannot be attributed to the worktree they came from")
    owned, unknown = stash_attribution(repo, commits, stash_added(recorded, entries))
    reasons = []
    if owned:
        count = len(owned)
        reasons.append(f"{count} stash entr{'y was' if count == 1 else 'ies were'} added while the slot "
                       f"was held, and stashed work has landed nowhere: "
                       f"{_names([entry.line() for entry in owned])}")
    if unknown:
        count = len(unknown)
        reasons.append(f"{count} stash entr{'y' if count == 1 else 'ies'} could not be attributed to the "
                       f"worktree {'it' if count == 1 else 'they'} came from, so "
                       f"{'it may be' if count == 1 else 'they may be'} this slot's: "
                       f"{_names([entry.line() for entry in unknown])}")
    if reasons:
        raise NotLanded("; ".join(reasons))


def _short_list(commits: list[str], limit: int = 5) -> str:
    shown = ", ".join(c[:12] for c in commits[:limit])
    return shown + (f" and {len(commits) - limit} more" if len(commits) > limit else "")


def _z_paths(worktree: Path, *args: str) -> set[str]:
    return {item for item in gitrun.run(worktree, *args).stdout.split("\0") if item}


def _content_differs(worktree: Path, tip: RemoteTip, start: str, stray: list[str]) -> bool:
    """True when `start` and the current default tip differ at any path a stray commit touches.

    Paths are compared as exact bytes, merges against every parent, a root commit against nothing."""
    touched: set[str] = set()
    for commit in stray:
        touched |= _z_paths(worktree, "diff-tree", "-r", "-m", "--root", "--no-commit-id", "--name-only", "-z",
                            "--no-renames", commit)
    if not touched:
        return False
    differing = _z_paths(worktree, "diff", "--name-only", "-z", "--no-renames", tip.commit, start)
    return bool(touched & differing)


@dataclass(frozen=True)
class Unproven:
    commits: list[str]      # every commit not proven landed, newest first
    not_current: list[str]  # of those, the ones that are the same patch but whose content is not in the tip
    branch_gone: list[str] = field(default_factory=list)  # of those, the ones a remote branch proved at the
    #                                                        fetch but not when the remote was asked again


_OBJECT_ID = re.compile(r"[0-9a-f]{40}|[0-9a-f]{64}")
_CONTAINS_BATCH = 100


def _branches_containing(worktree: Path, tip: RemoteTip, commits: list[str]) -> list[str]:
    """The names of the tracking refs, other than HEAD and the default branch, that contain any of
    `commits`. --contains given several times is an OR."""
    prefix = f"refs/remotes/{REMOTE}/"
    names: set[str] = set()
    for i in range(0, len(commits), _CONTAINS_BATCH):
        contains = [arg for c in commits[i:i + _CONTAINS_BATCH] for arg in ("--contains", c)]
        listing = gitrun.out(worktree, "for-each-ref", "--format=%(refname)", *contains, prefix)
        for ref in listing.splitlines():
            if not ref.startswith(prefix):
                raise cannot_verify(f"git for-each-ref listed {ref!r} outside {prefix}")
            name = ref[len(prefix):]
            if name not in ("HEAD", tip.branch):
                names.add(name)
    return sorted(names)


def confirm_branches_on_remote(worktree: Path, names: list[str]) -> list[str]:
    """Ask the remote, now, where each named branch is. Returns the remote commits that exist in this
    repository; a branch the remote no longer has, or whose commit is not here, confirms nothing. Called
    inside the locked section that acts on the answer, so the proof is not the tracking refs a fetch wrote
    before the wait for the machine-wide lock. Any failure or unreadable answer is cannot verify."""
    if not names:
        return []
    wanted = {f"refs/heads/{name}" for name in names}
    try:
        listing = gitrun.run(worktree, "ls-remote", REMOTE, *sorted(wanted), timeout=network_timeout()).stdout
    except GitError as ex:
        raise cannot_verify(f"cannot confirm the remote branches that prove the work: {ex.short()}") from ex
    confirmed: list[str] = []
    for line in listing.splitlines():
        if not line.strip():
            continue
        parts = line.split("\t")
        if len(parts) != 2 or not _OBJECT_ID.fullmatch(parts[0]):
            raise cannot_verify("git ls-remote gave an answer that could not be read")
        commit, ref = parts
        # ls-remote matches the tail of a ref name, so only the exact refs asked for are answers.
        if ref not in wanted:
            continue
        present = gitrun.run(worktree, "cat-file", "-e", f"{commit}^{{commit}}", check=False)
        if present.returncode == 0 and commit not in confirmed:
            confirmed.append(commit)
    return confirmed


def unproven_commits(worktree: Path, tip: RemoteTip, tips: list[str]) -> Unproven:
    """Every commit reachable from `tips` that is not individually proven landed, newest first.

    A commit is landed only when it is in the default tip's history, or on a remote branch the remote
    itself confirms at this moment, or when it is the same patch as a commit in the default branch (git
    cherry marks it "-") AND that content is in the default branch NOW: for every path that any stray
    commit of the same start touches, the start's content equals the current default tip's. A patch that
    was landed and then reverted, or whose paths were changed again since, is not landed. A squash, or a
    final snapshot that happens to match, says nothing about the commits behind it. An empty git answer
    where commits exist proves nothing, so a tip with no stray commits must also be positively found on a
    remote.

    The tracking refs describe the remote as it was at the fetch, before the wait for the machine-wide
    lock. Ancestry of the default tip needs nothing more: an older base loses nothing. A commit whose only
    proof is another remote branch has that branch asked for again with one git ls-remote, and when none
    has, no network call is made.
    """
    starts = []
    branch_proven: list[str] = []
    for start in tips:
        stray = gitrun.out(worktree, "rev-list", start, "--not", f"--remotes={REMOTE}").split()
        if not stray:
            on_remote = gitrun.out(worktree, "for-each-ref", "--format=%(refname)", "--contains", start,
                                   f"refs/remotes/{REMOTE}/")
            if not on_remote:
                raise cannot_verify(f"{start[:12]} lists no commit to check and is on no remote branch")
        in_stray = set(stray)
        outside_default = gitrun.out(worktree, "rev-list", start, "--not", tip.commit).split()
        # Every commit on no remote is outside the default tip too, and a start with nothing outside the
        # default tip must positively be in its history: an empty answer must not skip the remote check.
        if not in_stray <= set(outside_default):
            raise cannot_verify(f"git rev-list gave answers for {start[:12]} that do not fit together")
        if not outside_default:
            gitrun.run(worktree, "merge-base", "--is-ancestor", start, tip.commit)
        branch_proven.extend(c for c in outside_default if c not in in_stray and c not in branch_proven)
        starts.append((start, stray))
    if branch_proven:
        confirmed = confirm_branches_on_remote(worktree, _branches_containing(worktree, tip, branch_proven))
        starts = [(start, gitrun.out(worktree, "rev-list", start, "--not", tip.commit, *confirmed).split())
                  for start, _ in starts]

    unproven: list[str] = []
    not_current: list[str] = []
    seen: set[str] = set()
    for start, stray in starts:
        if not stray:
            continue
        # "- <sha>": the same patch is already in the default branch's history. "+ <sha>", a merge commit
        # (git cherry never lists one) or a commit git cherry does not mention at all stays unproven.
        cherry = gitrun.out(worktree, "cherry", tip.commit, start)
        same_patch = {line.split()[1] for line in cherry.splitlines() if line.startswith("- ")}
        # History is not content: a matched patch counts only while its content is in the tip now.
        stale = bool(same_patch & set(stray)) and _content_differs(worktree, tip, start, stray)
        for commit in stray:
            if commit in seen:
                continue
            seen.add(commit)
            if commit not in same_patch:
                unproven.append(commit)
            elif stale:
                unproven.append(commit)
                not_current.append(commit)
    proven_before = set(branch_proven)
    return Unproven(unproven, not_current, [c for c in unproven if c in proven_before])


class UnprovenWork(NotLanded):
    """Commits that are not proven landed. `commits` names every one of them."""

    def __init__(self, reason: str, commits: list[str]):
        super().__init__(reason)
        self.commits = commits


PIN_NAMESPACE = "refs/cc-worktrees"


def pin_prefix(slot: str) -> str:
    return f"{PIN_NAMESPACE}/{slot}/"


def read_pins(repo: Path, slot: str) -> tuple[tuple[str, str], ...]:
    """The (ref, commit) pins the tool wrote for this slot. Never under refs/heads or refs/remotes, never
    pushed, and never read as proof: a pinned commit is checked like any other and stays unproven until
    the normal rule proves it."""
    try:
        listing = gitrun.out(repo, "for-each-ref", "--format=%(refname)%09%(objectname)", pin_prefix(slot))
    except GitError as ex:
        raise cannot_verify(f"cannot read the commits pinned for this slot: {ex.short()}") from ex
    pins = []
    for line in listing.splitlines():
        parts = line.split("\t")
        if len(parts) != 2 or not parts[0].startswith(pin_prefix(slot)) or not _OBJECT_ID.fullmatch(parts[1]):
            raise cannot_verify(f"a pin for this slot could not be read: {line[:200]!r}")
        pins.append((parts[0], parts[1]))
    return tuple(pins)


def write_pins(repo: Path, slot: str, commits: list[str]) -> str | None:
    """Pin each commit under refs/cc-worktrees/<slot>/<commit>, so git gc and reflog expiry cannot remove it
    and every later check sees it. Returns git's error when a pin could not be written, else None."""
    for commit in commits:
        try:
            gitrun.run(repo, "update-ref", f"{pin_prefix(slot)}{commit}", commit)
        except GitError as ex:
            return ex.short()
    return None


def drop_pins(repo: Path, pins: tuple[tuple[str, str], ...]) -> None:
    """Delete the pins a passing check read. Call only after the act they permitted has succeeded."""
    for ref, commit in pins:
        try:
            gitrun.run(repo, "update-ref", "-d", ref, commit)
        except GitError as ex:
            raise NotLanded(f"the pin {ref} could not be removed: {ex.short()}") from ex


def require_commits_landed(worktree: Path, tip: RemoteTip, tips: list[str]) -> None:
    try:
        found = unproven_commits(worktree, tip, tips)
    except GitError as ex:
        raise cannot_verify(ex.short()) from ex
    if found.commits:
        count = len(found.commits)
        noun = "1 commit is" if count == 1 else f"{count} commits are"
        reason = f"{noun} on no remote: {_short_list(found.commits)}"
        if found.not_current:
            reason += (f"; the same patch is in the default branch's history, but its content is not in the "
                       f"current default branch: {_short_list(found.not_current)}")
        if found.branch_gone:
            reason += (f"; the remote branch that proved {_short_list(found.branch_gone)} at the fetch is gone "
                       f"from the remote or moved")
        raise UnprovenWork(reason, found.commits)


@dataclass(frozen=True)
class ReflogEntry:
    commit: str
    selector: str   # HEAD@{<unix time>}
    subject: str


@dataclass(frozen=True)
class ReflogMark:
    """The HEAD reflog line cc-worktrees itself wrote when it last reset or made the slot.

    Never "whatever entry was newest": a line is only a mark when the tool wrote it, under HEAD.lock,
    with a nonce nobody else knows, and verified it was the one line added to exactly what was checked.

    The log file as it stood right after that line was verified is recorded too: its identity, its size and
    a hash of its bytes. git's reflog expiry and gc do not append; they write a new file and rename it over
    the old one, even when they expire nothing, and an expired commit leaves no trace in the file. The same
    append-only file, no shorter and with the same bytes up to the tool's line, is the positive proof that
    nothing was expired since the mark."""
    position: int   # counted from the oldest entry, starting at 0
    commit: str
    nonce: str
    log_dev: int
    log_ino: int
    log_size: int
    log_sha256: str


TOOL_IDENT = "cc-worktrees <cc-worktrees@localhost>"


def tool_subject(commit: str, nonce: str) -> str:
    return f"cc-worktrees: reset to {commit} {nonce}"


_OFF = ("false", "no", "off", "0")


def reflog_entries(worktree: Path) -> list[ReflogEntry]:
    """The slot's HEAD reflog, newest first. A commit abandoned with reset --hard lives only here."""
    setting = gitrun.run(worktree, "config", "--get", "core.logAllRefUpdates", check=False)
    if setting.returncode not in (0, 1):
        raise cannot_verify(f"cannot read core.logAllRefUpdates: {setting.stderr.strip()[:200]}")
    if setting.returncode == 0 and setting.stdout.strip().lower() in _OFF:
        raise cannot_verify("core.logAllRefUpdates is off, so commits abandoned in the worktree cannot be seen")
    try:
        text = gitrun.run(worktree, "reflog", "show", "-z", "--date=unix", "--format=%H%x00%gd%x00%gs",
                          "HEAD").stdout
    except GitError as ex:
        raise cannot_verify(f"cannot read the HEAD reflog: {ex.short()}") from ex
    if not text:
        return []
    fields = text.removesuffix("\0").split("\0")
    if len(fields) % 3:
        raise cannot_verify("the HEAD reflog could not be read entry by entry")
    return [ReflogEntry(*fields[i:i + 3]) for i in range(0, len(fields), 3)]


REFLOG_REWRITTEN = ("the HEAD reflog was rewritten since cc-worktrees wrote its line (git gc or git reflog "
                    "expire), so commits abandoned in it may have been removed")


def require_reflog_complete(gitdir: Path, mark: ReflogMark | None) -> None:
    """Hold unless the slot's HEAD reflog is provably the same append-only file the tool's mark was written
    to. No age is ever proof: a hand-run `git reflog expire --expire-unreachable=now` ignores every
    configured expiry."""
    if mark is None:
        raise NotLanded("cc-worktrees has no record of the HEAD reflog file it last wrote, so commits abandoned "
                        "in it may have been removed")
    log = gitdir / "logs" / "HEAD"
    try:
        with open(log, "rb") as f:
            info = os.fstat(f.fileno())
            prefix = f.read(mark.log_size)
    except OSError as ex:
        raise cannot_verify(f"cannot read the HEAD reflog file: {ex.strerror or ex}") from ex
    if ((info.st_dev, info.st_ino) != (mark.log_dev, mark.log_ino) or info.st_size < mark.log_size
            or hashlib.sha256(prefix).hexdigest() != mark.log_sha256):
        raise NotLanded(REFLOG_REWRITTEN)


def reflog_commits_since(entries: list[ReflogEntry], mark: ReflogMark | None) -> list[str]:
    """The commits of the HEAD reflog entries after `mark`, newest first. With no mark on record, every
    entry counts. The mark must be found as the tool's own line, with its commit and nonce, at its
    recorded position; anything else cannot be vouched for."""
    if mark is None:
        new = entries
    else:
        index = len(entries) - 1 - mark.position
        if index < 0:
            raise cannot_verify("the HEAD reflog lost the line cc-worktrees wrote when the slot was handed out")
        entry = entries[index]
        if entry.commit != mark.commit or entry.subject != tool_subject(mark.commit, mark.nonce):
            raise cannot_verify("the HEAD reflog does not have the line cc-worktrees wrote where it was recorded")
        new = entries[:index]
    commits: list[str] = []
    for entry in new:
        if entry.commit not in commits:
            commits.append(entry.commit)
    return commits


@dataclass(frozen=True)
class Checked:
    tip: RemoteTip                    # the remote default branch the work was checked against
    head: str                         # the HEAD that was proven landed
    gitdir: Path                      # the slot's own git metadata directory, proven bound to it
    reflog: tuple[ReflogEntry, ...]   # the whole HEAD reflog the proof examined, newest first
    pins: tuple[tuple[str, str], ...] = ()  # the slot's pins, all proven landed; dropped after the act
    stash: str | None = None          # refs/stash as recorded when the slot was handed out, proven unmoved


def check(worktree: Path, repo: Path, tip: RemoteTip, recorded_gitdir: str | None,
          mark: ReflogMark | None, slot: str, stash: str | None) -> Checked:
    """Prove the worktree's work landed, at this moment. Raises NotLanded with the plain reason.

    Checked: HEAD's commits, every commit the HEAD reflog gained since `mark` (None: all of them), and every
    commit pinned for `slot`. `tip` must come from a fetch made moments ago by the same command.

    A hold found before the commits are checked (uncommitted changes, hidden flags, a nested repository, a
    reflog mark that cannot be vouched for) does not stop the commit proof: every commit it cannot prove is
    pinned first, so a slot held for any reason cannot lose a commit to git gc while it waits. With the mark
    unusable, every reflog entry is checked. The first reason found is the one raised.
    """
    if not worktree.is_dir():
        raise NotLanded("the worktree directory is missing")
    gitdir = require_bound(worktree, repo, recorded_gitdir)
    # HEAD and the reflog are read before the holds, not because of them: the stash rule needs the
    # commits this slot was at to say which entries are its own. Neither read can be held for - each
    # raises its own reason - so nothing about which reason wins changes by reading them here.
    head = head_commit(worktree)
    entries = reflog_entries(worktree)
    held: NotLanded | None = None
    try:
        require_clean(worktree)
        require_no_hidden_flags(worktree)
        require_no_nested_repositories(worktree)
        require_stash_unmoved(repo, stash, slot_commits(head, entries))
    except NotLanded as ex:
        held = ex
    try:
        since = reflog_commits_since(entries, mark)
        require_reflog_complete(gitdir, mark)
    except NotLanded as ex:
        held = held or ex
        since = reflog_commits_since(entries, None)
    pins = read_pins(repo, slot)
    starts = [head]
    for commit in [*since, *(c for _, c in pins)]:
        if commit not in starts:
            starts.append(commit)
    try:
        require_commits_landed(worktree, tip, starts)
    except NotLanded as ex:
        reason = str(ex)
        if isinstance(ex, UnprovenWork):
            failed = write_pins(repo, slot, ex.commits)
            reason += (f"; and it could not be pinned: {failed}" if failed
                       else f"; kept from git gc under {pin_prefix(slot)}")
        raise NotLanded(f"{held}; {reason}" if held is not None else reason) from ex
    if held is not None:
        raise held
    return Checked(tip, head, gitdir, tuple(entries), pins, stash)


def _z_list(worktree: Path, *args: str) -> list[str]:
    return [item for item in gitrun.run(worktree, *args).stdout.split("\0") if item]


def ignored_overlaps(worktree: Path, target: str) -> list[str]:
    """Ignored files in the worktree whose path the target tree tracks, as a file or as a directory
    above a file. `read-tree --reset -u` would overwrite them without a word.

    Compared without regard to case, so a path that differs only in case also counts: on a
    case-insensitive file system it is the same file.
    """
    ignored = _z_list(worktree, "ls-files", "-z", "--others", "--ignored", "--exclude-standard")
    if not ignored:
        return []
    tracked_files: set[str] = set()
    tracked_dirs: set[str] = set()
    for path in _z_list(worktree, "ls-tree", "-r", "-z", "--name-only", "--full-tree", target):
        folded = path.casefold()
        tracked_files.add(folded)
        parts = folded.split("/")
        tracked_dirs.update("/".join(parts[:i]) for i in range(1, len(parts)))
    hits = []
    for path in ignored:
        folded = path.casefold()
        parts = folded.split("/")
        if (folded in tracked_files or folded in tracked_dirs
                or any("/".join(parts[:i]) in tracked_files for i in range(1, len(parts)))):
            hits.append(path)
    return hits


def _names(paths: list[str], limit: int = 5) -> str:
    shown = ", ".join(paths[:limit])
    return shown + (f" and {len(paths) - limit} more" if len(paths) > limit else "")


def _take_head_lock(gitdir: Path, action: str) -> tuple[int, Path]:
    lock_path = gitdir / "HEAD.lock"
    try:
        fd = os.open(lock_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o666)
    except FileExistsError as ex:
        raise NotLanded(f"{action} refused: {lock_path.name} exists, another git command is running") from ex
    except OSError as ex:
        raise NotLanded(f"{action} refused: cannot lock HEAD: {ex.strerror}") from ex
    return fd, lock_path


def _recheck_under_lock(worktree: Path, repo: Path, checked: Checked) -> None:
    """With HEAD.lock held nothing can commit, check out or rebase. Everything the check proved must
    still be exactly as it was, the whole reflog included, entry by entry."""
    if not same_path(require_bound(worktree, repo, str(checked.gitdir)), checked.gitdir):
        raise NotLanded("the worktree's git metadata changed after the check")
    if head_commit(worktree) != checked.head:
        raise NotLanded("HEAD moved after the check")
    if tuple(reflog_entries(worktree)) != checked.reflog:
        raise NotLanded("the HEAD reflog changed after the check")
    require_clean(worktree)
    require_no_hidden_flags(worktree)
    require_stash_unmoved(repo, checked.stash, slot_commits(checked.head, checked.reflog))


def _append_tool_line(worktree: Path, gitdir: Path, examined: tuple[ReflogEntry, ...], old: str,
                      new: str) -> ReflogMark:
    """Write the tool's own HEAD reflog line, then prove the reflog is exactly what was examined plus
    that one line. Call only while holding HEAD.lock: git itself appends to this log only under that
    lock, so nothing else can write between the append and the re-read."""
    nonce = uuid.uuid4().hex
    line = f"{old} {new} {TOOL_IDENT} {int(time.time())} +0000\t{tool_subject(new, nonce)}\n"
    log = gitdir / "logs" / "HEAD"
    log.parent.mkdir(exist_ok=True)
    with open(log, "ab") as f:
        f.write(line.encode("ascii"))
        f.flush()
        os.fsync(f.fileno())
    now = reflog_entries(worktree)
    if (len(now) != len(examined) + 1 or tuple(now[1:]) != examined or now[0].commit != new
            or now[0].subject != tool_subject(new, nonce)):
        raise NotLanded("the HEAD reflog is not exactly what was checked plus the line cc-worktrees wrote")
    try:
        with open(log, "rb") as f:
            info = os.fstat(f.fileno())
            data = f.read()
    except OSError as ex:
        raise cannot_verify(f"cannot read the HEAD reflog file: {ex.strerror or ex}") from ex
    if not data.endswith(line.encode("ascii")) or len(data) != info.st_size:
        raise NotLanded("the HEAD reflog file does not end with the line cc-worktrees wrote")
    return ReflogMark(len(examined), new, nonce, info.st_dev, info.st_ino, len(data),
                      hashlib.sha256(data).hexdigest())


def mark_new_slot(worktree: Path, repo: Path, tip: str) -> tuple[Path, ReflogMark]:
    """For a slot `git worktree add` just made at `tip`: prove it is bound to its own record, that HEAD
    and every entry of its reflog are the tip, and write the tool's reflog line as its first mark."""
    gitdir = require_bound(worktree, repo, None)
    fd, lock_path = _take_head_lock(gitdir, "create")
    try:
        os.close(fd)
        if head_commit(worktree) != tip:
            raise NotLanded("HEAD is not the default branch tip it was created at")
        examined = tuple(reflog_entries(worktree))
        others = [e.commit for e in examined if e.commit != tip]
        if others:
            raise NotLanded(f"its HEAD reflog already names other commits: {_short_list(others)}")
        return gitdir, _append_tool_line(worktree, gitdir, examined, tip, tip)
    finally:
        _drop_lock(lock_path)


def _drop_lock(lock_path: Path) -> None:
    try:
        os.remove(lock_path)
    except FileNotFoundError:
        pass


def reset(worktree: Path, repo: Path, checked: Checked) -> ReflogMark:
    """Reset a worktree whose work was just proven landed to the checked remote tip, keeping ignored files.

    Takes git's own HEAD.lock first, so no commit, checkout or rebase can move HEAD while it runs,
    then re-checks everything under that lock. Before HEAD moves, writes the tool's own reflog line and
    proves it is the only line added to what was checked; that line is the returned mark. Raises
    NotLanded with the reason if the worktree changed, or if the reset fails (for example a file locked
    by another process).
    """
    target = checked.tip.commit
    fd, lock_path = _take_head_lock(checked.gitdir, "reset")
    mark = None
    committed = False
    try:
        _recheck_under_lock(worktree, repo, checked)
        # The status the check read is an observation an editor can outdate. The refresh re-reads every
        # tracked file's stat data; the two-tree merge below then refuses, path by path and before it
        # writes anything, to overwrite an untracked file or a local change on a path it would write.
        refreshed = gitrun.run(worktree, "update-index", "--refresh", check=False)
        if refreshed.returncode != 0:
            raise NotLanded(f"a tracked file changed after the check: {(refreshed.stdout or refreshed.stderr).strip()[:200]}")
        try:
            # As late as possible: read-tree has no way to keep an ignored file, in either mode.
            overlap = ignored_overlaps(worktree, target)
        except GitError as ex:
            raise cannot_verify(f"cannot list ignored files: {ex.short()}") from ex
        if overlap:
            raise NotLanded(f"the default branch now tracks {len(overlap)} ignored "
                            f"file{'s' if len(overlap) != 1 else ''} in this worktree, which a reset would "
                            f"overwrite: {_names(overlap)}")
        # Ignored build output stays, which is the point of a pool. There is no git clean: the tree was
        # proven clean, read-tree removes the files it untracks, so a clean could only ever delete a
        # file that was ignored before and is not ignored by the new tree.
        # Again at the last moment: a repository cloned into the slot since the check would otherwise be
        # handed to the next holder, or overwritten on a path the default branch now tracks.
        require_no_nested_repositories(worktree)
        try:
            gitrun.run(worktree, "read-tree", "-m", "-u", checked.head, target)
        except GitError as ex:
            raise NotLanded(f"reset refused or not finished: {ex.short()}") from ex
        os.write(fd, f"{target}\n".encode("ascii"))
        os.fsync(fd)
        mark = _append_tool_line(worktree, checked.gitdir, checked.reflog, checked.head, target)
        os.close(fd)
        fd = -1
        os.replace(lock_path, checked.gitdir / "HEAD")
        committed = True
        left = dirty_entries(worktree)
        if left:
            paths = [entry[3:] for entry in left]
            # Either files the new default branch no longer ignores, or a local change the two-tree merge
            # carried forward because the default branch did not touch that path. Both are kept.
            raise NotLanded(f"reset to the default branch, but {len(paths)} "
                            f"file{'s' if len(paths) != 1 else ''} it no longer ignores or that changed "
                            f"locally {'are' if len(paths) != 1 else 'is'} kept untouched: {_names(paths)}")
    finally:
        if fd != -1:
            os.close(fd)
        if not committed:
            _drop_lock(lock_path)
    return mark


# ---------------------------------------------------------------------------------------------------
# Releasing a held slot: the way out, without losing anything
# ---------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Released:
    pins: tuple[tuple[str, str], ...]   # every pin the slot has now, ref and commit
    pinned: tuple[str, ...]             # the commits this release pinned
    proven: tuple[str, ...]             # the commits it proved landed, so did not pin
    gone: tuple[str, ...]               # commits the pool state recorded that the repository no longer has
    removed: bool                       # whether a worktree directory or record was removed


def slots_with_pins(repo: Path) -> set[str]:
    """The slot names that still have pins under `refs/cc-worktrees/`.

    A name whose pins stand is never handed out again: the pins are commits every check must prove, so
    a new slot of that name would inherit the abandoned work of the old one and be held from its first
    return. The pins are the record of what was released, and nothing ever deletes them for the user.
    """
    try:
        listing = gitrun.out(repo, "for-each-ref", "--format=%(refname)", f"{PIN_NAMESPACE}/")
    except GitError as ex:
        raise cannot_verify(f"cannot read the commits pinned in this repository: {ex.short()}") from ex
    names = set()
    for ref in listing.splitlines():
        if not ref.startswith(f"{PIN_NAMESPACE}/") or "/" not in ref[len(PIN_NAMESPACE) + 1:]:
            raise cannot_verify(f"a pinned ref could not be read: {ref[:200]!r}")
        names.add(ref[len(PIN_NAMESPACE) + 1:].split("/", 1)[0])
    return names


def unpinnable(worktree: Path) -> list[str]:
    """Everything in the slot that no ref can keep, each named. A ref holds commits; it cannot hold a
    file nothing has committed, an edit git status cannot see, or the history of a repository whose
    commits are on no ref of this repository."""
    reasons = []
    try:
        entries = dirty_entries(worktree)
        if entries:
            paths = [entry[3:] for entry in entries]
            reasons.append(f"{len(paths)} uncommitted or untracked file{'s' if len(paths) != 1 else ''} "
                           f"cannot be kept by any ref: {_names(paths)}")
    except NotLanded as ex:
        reasons.append(str(ex))
    for require in (require_no_hidden_flags, require_no_nested_repositories):
        try:
            require(worktree)
        except NotLanded as ex:
            reasons.append(str(ex))
    return reasons


def release_candidates(worktree: Path) -> list[str]:
    """Every commit the slot itself could be holding: HEAD, every commit its HEAD reflog names, and the
    tip of the branch HEAD is on.

    The WHOLE reflog, never the part after the tool's mark: a mark that cannot be vouched for proves
    nothing about which entries are new, and a release must not depend on it.
    """
    commits = [head_commit(worktree)]
    for entry in reflog_entries(worktree):
        if entry.commit not in commits:
            commits.append(entry.commit)
    branch = gitrun.run(worktree, "symbolic-ref", "--quiet", "HEAD", check=False)
    name = branch.stdout.strip()
    if branch.returncode not in (0, 1):
        raise cannot_verify(f"cannot read which branch HEAD is on: {branch.stderr.strip()[:200]}")
    if branch.returncode == 0 and name:
        answer = gitrun.run(worktree, "rev-parse", "--verify", "--quiet", f"{name}^{{commit}}", check=False)
        tip = answer.stdout.strip()
        if answer.returncode == 0 and _OBJECT_ID.fullmatch(tip):
            if tip not in commits:
                commits.append(tip)
        elif not (answer.returncode == 1 and not tip):
            raise cannot_verify(f"cannot read {name}: {(answer.stderr or answer.stdout).strip()[:200]}")
    return commits


def _pin_for_release(cwd: Path, repo: Path, tip: RemoteTip | None, slot: str, candidates: list[str],
                     always: list[str], existing: tuple[tuple[str, str], ...]) -> tuple[list[str], list[str]]:
    """Pin every candidate the normal rule cannot prove landed, and every commit in `always`. Returns
    (pinned now, proven landed). Raises NotLanded when a pin cannot be written or is not there
    afterwards - the whole point of the release is that these refs exist."""
    already = {commit for _, commit in existing}
    pinned = [commit for commit in always if commit not in already]
    to_prove = [commit for commit in candidates if commit not in already and commit not in pinned]
    proven: list[str] = []
    if to_prove:
        found = None
        if tip is not None:
            try:
                found = unproven_commits(cwd, tip, to_prove)
            except (NotLanded, GitError):
                found = None
        if found is None:
            # No default branch to measure against, or git could not answer: nothing is proven landed,
            # so every candidate is kept.
            pinned.extend(commit for commit in to_prove if commit not in pinned)
        else:
            unproven = set(found.commits)
            pinned.extend(commit for commit in found.commits if commit not in already and commit not in pinned)
            proven.extend(commit for commit in to_prove if commit not in unproven)
    if pinned:
        failed = write_pins(repo, slot, pinned)
        if failed:
            raise NotLanded(f"the work in this slot could not be pinned, so it was not released: {failed}")
    held = {commit for _, commit in read_pins(repo, slot)}
    absent = [commit for commit in pinned if commit not in held]
    if absent:
        raise NotLanded(f"{len(absent)} commit{'s are' if len(absent) != 1 else ' is'} not pinned after "
                        f"writing the pins, so it was not released: {_short_list(absent)}")
    for commit in pinned:
        if gitrun.run(repo, "cat-file", "-e", f"{commit}^{{commit}}", check=False).returncode != 0:
            raise NotLanded(f"{commit[:12]} was pinned but the repository cannot read it, so the slot "
                            f"was not released")
    return pinned, proven


def release(worktree: Path, repo: Path, tip: RemoteTip | None, recorded_gitdir: str | None,
            mark: ReflogMark | None, slot: str, recorded_stash: str | None) -> Released:
    """Put a held slot back in the pool without losing anything.

    Everything in the slot that a ref can keep is pinned under `refs/cc-worktrees/<slot>/` first - HEAD,
    every commit its HEAD reflog names, the branch HEAD is on, and the stash entries the slot added -
    and only what the normal rule proves landed is left unpinned. Anything that cannot be pinned, or
    cannot be read, refuses the whole release. Then the directory is removed, ignored files with it.

    HEAD.lock is held from before the commits are read until the removal, so a commit made in the slot
    at that moment cannot slip past the pins.
    """
    existing = read_pins(repo, slot)
    if not worktree.is_dir():
        # Nothing to read and nothing to reset: pin what the repository still knows about this slot -
        # the pins already there, and the commit the pool state recorded when it last reset the slot.
        recorded = [mark.commit] if mark is not None else []
        known, gone = [], []
        for commit in recorded:
            found = gitrun.run(repo, "cat-file", "-e", f"{commit}^{{commit}}", check=False).returncode == 0
            (known if found else gone).append(commit)
        pinned, proven = _pin_for_release(repo, repo, tip, slot, known, [], existing)
        removed = False
        try:
            git_metadata_dir(repo, worktree)
        except NotLanded:
            record = None   # no record names this directory, so git has nothing to remove
        else:
            record = worktree
        if record is not None:
            try:
                gitrun.run(repo, "worktree", "remove", str(worktree))
            except GitError as ex:
                raise NotLanded(f"the worktree record could not be removed: {ex.short()}") from ex
            removed = True
        return Released(read_pins(repo, slot), tuple(pinned), tuple(proven), tuple(gone), removed)

    gitdir = require_bound(worktree, repo, recorded_gitdir)
    fd, lock_path = _take_head_lock(gitdir, "release")
    removed = False
    try:
        reasons = unpinnable(worktree)
        if reasons:
            raise NotLanded("; ".join(reasons))
        candidates = release_candidates(worktree)
        # The stash entries this slot owns, and the ones nothing can attribute: the slot is held on an
        # unknown, so the release keeps the unknown rather than leave it to gc. An entry the repository
        # shows was made in another worktree is that worktree's and is never pinned to this slot.
        owned, unknown = stash_attribution(repo, candidates, stash_added(recorded_stash, stash_entries(repo)))
        stashed: list[str] = []
        for entry in [*owned, *unknown]:
            if entry.commit not in stashed:
                stashed.append(entry.commit)
        pinned, proven = _pin_for_release(worktree, repo, tip, slot, candidates, stashed, existing)
        # The lock file stays on disk and is removed with the worktree's record; it only has to be
        # closed so that removal can delete it.
        os.close(fd)
        fd = -1
        try:
            gitrun.run(repo, "worktree", "remove", str(worktree))
        except GitError as ex:
            raise NotLanded(f"the worktree directory could not be removed: {ex.short()}") from ex
        removed = True
    finally:
        if fd != -1:
            os.close(fd)
        if not removed:
            _drop_lock(lock_path)
    return Released(read_pins(repo, slot), tuple(pinned), tuple(proven), (), True)


def remove(worktree: Path, repo: Path, checked: Checked) -> None:
    """Remove a worktree whose work was just proven landed, re-checked under HEAD.lock so nothing can
    commit between the check and the removal. `git worktree remove` is never forced: git itself still
    refuses a worktree with modified or untracked files that git status can see. It deletes ignored files,
    and would delete a repository under an ignored path, so the slot is walked for one again here."""
    fd, lock_path = _take_head_lock(checked.gitdir, "destroy")
    removed = False
    try:
        _recheck_under_lock(worktree, repo, checked)
        require_no_nested_repositories(worktree)
        # The lock file stays on disk and is removed with the worktree's record; it only has to be
        # closed so that removal can delete it.
        os.close(fd)
        fd = -1
        try:
            gitrun.run(repo, "worktree", "remove", str(worktree))
        except GitError as ex:
            raise NotLanded(f"not removed: {ex.short()}") from ex
        removed = True
    finally:
        if fd != -1:
            os.close(fd)
        if not removed:
            _drop_lock(lock_path)
