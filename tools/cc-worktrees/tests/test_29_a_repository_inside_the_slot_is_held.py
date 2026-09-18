"""Fix round 3, finding 4: a git repository anywhere inside the slot. Under an ignored path it is invisible to
git status, its commits are on no ref of this repository, and git worktree remove deletes it with its history.
Any entry named .git other than the slot's own holds the slot, naming the path. The walk does not follow a
link out of the slot."""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import pytest

from conftest import commit_file, git

EXIT_HELD = 3


def _clone_with_unpushed_work(w, path: Path) -> tuple[Path, str]:
    nested = path / "bin" / "vendored"
    git(path, "clone", "-q", w.remote_url, str(nested))
    commit = commit_file(nested, "mine.txt", "only here\n", "unpushed work in a nested clone")
    assert git(path, "status", "--porcelain", "--untracked-files=all") == ""
    return nested, commit


def test_return_of_a_slot_with_a_clone_under_an_ignored_path_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    nested, commit = _clone_with_unpushed_work(w, path)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    reason = w.slot(got["slot"])["reason"]
    assert "bin/vendored/.git" in reason
    assert git(nested, "rev-parse", "HEAD") == commit


def test_destroy_of_a_free_slot_with_a_clone_under_an_ignored_path_is_refused(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == 0
    nested, commit = _clone_with_unpushed_work(w, path)

    res = w.run("destroy", got["slot"], "--repo", str(w.repo), "--yes", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert git(nested, "rev-parse", "HEAD") == commit


def test_a_git_file_under_an_ignored_path_holds_the_slot(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    linked = path / "bin" / "linked"
    linked.mkdir(parents=True)
    (linked / ".git").write_text(f"gitdir: {w.tmp / 'elsewhere.git'}\n", encoding="utf-8")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "bin/linked/.git" in w.slot(got["slot"])["reason"]


@pytest.mark.skipif(not (os.name == "nt" or sys.platform == "darwin"),
                    reason="a .git name that differs in case is only the same name on a case-insensitive file system")
def test_a_git_directory_named_in_another_case_holds_the_slot(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    (path / "bin" / "other" / ".GIT").mkdir(parents=True)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "bin/other/.GIT" in w.slot(got["slot"])["reason"]


def _link_directory(link: Path, target: Path) -> None:
    if os.name == "nt":
        # A junction needs no privilege on Windows, unlike a directory symlink.
        subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)], check=True, capture_output=True)
    else:
        os.symlink(target, link, target_is_directory=True)


def test_a_link_out_of_the_slot_to_a_repository_is_not_followed(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    outside = w.tmp / "outside"
    git(w.tmp, "clone", "-q", w.remote_url, str(outside))
    (path / "bin").mkdir()
    _link_directory(path / "bin" / "link", outside)
    assert (path / "bin" / "link" / ".git").exists()

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == 0, res.out + res.err
    assert (outside / ".git").is_dir()


def test_a_directory_that_cannot_be_listed_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    blocked = Path(got["path"]) / "bin" / "blocked"
    blocked.mkdir(parents=True)
    original = os.scandir

    def scandir(target):
        if landed.same_path(target, blocked):
            raise PermissionError(13, "simulated: access denied", str(target))
        return original(target)

    monkeypatch.setattr(os, "scandir", scandir)
    try:
        pool.return_slot(got["path"], got["lease"], None)
        code = 0
    except pool.ToolError as ex:
        code = ex.exit_code
    monkeypatch.undo()

    assert code == EXIT_HELD
    assert "cannot list bin/blocked" in w.slot(got["slot"])["reason"]


def test_a_clone_made_after_the_check_is_held_before_the_reset(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    w.push_from_other_clone({"moved-on.txt": "moved\n"}, "the default branch moves on")
    original = landed.check
    made = {}

    def check_then_clone(*args, **kwargs):
        answer = original(*args, **kwargs)
        made["nested"], made["commit"] = _clone_with_unpushed_work(w, path)
        return answer

    monkeypatch.setattr(landed, "check", check_then_clone)
    try:
        pool.return_slot(got["path"], got["lease"], None)
        code = 0
    except pool.ToolError as ex:
        code = ex.exit_code
    monkeypatch.undo()

    assert code == EXIT_HELD
    assert "bin/vendored/.git" in w.slot(got["slot"])["reason"]
    assert not (path / "moved-on.txt").exists()
    assert git(made["nested"], "rev-parse", "HEAD") == made["commit"]


def test_a_clone_made_after_the_check_is_held_before_the_removal(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    original = landed.check
    made = {}

    def check_then_clone(*args, **kwargs):
        answer = original(*args, **kwargs)
        made["nested"], made["commit"] = _clone_with_unpushed_work(w, path)
        return answer

    monkeypatch.setattr(landed, "check", check_then_clone)
    try:
        pool.destroy_slot(got["slot"], True, False, True, str(w.repo))
        code = 0
    except pool.ToolError as ex:
        code = ex.exit_code
    monkeypatch.undo()

    assert code == EXIT_HELD
    assert git(made["nested"], "rev-parse", "HEAD") == made["commit"]
