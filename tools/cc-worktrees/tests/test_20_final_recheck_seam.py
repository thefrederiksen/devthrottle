"""Fix round 2, finding 6: a commit made between the landed check and the reset is caught.

Two injections through a seam the test controls. A commit that moves HEAD without writing a reflog line
(a tool writing HEAD directly) is caught only by the recheck under HEAD.lock. A normal git commit is caught
by that recheck and, behind it, by the verification of the reflog after the reset's own line is written.
"""

from __future__ import annotations

from pathlib import Path

from conftest import git, on_any_ref

EXIT_HELD = 3


def _return_with_a_step_after_check(pool, landed, monkeypatch, path, lease, step):
    original_check = landed.check

    def check_then_step(*args, **kwargs):
        answer = original_check(*args, **kwargs)
        step()
        return answer

    monkeypatch.setattr(landed, "check", check_then_step)
    try:
        pool.return_slot(str(path), lease, None)
        return 0
    except pool.ToolError as ex:
        return ex.exit_code
    finally:
        monkeypatch.setattr(landed, "check", original_check)


def test_a_commit_that_moves_head_without_a_reflog_line_after_the_check_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    w.push_from_other_clone({"moved-on.txt": "moved\n"}, "the default branch moves on")
    made: list[str] = []

    def commit_by_writing_head():
        (path / "after-check.txt").write_text("unique after check\n", encoding="utf-8")
        git(path, "add", "after-check.txt")
        tree = git(path, "write-tree")
        parent = git(path, "rev-parse", "HEAD")
        commit = git(path, "commit-tree", tree, "-p", parent, "-m", "after the check, no reflog line")
        head = Path(git(path, "rev-parse", "--path-format=absolute", "--git-path", "HEAD"))
        head.write_text(commit + "\n", encoding="ascii")
        made.append(commit)

    code = _return_with_a_step_after_check(pool, landed, monkeypatch, path, got["lease"], commit_by_writing_head)

    assert made
    assert code == EXIT_HELD
    assert w.slot(got["slot"])["state"] == "held"
    assert (path / "after-check.txt").read_text(encoding="utf-8") == "unique after check\n"
    assert on_any_ref(w.repo, made[0]), "the commit was left on no ref"
    assert git(path, "rev-parse", "HEAD") == made[0]


def test_a_normal_commit_after_the_check_is_held_and_the_files_are_intact(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    w.push_from_other_clone({"moved-on.txt": "moved\n"}, "the default branch moves on")
    made: list[str] = []

    def commit_normally():
        (path / "after-check.txt").write_text("unique after check\n", encoding="utf-8")
        git(path, "add", "after-check.txt")
        git(path, "commit", "-q", "-m", "after the check")
        made.append(git(path, "rev-parse", "HEAD"))

    code = _return_with_a_step_after_check(pool, landed, monkeypatch, path, got["lease"], commit_normally)

    assert made
    assert code == EXIT_HELD
    assert w.slot(got["slot"])["state"] == "held"
    assert on_any_ref(w.repo, made[0])
    assert git(path, "rev-parse", "HEAD") == made[0]
    # The working tree is exactly the injected commit: nothing of the reset was written.
    assert (path / "after-check.txt").read_text(encoding="utf-8") == "unique after check\n"
    assert not (path / "moved-on.txt").exists()
    assert git(path, "status", "--porcelain") == ""
