"""Fix round 1, item 8: a commit abandoned in the slot survives only in the slot's HEAD reflog.

Freeing the slot leaves it to be expired, and destroying the slot deletes that reflog outright. So
every commit the reflog gained since the slot was handed out must pass the landed check too.
"""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git

EXIT_HELD = 3


def test_return_holds_a_slot_whose_abandoned_commit_is_only_in_the_reflog(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    sha = commit_file(path, "abandoned.txt", "abandoned work\n", "abandoned")
    git(path, "reset", "-q", "--hard", f"origin/{w.default_branch}")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    assert sha[:12] in slot["reason"]


def test_destroy_refuses_a_free_slot_whose_abandoned_commit_is_only_in_the_reflog(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0
    commit_file(path, "abandoned.txt", "abandoned work\n", "abandoned")
    git(path, "reset", "-q", "--hard", f"origin/{w.default_branch}")

    res = w.run("destroy", got["path"], "--yes", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert path.is_dir()


def test_landed_work_from_an_earlier_lease_does_not_hold_a_later_return(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit_file(path, "pushed.txt", "pushed\n", "pushed")
    git(path, "push", "-q", "origin", f"HEAD:refs/heads/{w.default_branch}")
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0

    again = w.get()
    assert again["slot"] == got["slot"]
    res = w.run("return", again["path"], "--lease", again["lease"], "--json")

    assert res.code == 0, res.out + res.err
