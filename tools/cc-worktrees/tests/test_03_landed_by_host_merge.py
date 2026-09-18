"""Work landed by a squash merge or a rebase gets new commit ids on the default branch.

A rebase keeps each patch, so each commit is recognised as landed on its own, while its content is still
the default tip's. A squash does not: the squashed commit is not the same patch as any one of the
worktree's commits, so squashed work is held unless its commits are also on a remote branch (for example
the pull request branch, not yet deleted).

The merges are simulated in the bare remote from a second clone, the way a host would do them."""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git

EXIT_HELD = 3


def _squash_onto_default(w, files: dict[str, str]) -> None:
    other = w.second_clone()
    commit_file(other, "unrelated.txt", "someone else\n", "unrelated work first")
    for name, content in files.items():
        (other / name).parent.mkdir(parents=True, exist_ok=True)
        (other / name).write_text(content, encoding="utf-8", newline="\n")
        git(other, "add", "--", name)
    git(other, "commit", "-q", "-m", "Feature (squashed)")
    git(other, "push", "-q", "origin", w.default_branch)


def test_squash_merged_work_whose_commits_are_on_no_remote_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit_file(path, "feature/a.txt", "one\n", "feature part 1")
    head = commit_file(path, "feature/b.txt", "two\n", "feature part 2")
    _squash_onto_default(w, {"feature/a.txt": "one\n", "feature/b.txt": "two\n"})

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    assert "2 commits are on no remote" in slot["reason"]
    assert git(path, "rev-parse", "HEAD") == head


def test_squash_merged_work_whose_branch_is_still_on_the_remote_is_landed(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit_file(path, "feature/a.txt", "one\n", "feature part 1")
    commit_file(path, "feature/b.txt", "two\n", "feature part 2")
    git(path, "push", "-q", "origin", "HEAD:refs/heads/feature-branch")
    _squash_onto_default(w, {"feature/a.txt": "one\n", "feature/b.txt": "two\n"})

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert w.slot(got["slot"])["state"] == "free"


def test_rebased_work_whose_file_changed_again_upstream_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    sha = commit_file(path, "rebased.txt", "v1\n", "rebased work")

    other = w.second_clone()
    commit_file(other, "unrelated.txt", "moved on\n", "the default branch moved on")
    git(other, "fetch", "-q", str(w.repo), sha)
    git(other, "cherry-pick", sha)
    # A later change to the same file. The patch is in the default branch's history, but the slot's
    # content is not the tip's any more, and history alone is not proof (fix round 2): held. Nothing is
    # lost here; the hold is the accepted cost of that rule.
    commit_file(other, "rebased.txt", "v2\n", "later edit")
    git(other, "push", "-q", "origin", w.default_branch)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "not in the current default branch" in w.slot(got["slot"])["reason"]


def test_partly_landed_work_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    landed_sha = commit_file(path, "landed.txt", "yes\n", "this one lands")
    commit_file(path, "stranded.txt", "no\n", "this one does not")

    other = w.second_clone()
    git(other, "fetch", "-q", str(w.repo), landed_sha)
    # -x: a different commit id for the same patch. A plain pick on the same parent in the same second
    # recreates the identical commit, which is then on the remote and not a stray commit at all.
    git(other, "cherry-pick", "-x", landed_sha)
    git(other, "push", "-q", "origin", w.default_branch)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    # The stranded commit's file is not in the default branch, so the content of the slot is not the
    # tip's and the landed patch is not proven either: both are named.
    assert "2 commits are on no remote" in w.slot(got["slot"])["reason"]
    assert (path / "stranded.txt").exists()
