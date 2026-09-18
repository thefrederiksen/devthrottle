"""Fix round 3, finding 3b: git's reflog expiry and gc rewrite a worktree's HEAD reflog, and an expired commit
leaves no trace in it, so the shorter list after the mark proves nothing. A slot is proven only while its
reflog is the same append-only file the tool's mark was written to: same identity, no shorter, the same bytes
up to the tool's line. Anything else holds, whatever the mark's age."""

from __future__ import annotations

import os
import shutil
from pathlib import Path

import pytest

from conftest import commit_file, git

EXIT_HELD = 3
REWRITTEN = "the HEAD reflog was rewritten since cc-worktrees wrote its line"


def _abandon_a_commit(path: Path) -> str:
    commit = commit_file(path, "work.txt", "precious unpushed work\n", "precious")
    git(path, "reset", "-q", "--hard", "HEAD~1")
    return commit


def _log(path: Path) -> Path:
    return Path(git(path, "rev-parse", "--path-format=absolute", "--git-dir")) / "logs" / "HEAD"


EXPIRIES = {
    "expire-unreachable-in-the-slot": lambda w, path: git(path, "reflog", "expire", "--expire-unreachable=now", "--all"),
    "expire-unreachable-in-the-repository": lambda w, path: git(w.repo, "reflog", "expire", "--expire-unreachable=now",
                                                                 "--all"),
    "gc-configured-in-the-slot": lambda w, path: (git(w.repo, "config", "gc.reflogExpireUnreachable", "now"),
                                                   git(path, "gc", "-q")),
    "gc-configured-in-the-repository": lambda w, path: (git(w.repo, "config", "gc.reflogExpireUnreachable", "now"),
                                                         git(w.repo, "gc", "-q")),
}


@pytest.mark.parametrize("expire", list(EXPIRIES), ids=list(EXPIRIES))
def test_a_commit_expired_from_the_reflog_before_any_check_holds_return_and_destroy(local_world, expire):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit = _abandon_a_commit(path)
    EXPIRIES[expire](w, path)
    assert commit not in _log(path).read_text(encoding="utf-8")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert REWRITTEN in w.slot(got["slot"])["reason"]
    gone = w.run("destroy", got["slot"], "--repo", str(w.repo), "--yes", "--allow-held", "--json")
    assert gone.code == EXIT_HELD, gone.out + gone.err
    assert path.is_dir()


def test_a_gc_that_expires_nothing_still_holds_a_free_slot(local_world):
    w = local_world
    got = w.get(holder="first")
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == 0
    git(w.repo, "gc", "-q")

    second = w.get(holder="second")

    assert second["slot"] != got["slot"]
    assert REWRITTEN in w.slot(got["slot"])["reason"]


def test_a_gc_auto_with_nothing_to_do_frees_the_slot(local_world):
    w = local_world
    got = w.get()
    git(w.repo, "gc", "--auto", "-q")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err


def test_the_same_bytes_in_a_new_file_hold_the_slot(local_world):
    w = local_world
    got = w.get()
    log = _log(Path(got["path"]))
    copy = log.with_name("HEAD.copy")
    shutil.copyfile(log, copy)
    os.replace(copy, log)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert REWRITTEN in w.slot(got["slot"])["reason"]


def test_bytes_changed_in_place_before_the_mark_hold_the_slot(local_world):
    w = local_world
    got = w.get()
    log = _log(Path(got["path"]))
    with open(log, "r+b") as f:
        first = f.read(1)
        f.seek(0)
        f.write(b"f" if first != b"f" else b"e")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert w.slot(got["slot"])["state"] == "held"


def test_a_reflog_file_cut_short_in_place_holds_the_slot(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    log = _log(path)
    with open(log, "r+b") as f:
        f.truncate(log.stat().st_size - 1)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert w.slot(got["slot"])["state"] == "held"


def test_a_held_commit_seen_before_the_expiry_is_kept_and_named(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit = _abandon_a_commit(path)
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == EXIT_HELD
    git(w.repo, "reflog", "expire", "--expire-unreachable=now", "--all")

    res = w.run("destroy", got["slot"], "--repo", str(w.repo), "--yes", "--allow-held", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    reason = w.slot(got["slot"])["reason"]
    assert REWRITTEN in reason and commit[:12] in reason
    git(w.repo, "gc", "-q", "--prune=now")
    assert git(w.repo, "cat-file", "-t", commit) == "commit"
