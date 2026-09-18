"""Fix round 1, item 9: a branch deleted on the remote by someone else leaves a stale tracking ref here.

The fetch must prune it; otherwise the stale ref counts as proof the commit is on a remote.
"""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git

EXIT_HELD = 3


def test_commit_whose_remote_branch_was_deleted_from_another_clone_is_held(world):
    got = world.get()
    path = Path(got["path"])
    commit_file(path, f"topic-{got['lease'][:6]}.txt", "topic\n", "topic work")
    branch = world.unique_branch()
    git(path, "push", "-q", "origin", f"HEAD:refs/heads/{branch}")
    git(world.repo, "fetch", "-q", "origin")
    other = world.second_clone()
    git(other, "push", "-q", "origin", "--delete", branch)
    assert git(world.repo, "show-ref", "--verify", f"refs/remotes/origin/{branch}", check=False)

    res = world.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "on no remote" in world.slot(got["slot"])["reason"]
