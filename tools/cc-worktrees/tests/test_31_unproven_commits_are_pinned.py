"""Fix round 3, finding 3a: a commit the check cannot prove landed is pinned under refs/cc-worktrees/<slot>/,
so git's own reflog expiry and gc cannot remove it and every later check still sees it. A pin is removed
only after a passing check's act has succeeded."""

from __future__ import annotations

import subprocess
from pathlib import Path

import gitrun
from conftest import commit_file, git

EXIT_HELD = 3


def _outcome(call) -> int:
    try:
        call()
        return 0
    except Exception as ex:
        code = getattr(ex, "exit_code", None)
        assert code is not None, f"escaped {type(ex).__name__}: {ex}"
        return code


def _abandon_a_commit(path: Path) -> str:
    commit = commit_file(path, "x.txt", "the work the hold exists for\n", "X")
    git(path, "reset", "-q", "--hard", "HEAD~1")
    return commit


def _pins(repo: Path, slot: str) -> list[str]:
    return git(repo, "for-each-ref", "--format=%(objectname)", f"refs/cc-worktrees/{slot}/").split()


def test_a_held_commit_survives_reflog_expiry_and_gc_and_destroy_refuses_naming_it(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit = _abandon_a_commit(path)
    held = w.run("return", got["path"], "--lease", got["lease"], "--json")
    assert held.code == EXIT_HELD, held.out + held.err

    git(w.repo, "-c", "gc.reflogExpireUnreachable=now", "gc", "-q")

    dry = w.run("destroy", got["slot"], "--repo", str(w.repo), "--allow-held", "--json")
    assert dry.code == EXIT_HELD, dry.out + dry.err
    res = w.run("destroy", got["slot"], "--repo", str(w.repo), "--yes", "--allow-held", "--json")
    assert res.code == EXIT_HELD, res.out + res.err
    assert commit[:12] in w.slot(got["slot"])["reason"]
    assert path.is_dir()
    git(w.repo, "gc", "-q", "--prune=now")
    assert git(w.repo, "cat-file", "-t", commit) == "commit"
    assert _pins(w.repo, got["slot"]) == [commit]


def test_a_pinned_commit_later_pushed_to_a_remote_branch_is_released_and_its_pin_removed(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit = _abandon_a_commit(path)
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == EXIT_HELD
    assert _pins(w.repo, got["slot"]) == [commit]
    git(path, "push", "-q", "origin", f"{commit}:refs/heads/topic")
    again = w.run("lease", got["slot"], "--repo", str(w.repo), "--holder", "again", "--reclaim-held", "--json")
    assert again.code == 0, again.out + again.err

    res = w.run("return", got["path"], "--lease", again.data["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert _pins(w.repo, got["slot"]) == []


def test_a_slot_held_for_another_reason_still_pins_its_abandoned_commit(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit = _abandon_a_commit(path)
    (path / "notes.txt").write_text("untracked\n", encoding="utf-8")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    reason = w.slot(got["slot"])["reason"]
    assert reason.startswith("1 untracked file") and commit[:12] in reason
    assert _pins(w.repo, got["slot"]) == [commit]


def test_a_pin_that_cannot_be_written_is_named_and_the_slot_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    commit = _abandon_a_commit(Path(got["path"]))
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if args[:1] == ("update-ref",) and "-d" not in args:
            raise gitrun.GitError(list(args), 128, "fatal: simulated failure")
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)
    code = _outcome(lambda: pool.return_slot(got["path"], got["lease"], None))
    monkeypatch.undo()

    assert code == EXIT_HELD
    reason = w.slot(got["slot"])["reason"]
    assert commit[:12] in reason and "could not be pinned" in reason


def test_a_pin_is_kept_when_the_act_it_permitted_fails(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    commit = _abandon_a_commit(path)
    assert _outcome(lambda: pool.return_slot(got["path"], got["lease"], None)) == EXIT_HELD
    git(path, "push", "-q", "origin", f"{commit}:refs/heads/topic")
    w.push_from_other_clone({"moved-on.txt": "moved\n"}, "the default branch moves on")
    again = pool.lease_slot(got["slot"], "again", True, str(w.repo))
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if args[:1] == ("read-tree",):
            raise gitrun.GitError(list(args), 128, "fatal: simulated failure")
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)
    code = _outcome(lambda: pool.return_slot(got["path"], again["lease"], None))
    monkeypatch.undo()

    assert code == EXIT_HELD
    assert _pins(w.repo, got["slot"]) == [commit]


def test_a_pinned_commit_with_no_stray_answer_is_not_proven_by_its_pin(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    commit = _abandon_a_commit(Path(got["path"]))
    assert _outcome(lambda: pool.return_slot(got["path"], got["lease"], None)) == EXIT_HELD
    again = pool.lease_slot(got["slot"], "again", True, str(w.repo))
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if args[:2] == ("rev-list", commit) and "--remotes=origin" in args:
            return subprocess.CompletedProcess(["git", *args], 0, "", "")
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)
    code = _outcome(lambda: pool.return_slot(got["path"], again["lease"], None))
    monkeypatch.undo()

    assert code == EXIT_HELD
    assert "is on no remote branch" in w.slot(got["slot"])["reason"]
