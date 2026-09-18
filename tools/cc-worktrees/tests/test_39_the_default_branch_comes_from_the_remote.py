"""The default branch is READ FROM THE REMOTE, every time, and never assumed.

"main" is a habit, not a fact: the Azure DevOps testbed's default branch is `develop`, and plenty of
repositories use `trunk`, `master` or something else again. The default branch is what every commit is
measured against, so taking the wrong one either holds work that landed or - worse - measures against a
branch the work never landed on. It comes from `git ls-remote --symref origin HEAD`, which both hosts
answer and which needs no host tool at all.

The local `refs/remotes/origin/HEAD` is NOT that answer. It is written once when the repository is cloned
and then left alone for ever, so it still names the branch the remote had years ago.
"""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git, make_world

EXIT_HELD = 3


def test_a_remote_whose_default_branch_is_not_main_is_used_as_it_is(tmp_path):
    w = make_world(tmp_path, "local", default_branch="trunk")
    got = w.get()

    assert got["base"] == "trunk"
    path = Path(got["path"])
    sha = commit_file(path, "landed.txt", "on trunk\n", "work for trunk")
    other = w.second_clone()
    git(other, "fetch", "-q", str(w.repo), sha)
    git(other, "merge", "-q", "--ff-only", sha)
    git(other, "push", "-q", "origin", "trunk")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert res.data["base"] == "trunk"
    assert w.slot(got["slot"])["state"] == "free"


def test_a_stale_origin_head_in_the_clone_is_not_the_default_branch(tmp_path):
    """The clone is told origin/HEAD is another branch. The remote's own answer still wins."""
    w = make_world(tmp_path, "local", default_branch="trunk")
    other = w.second_clone()
    git(other, "checkout", "-q", "-b", "not-the-default")
    commit_file(other, "elsewhere.txt", "not the default branch\n", "a branch that is not the default")
    git(other, "push", "-q", "origin", "not-the-default")
    git(w.repo, "fetch", "-q", "origin")
    git(w.repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/not-the-default")
    assert git(w.repo, "symbolic-ref", "refs/remotes/origin/HEAD") == "refs/remotes/origin/not-the-default"

    got = w.get()
    assert got["base"] == "trunk"

    path = Path(got["path"])
    sha = commit_file(path, "landed.txt", "on trunk\n", "work for trunk")
    third = w.second_clone()
    git(third, "fetch", "-q", str(w.repo), sha)
    git(third, "merge", "-q", "--ff-only", sha)
    git(third, "push", "-q", "origin", "trunk")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert res.data["base"] == "trunk"


def test_a_remote_that_names_no_default_branch_is_cannot_verify(tmp_path):
    w = make_world(tmp_path, "local", default_branch="trunk")
    got = w.get()
    path = Path(got["path"])
    commit_file(path, "work.txt", "mine\n", "work nobody has landed")
    # The remote stops naming a default branch. Nothing about the work changed.
    git(Path(w.remote_url), "symbolic-ref", "HEAD", "refs/heads/there-is-no-such-branch")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    assert "cannot verify" in slot["reason"]
    assert "which branch is its default" in slot["reason"]
    assert (path / "work.txt").exists()
