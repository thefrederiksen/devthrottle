"""Fix round 3, the record read: exactly one worktree record must name the slot, and a record whose back-link
cannot be read could be a second one. A record with no readable gitdir file holds the slot as cannot verify;
it is never skipped."""

from __future__ import annotations

from pathlib import Path

from conftest import git

EXIT_HELD = 3


def _second_record(w, got) -> Path:
    path = Path(got["path"])
    record = Path(git(path, "rev-parse", "--path-format=absolute", "--git-dir"))
    other = record.parent / f"{record.name}other"
    other.mkdir()
    return other


def test_a_second_record_whose_gitdir_is_a_directory_holds_the_slot(local_world):
    w = local_world
    got = w.get()
    (_second_record(w, got) / "gitdir").mkdir()

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "cannot verify" in w.slot(got["slot"])["reason"]


def test_a_second_record_with_no_gitdir_holds_the_slot(local_world):
    w = local_world
    got = w.get()
    _second_record(w, got)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "cannot verify" in w.slot(got["slot"])["reason"]


def test_worktree_records_that_cannot_be_listed_hold_the_slot(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    records = w.repo / ".git" / "worktrees"
    original = Path.iterdir

    def iterdir(self):
        if landed.same_path(self, records):
            raise PermissionError(13, "simulated: access denied", str(self))
        return original(self)

    monkeypatch.setattr(Path, "iterdir", iterdir)
    try:
        pool.return_slot(got["path"], got["lease"], None)
        code = 0
    except pool.ToolError as ex:
        code = ex.exit_code
    monkeypatch.undo()

    assert code == EXIT_HELD
    assert "cannot list the worktree records" in w.slot(got["slot"])["reason"]
