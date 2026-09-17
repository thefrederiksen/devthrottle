"""Fix round 3, finding 2: a commit whose only proof is a remote branch other than the default. The tracking
refs come from a fetch made before the wait for the machine-wide lock, so the branch is asked for again on
the remote inside the locked section that acts. Gone, moved away, or no answer: held."""

from __future__ import annotations

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


def _work_only_on_topic(pool, w) -> tuple[dict, Path, str]:
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    commit = commit_file(path, "topic.txt", "work only on a remote branch\n", "topic work")
    git(path, "push", "-q", "origin", "HEAD:refs/heads/topic")
    git(path, "reset", "-q", "--hard", "HEAD~1")
    return got, path, commit


def _after_fetch(monkeypatch, landed, step) -> None:
    """The remote changes after the real fetch returned: the moment the command waits for the machine lock."""
    original = landed.fetch_default

    def fetch_then_step(cwd):
        answer = original(cwd)
        step()
        return answer

    monkeypatch.setattr(landed, "fetch_default", fetch_then_step)


def _in_any_reflog(repo: Path, commit: str) -> bool:
    return commit in git(repo, "log", "-g", "--all", "--format=%H", check=False).split()


def test_a_branch_deleted_on_the_remote_during_the_lock_wait_holds_return_and_destroy(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path, commit = _work_only_on_topic(pool, w)
    other = w.second_clone()
    _after_fetch(monkeypatch, landed, lambda: git(other, "push", "-q", "origin", "--delete", "topic"))

    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))

    monkeypatch.undo()
    assert code == EXIT_HELD
    reason = w.slot(got["slot"])["reason"]
    assert commit[:12] in reason and "gone from the remote or moved" in reason
    assert _outcome(lambda: pool.destroy_slot(got["slot"], True, True, False, str(w.repo))) == EXIT_HELD
    assert path.is_dir()
    assert _in_any_reflog(w.repo, commit)


def test_a_branch_moved_to_an_unrelated_commit_during_the_lock_wait_holds(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path, commit = _work_only_on_topic(pool, w)
    other = w.second_clone()
    commit_file(other, "unrelated.txt", "unrelated\n", "unrelated work")
    _after_fetch(monkeypatch, landed, lambda: git(other, "push", "-q", "-f", "origin", "HEAD:refs/heads/topic"))

    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert commit[:12] in w.slot(got["slot"])["reason"]


def test_a_branch_the_remote_still_has_frees_the_slot_after_asking_it(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path, commit = _work_only_on_topic(pool, w)
    asked = []
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if args[:1] == ("ls-remote",) and "--symref" not in args:
            asked.append(args)
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)
    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))
    monkeypatch.undo()

    assert code == 0
    assert asked and "refs/heads/topic" in asked[0]


def test_work_in_the_default_branch_history_makes_no_network_call(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    commit_file(path, "landed.txt", "landed\n", "landed work")
    git(path, "push", "-q", "origin", f"HEAD:refs/heads/{w.default_branch}")
    asked = []
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if args[:1] == ("ls-remote",) and "--symref" not in args:
            asked.append(args)
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)
    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))
    monkeypatch.undo()

    assert code == 0
    assert asked == []


def test_a_remote_that_cannot_be_asked_again_holds(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path, commit = _work_only_on_topic(pool, w)
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if args[:1] == ("ls-remote",) and "--symref" not in args:
            raise gitrun.GitError(list(args), 128, "fatal: simulated failure")
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)
    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))
    monkeypatch.undo()

    assert code == EXIT_HELD
    assert "cannot confirm the remote branches" in w.slot(got["slot"])["reason"]
