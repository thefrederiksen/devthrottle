"""Work landed by a squash merge or a rebase gets new commit ids on the default branch. The worktree's
own commits are then on no remote, and the check must still recognise the work as landed.

The merges are simulated in the bare remote from a second clone, the way a host would do them."""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git

EXIT_HELD = 3


def test_squash_merged_work_is_landed(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit_file(path, "feature/a.txt", "one\n", "feature part 1")
    commit_file(path, "feature/b.txt", "two\n", "feature part 2")

    other = w.second_clone()
    commit_file(other, "unrelated.txt", "someone else\n", "unrelated work first")
    (other / "feature").mkdir()
    (other / "feature" / "a.txt").write_text("one\n", encoding="utf-8", newline="\n")
    (other / "feature" / "b.txt").write_text("two\n", encoding="utf-8", newline="\n")
    git(other, "add", "feature")
    git(other, "commit", "-q", "-m", "Feature (squashed)")
    git(other, "push", "-q", "origin", w.default_branch)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert w.slot(got["slot"])["state"] == "free"


def test_rebased_work_is_landed_even_after_the_file_changed_again(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    sha = commit_file(path, "rebased.txt", "v1\n", "rebased work")

    other = w.second_clone()
    commit_file(other, "unrelated.txt", "moved on\n", "the default branch moved on")
    git(other, "fetch", "-q", str(w.repo), sha)
    git(other, "cherry-pick", sha)
    # A later change to the same file: the content check alone could no longer prove it;
    # the patch-id match (git cherry) does.
    commit_file(other, "rebased.txt", "v2\n", "later edit")
    git(other, "push", "-q", "origin", w.default_branch)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert w.slot(got["slot"])["state"] == "free"


def test_partly_landed_work_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    landed_sha = commit_file(path, "landed.txt", "yes\n", "this one lands")
    commit_file(path, "stranded.txt", "no\n", "this one does not")

    other = w.second_clone()
    git(other, "fetch", "-q", str(w.repo), landed_sha)
    git(other, "cherry-pick", landed_sha)
    git(other, "push", "-q", "origin", w.default_branch)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "1 commit is on no remote" in w.slot(got["slot"])["reason"]
    assert (path / "stranded.txt").exists()
