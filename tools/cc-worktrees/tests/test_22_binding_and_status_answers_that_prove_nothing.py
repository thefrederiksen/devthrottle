"""Guard inventory, fix round 2: the binding and status guards that no earlier test could tell from their
absence. A slot whose git metadata is ambiguous, whose directory is gone, or whose status is hidden by
configuration or cannot be read, is held with the guard's own reason."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path

import gitrun
from conftest import commit_file, git

EXIT_HELD = 3


def _return(pool, path: Path, lease: str) -> int | str:
    try:
        pool.return_slot(str(path), lease, None)
        return 0
    except pool.ToolError as ex:
        return ex.exit_code
    except Exception as ex:  # an escaped error is the failure this file looks for, so it is reported
        return f"escaped {type(ex).__name__}"


def _git_fails(monkeypatch, matches) -> None:
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if matches(args):
            raise gitrun.GitError(list(args), 128, "fatal: simulated failure")
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)


def _force_remove(func, path, _exc):
    os.chmod(path, 0o700)
    func(path)


def test_a_slot_that_is_not_the_top_of_its_own_worktree_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    os.chmod(path / ".git", 0o666)
    (path / ".git").unlink()
    git(path.parent, "init", "-q")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "not the top of its own git worktree" in w.slot(got["slot"])["reason"]


def test_a_slot_named_by_two_worktree_records_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    record = Path(git(path, "rev-parse", "--path-format=absolute", "--git-dir"))
    shutil.copytree(record, record.parent / f"{record.name}copy")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "2 worktree records for this directory" in w.slot(got["slot"])["reason"]


def test_a_slot_whose_record_is_not_the_recorded_one_is_held(local_world):
    w = local_world
    one = w.get(holder="one")
    two = w.get(holder="two")
    state = w.state_file()
    data = json.loads(state.read_text(encoding="utf-8"))
    data["slots"][one["slot"]]["gitdir"] = data["slots"][two["slot"]]["gitdir"]
    state.write_text(json.dumps(data, indent=2) + "\n", encoding="ascii")

    res = w.run("return", one["path"], "--lease", one["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "is not the one recorded when the slot was made" in w.slot(one["slot"])["reason"]


def test_a_slot_whose_directory_is_gone_is_held(local_world):
    w = local_world
    got = w.get()
    shutil.rmtree(got["path"], onerror=_force_remove)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "the worktree directory is missing" in w.slot(got["slot"])["reason"]


def test_an_untracked_file_hidden_by_status_configuration_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    git(w.repo, "config", "status.showUntrackedFiles", "no")
    (path / "notes.txt").write_text("the only copy\n", encoding="utf-8")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "1 untracked file" in w.slot(got["slot"])["reason"]
    assert (path / "notes.txt").read_text(encoding="utf-8") == "the only copy\n"


def test_destroy_of_a_slot_with_an_untracked_file_hidden_by_configuration_keeps_the_file(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0
    git(w.repo, "config", "status.showUntrackedFiles", "no")
    (path / "notes.txt").write_text("the only copy\n", encoding="utf-8")

    res = w.run("destroy", got["path"], "--yes", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert (path / "notes.txt").read_text(encoding="utf-8") == "the only copy\n"


def test_a_submodule_change_hidden_by_its_ignore_setting_is_held(local_world):
    w = local_world
    allow = ("-c", "protocol.file.allow=always")
    sub_remote = w.tmp / "sub.git"
    git(w.tmp, "init", "-q", "--bare", "-b", "main", str(sub_remote))
    seed = w.tmp / "sub-seed"
    git(w.tmp, "init", "-q", "-b", "main", str(seed))
    commit_file(seed, "inner.txt", "inner\n", "submodule first")
    git(seed, "remote", "add", "origin", str(sub_remote))
    git(seed, "push", "-q", "origin", "main")
    other = w.second_clone()
    git(other, *allow, "submodule", "add", "-q", str(sub_remote), "sub")
    git(other, "commit", "-q", "-m", "add a submodule")
    git(other, "push", "-q", "origin", w.default_branch)
    got = w.get()
    path = Path(got["path"])
    git(path, *allow, "submodule", "update", "-q", "--init")
    commit_file(path / "sub", "inner.txt", "changed\n", "unpushed submodule work")
    git(w.repo, "config", "submodule.sub.ignore", "all")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "uncommitted change" in w.slot(got["slot"])["reason"]


def test_a_status_that_fails_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    _git_fails(monkeypatch, lambda args: args[:1] == ("status",))

    code = _return(pool, Path(got["path"]), got["lease"])

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "git status failed" in w.slot(got["slot"])["reason"]


def test_a_head_that_cannot_be_read_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    _git_fails(monkeypatch, lambda args: "HEAD^{commit}" in args)

    code = _return(pool, Path(got["path"]), got["lease"])

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "HEAD cannot be read" in w.slot(got["slot"])["reason"]
