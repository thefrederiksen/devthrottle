"""Fix round 1, finding 1: every commit is checked on its own.

A worktree whose FINAL files happen to match the default branch still holds commits whose content is
nowhere else. Matching the final snapshot is not proof for the commits behind it, so such a worktree
is held.
"""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git

EXIT_HELD = 3


def test_intermediate_commit_is_held_when_the_final_files_match_the_default_branch(local_world):
    # Local remote only: the scenario needs a commit landed on the DEFAULT branch, and a hosted run
    # may push only to throwaway branches it creates and deletes.
    world = local_world
    got = world.get()
    path = Path(got["path"])
    name = f"x-{got['lease'][:6]}.txt"
    intermediate = commit_file(path, name, "important intermediate\n", "unique intermediate")
    final = commit_file(path, name, "final\n", "local final")
    world.push_from_other_clone({name: "final\n"}, "the same final file, landed from elsewhere")

    res = world.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, f"expected held, got {res.code}: {res.out} {res.err}"
    slot = world.slot(got["slot"])
    assert slot["state"] == "held"
    assert "on no remote" in slot["reason"]
    assert git(path, "rev-parse", "HEAD") == final
    assert git(world.repo, "cat-file", "-t", intermediate) == "commit"


def test_same_patch_with_a_different_message_counts_as_landed(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit_file(path, "renamed-message.txt", "content\n", "my message")
    w.push_from_other_clone({"renamed-message.txt": "content\n"}, "a completely different message")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert w.slot(got["slot"])["state"] == "free"


def test_merge_commit_on_no_remote_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    base = git(path, "rev-parse", "HEAD")
    side = commit_file(path, "side.txt", "side\n", "side")
    git(path, "checkout", "-q", "--detach", base)
    commit_file(path, "main-line.txt", "main line\n", "main line")
    git(path, "merge", "-q", "--no-ff", "-m", "merge side", side)
    merge = git(path, "rev-parse", "HEAD")
    # Both parents land on the default branch as separate patches; the merge itself is nowhere.
    w.push_from_other_clone({"side.txt": "side\n"}, "side, landed")
    w.push_from_other_clone({"main-line.txt": "main line\n"}, "main line, landed")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert w.slot(got["slot"])["state"] == "held"
    assert git(path, "rev-parse", "HEAD") == merge
