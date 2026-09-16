"""Fix round 1, finding 5: a slot's .git pointer must lead to that slot's own git metadata.

Otherwise every git answer describes some other worktree, and a reset would move the other one's HEAD.
"""

from __future__ import annotations

import os
from pathlib import Path

from conftest import git

EXIT_HELD = 3


def test_swapped_git_pointer_is_held_and_neither_slot_is_touched(local_world):
    w = local_world
    one = w.get(holder="one")
    two = w.get(holder="two")
    p1, p2 = Path(one["path"]), Path(two["path"])
    pointer = (p2 / ".git").read_text(encoding="utf-8")
    os.chmod(p1 / ".git", 0o666)
    (p1 / ".git").unlink()
    (p1 / ".git").write_text(pointer, encoding="utf-8")
    head_two = git(p2, "rev-parse", "HEAD")
    w.push_from_other_clone({"moved-on.txt": "moved\n"}, "the default branch moves on")

    res = w.run("return", one["path"], "--lease", one["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(one["slot"])
    assert slot["state"] == "held"
    assert ".git" in slot["reason"]
    assert git(p2, "rev-parse", "HEAD") == head_two
    assert not (p2 / "moved-on.txt").exists()
    other = w.slot(two["slot"])
    assert other["state"] == "in-use" and other["holder"] == "two"
    lock = Path(git(p2, "rev-parse", "--path-format=absolute", "--git-path", "HEAD.lock"))
    assert not lock.exists()
