"""Fix round 3, finding 1: a tracked file flagged assume-unchanged or skip-worktree hides a local edit from
git status, from update-index --refresh and from git worktree remove. Every one of those answers "clean",
so a slot with either flag is held by name, whatever the file holds, and its edit survives."""

from __future__ import annotations

from pathlib import Path

import pytest

from conftest import git

EXIT_HELD = 3
FLAGS = ["--assume-unchanged", "--skip-worktree"]
EDIT = "hello\nMY LOCAL EDIT\n"


def _hide_an_edit(path: Path, flag: str) -> None:
    (path / "README.md").write_text(EDIT, encoding="utf-8", newline="\n")
    git(path, "update-index", flag, "README.md")
    assert git(path, "status", "--porcelain", "--untracked-files=all") == ""


def _edit_kept(path: Path) -> bool:
    return (path / "README.md").read_text(encoding="utf-8") == EDIT


@pytest.mark.parametrize("flag", FLAGS)
def test_return_of_a_slot_with_a_hidden_edit_is_held(local_world, flag):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    _hide_an_edit(path, flag)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    reason = w.slot(got["slot"])["reason"]
    assert "assume-unchanged or skip-worktree" in reason and "README.md" in reason
    assert _edit_kept(path)


@pytest.mark.parametrize("flag", FLAGS)
def test_destroy_of_a_free_slot_with_a_hidden_edit_is_refused(local_world, flag):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == 0
    _hide_an_edit(path, flag)

    res = w.run("destroy", got["slot"], "--repo", str(w.repo), "--yes", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert path.is_dir() and _edit_kept(path)
    assert "README.md" in w.slot(got["slot"])["reason"]


@pytest.mark.parametrize("flag", FLAGS)
def test_destroy_allow_in_use_of_a_slot_with_a_hidden_edit_is_refused(local_world, flag):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    _hide_an_edit(path, flag)

    res = w.run("destroy", got["slot"], "--repo", str(w.repo), "--yes", "--allow-in-use", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert path.is_dir() and _edit_kept(path)


@pytest.mark.parametrize("flag", FLAGS)
def test_get_skips_a_free_slot_with_a_hidden_edit_and_holds_it(local_world, flag):
    w = local_world
    got = w.get(holder="first")
    path = Path(got["path"])
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == 0
    _hide_an_edit(path, flag)

    second = w.get(holder="second")

    assert second["slot"] != got["slot"]
    assert w.slot(got["slot"])["state"] == "held"
    assert _edit_kept(path)


def test_index_flags_that_cannot_be_read_are_held(in_process, monkeypatch):
    import gitrun

    w, pool, _ = in_process
    got = pool.get(str(w.repo), "owner", 4)
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if args[:2] == ("ls-files", "-v"):
            raise gitrun.GitError(list(args), 128, "fatal: simulated failure")
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)
    with pytest.raises(pool.ToolError) as raised:
        pool.return_slot(got["path"], got["lease"], None)
    monkeypatch.undo()

    assert raised.value.exit_code == EXIT_HELD
    assert "cannot read the index flags" in w.slot(got["slot"])["reason"]
