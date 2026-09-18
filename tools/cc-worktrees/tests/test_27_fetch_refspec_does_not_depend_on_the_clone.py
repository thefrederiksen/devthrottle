"""Fix round 3, finding 5: the fetch names its own refspec. A clone whose configured refspec covers only the
default branch (what git clone --single-branch leaves) would otherwise never prune a tracking ref for any
other branch, and a branch the remote deleted would still count as proof."""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git

EXIT_HELD = 3


def test_a_stale_tracking_ref_outside_a_narrowed_fetch_refspec_is_not_proof(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit = commit_file(path, "topic.txt", "topic\n", "topic work")
    git(path, "push", "-q", "origin", "HEAD:refs/heads/topic")
    git(w.repo, "fetch", "-q", "origin")
    git(w.repo, "config", "remote.origin.fetch", f"+refs/heads/{w.default_branch}:refs/remotes/origin/{w.default_branch}")
    git(w.second_clone(), "push", "-q", "origin", "--delete", "topic")
    assert git(w.repo, "show-ref", "--verify", "refs/remotes/origin/topic", check=False)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert commit[:12] in w.slot(got["slot"])["reason"]
    assert not git(w.repo, "show-ref", "--verify", "refs/remotes/origin/topic", check=False)
    assert git(path, "rev-parse", "HEAD") == commit
