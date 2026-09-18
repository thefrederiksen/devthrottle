"""Fix round 1, finding 2: destroy proves the work landed at that moment, whatever the state says.

A free slot is only free as of the last check. Someone can commit in it afterwards.
"""

from __future__ import annotations

from pathlib import Path

from conftest import commit_file, git

EXIT_HELD = 3


def test_destroy_refuses_a_free_slot_that_gained_an_unpushed_commit(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0
    assert w.slot(got["slot"])["state"] == "free"
    sha = commit_file(path, "after-return.txt", "work after the return\n", "unpushed after return")

    res = w.run("destroy", got["path"], "--yes", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert res.data.get("removed") is not True
    assert path.is_dir()
    assert git(path, "rev-parse", "HEAD") == sha
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    assert "on no remote" in slot["reason"]


def test_destroy_dry_run_also_refuses_unlanded_work(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0
    commit_file(path, "after-return.txt", "work after the return\n", "unpushed after return")

    res = w.run("destroy", got["path"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert path.is_dir()


def test_destroy_held_slot_with_allow_held_still_refuses_unlanded_work(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    commit_file(path, "held.txt", "held work\n", "held work")
    assert w.run("return", got["path"], "--lease", got["lease"]).code == EXIT_HELD

    res = w.run("destroy", got["path"], "--yes", "--allow-held", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert path.is_dir()
    assert (path / "held.txt").exists()
