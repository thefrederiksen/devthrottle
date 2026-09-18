"""Fix round 2, finding 1: the same patch in the default branch's HISTORY is not proof that its content
is in the default branch NOW.

A commit on no remote branch that git cherry matches counts as landed only if, for every path any stray
commit of that start touches, the start's content equals the current default tip's. A patch landed and
then reverted upstream is held; so is one whose paths upstream changed again afterwards.
"""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git, on_any_ref

EXIT_HELD = 3


def _land_then(w, name: str, content: str, then: str) -> None:
    """Land `name` with `content` on the default branch from another clone, then revert it or change it again."""
    other = w.second_clone()
    commit_file(other, name, content, "the same patch, landed from elsewhere")
    git(other, "push", "-q", "origin", w.default_branch)
    if then == "revert":
        git(other, "revert", "--no-edit", "HEAD")
    else:
        commit_file(other, name, "changed again upstream\n", "upstream changes it again")
    git(other, "push", "-q", "origin", w.default_branch)


def test_a_patch_reverted_upstream_is_held_and_its_file_kept(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    sha = commit_file(path, "private.txt", "private work\n", "private work")
    _land_then(w, "private.txt", "private work\n", "revert")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    assert "not in the current default branch" in slot["reason"]
    assert (path / "private.txt").read_text(encoding="utf-8") == "private work\n"
    assert on_any_ref(w.repo, sha)


def test_a_patch_whose_path_upstream_changed_again_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit_file(path, "shared.txt", "version one\n", "version one")
    _land_then(w, "shared.txt", "version one\n", "change")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "not in the current default branch" in w.slot(got["slot"])["reason"]
    assert (path / "shared.txt").read_text(encoding="utf-8") == "version one\n"


def test_a_reflog_only_commit_whose_patch_was_reverted_upstream_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    base = git(path, "rev-parse", "HEAD")
    sha = commit_file(path, "abandoned.txt", "abandoned work\n", "abandoned work")
    git(path, "reset", "-q", "--hard", base)
    _land_then(w, "abandoned.txt", "abandoned work\n", "revert")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    assert sha[:12] in slot["reason"]
    assert sha in git(path, "reflog", "show", "--format=%H", "HEAD").split()


def test_a_patch_whose_content_is_in_the_current_default_branch_still_counts_as_landed(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit_file(path, "kept.txt", "kept\n", "my message")
    other = w.second_clone()
    commit_file(other, "kept.txt", "kept\n", "a different message")
    commit_file(other, "unrelated.txt", "unrelated\n", "unrelated work after it")
    git(other, "push", "-q", "origin", w.default_branch)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert w.slot(got["slot"])["state"] == "free"
