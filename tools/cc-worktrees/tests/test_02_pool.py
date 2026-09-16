"""Handing out, returning and reusing worktrees; holding what is not proven landed."""

from __future__ import annotations

import os
from pathlib import Path

from conftest import commit_file, git, make_world

EXIT_HELD = 3
EXIT_POOL_FULL = 4


def test_get_twice_gives_two_slots_and_a_returned_slot_is_reused_with_build_output(world):
    first = world.get(holder="a")
    second = world.get(holder="b")
    assert first["slot"] != second["slot"]
    assert first["path"] != second["path"]
    first_path = Path(first["path"])
    assert first_path.parent.name == f"{world.repo.name}.worktrees"
    assert git(first_path, "rev-parse", "HEAD") == git(world.repo, "rev-parse", f"origin/{world.default_branch}")
    assert git(first_path, "symbolic-ref", "-q", "HEAD", check=False) == ""  # detached

    build = first_path / "bin" / "out.txt"
    build.parent.mkdir()
    build.write_text("built\n", encoding="utf-8")
    if not git(first_path, "check-ignore", "bin/out.txt", check=False):
        # A hosted testbed may not ignore bin/; ignore it for this clone only.
        exclude = Path(git(first_path, "rev-parse", "--path-format=absolute", "--git-path", "info/exclude"))
        exclude.parent.mkdir(parents=True, exist_ok=True)
        with exclude.open("a", encoding="utf-8") as f:
            f.write("\nbin/\n")

    res = world.run("return", first["path"], "--lease", first["lease"], "--json")
    assert res.code == 0, res.out + res.err
    assert world.slot(first["slot"])["state"] == "free"

    again = world.get(holder="c")
    assert again["slot"] == first["slot"]
    assert again["reused"] is True
    assert again["lease"] != first["lease"]
    assert build.read_text(encoding="utf-8") == "built\n"


def test_uncommitted_change_is_held(world):
    got = world.get()
    path = Path(got["path"])
    readme = next(p for p in path.iterdir() if p.is_file() and p.name != ".git")
    readme.write_text(readme.read_text(encoding="utf-8") + "edited\n", encoding="utf-8")

    res = world.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = world.slot(got["slot"])
    assert slot["state"] == "held"
    assert "uncommitted" in slot["reason"]
    assert readme.read_text(encoding="utf-8").endswith("edited\n")


def test_untracked_file_is_held(world):
    got = world.get()
    path = Path(got["path"])
    (path / "notes-not-ignored.txt").write_text("keep me\n", encoding="utf-8")

    res = world.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = world.slot(got["slot"])
    assert slot["state"] == "held"
    assert "untracked" in slot["reason"]
    assert (path / "notes-not-ignored.txt").exists()


def test_commit_pushed_to_a_remote_branch_is_landed(world):
    got = world.get()
    path = Path(got["path"])
    commit_file(path, f"pushed-{got['lease'][:6]}.txt", "pushed\n", "pushed work")
    branch = world.unique_branch()
    git(path, "push", "-q", "origin", f"HEAD:refs/heads/{branch}")

    res = world.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert world.slot(got["slot"])["state"] == "free"
    assert git(path, "rev-parse", "HEAD") == git(world.repo, "rev-parse", f"origin/{world.default_branch}")


def test_unreachable_remote_is_held_cannot_verify(local_world):
    w = local_world
    got = w.get()
    git(w.repo, "remote", "set-url", "origin", str(w.tmp / "no-such-remote.git"))

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    assert slot["reason"].startswith("cannot verify")


def test_pool_full_refuses_with_distinct_code_and_creates_nothing(local_world):
    w = local_world
    a = w.get(holder="alpha", pool_size=2)
    b = w.get(holder="beta", pool_size=2)

    res = w.run("get", "--repo", str(w.repo), "--holder", "gamma", "--pool-size", "2")

    assert res.code == EXIT_POOL_FULL, res.out + res.err
    assert a["slot"] in res.out and b["slot"] in res.out
    assert "alpha" in res.out and "beta" in res.out
    assert sorted(os.listdir(w.repo.parent / f"{w.repo.name}.worktrees")) == sorted([a["slot"], b["slot"]])
    assert len(w.list()) == 2


def test_default_branch_is_read_from_the_remote_not_assumed(tmp_path):
    w = make_world(tmp_path, "local", default_branch="develop")
    bare = Path(w.remote_url)
    # A main branch that is ahead of develop, and a local origin/HEAD that wrongly says main.
    other = w.second_clone()
    git(other, "checkout", "-q", "-b", "main")
    commit_file(other, "main-only.txt", "main\n", "only on main")
    git(other, "push", "-q", "origin", "main")
    git(w.repo, "fetch", "-q", "origin")
    git(w.repo, "remote", "set-head", "origin", "main")
    assert git(bare, "symbolic-ref", "HEAD") == "refs/heads/develop"

    got = w.get()

    assert got["base"] == "develop"
    assert git(Path(got["path"]), "rev-parse", "HEAD") == git(w.repo, "rev-parse", "origin/develop")
    assert not (Path(got["path"]) / "main-only.txt").exists()


def test_stale_lease_is_refused_and_changes_nothing(local_world):
    w = local_world
    got = w.get(holder="first")
    before = w.slot(got["slot"])

    res = w.run("return", got["path"], "--lease", "0" * 32, "--json")

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "lease-mismatch"
    after = w.slot(got["slot"])
    assert after == before
    assert after["state"] == "in-use" and after["holder"] == "first"


def test_corrupt_state_file_brings_slots_back_held(local_world):
    w = local_world
    a = w.get()
    b = w.get()
    w.run("return", a["path"], "--lease", a["lease"])
    state_files = list((w.home / "pools").glob("*.json"))
    assert len(state_files) == 1
    state_files[0].write_text("{ this is not json", encoding="utf-8")

    slots = {s["slot"]: s for s in w.list()}

    assert set(slots) == {a["slot"], b["slot"]}
    for slot in slots.values():
        assert slot["state"] == "held"
        assert slot["reason"] == "state lost, cannot verify"
    res = w.run("get", "--repo", str(w.repo), "--holder", "x", "--pool-size", "2")
    assert res.code == EXIT_POOL_FULL
