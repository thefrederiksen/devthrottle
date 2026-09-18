"""The treehouse loss case: a clean worktree holding one commit that was never pushed.

Treehouse's return reset such a worktree and reported success, and the commit became unreachable.
cc-worktrees must hold the worktree instead, and the commit must still be there afterwards.
"""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git

EXIT_HELD = 3


def test_return_with_unpushed_detached_commit_is_held_and_commit_survives(world):
    got = world.get(holder="loss-case")
    path = Path(got["path"])
    assert git(path, "status", "--porcelain") == ""

    sha = commit_file(path, "work.txt", "unpushed work\n", "work that was never pushed")

    res = world.run("return", str(path), "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, f"expected exit {EXIT_HELD}, got {res.code}: {res.out} {res.err}"
    slot = world.slot(got["slot"])
    assert slot["state"] == "held"
    assert "on no remote" in slot["reason"]
    assert git(world.repo, "cat-file", "-t", sha) == "commit"
    assert git(path, "rev-parse", "HEAD") == sha
    assert (path / "work.txt").read_text(encoding="utf-8") == "unpushed work\n"
