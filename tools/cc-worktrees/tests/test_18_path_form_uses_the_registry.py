"""Fix round 2, finding 4: a slot given by path is found from the registry, never by asking git through
the slot's own .git pointer. A pointer swapped to another repository then reaches the binding check and
the slot is held with its reason."""

from __future__ import annotations

import os
from pathlib import Path

from conftest import git, make_world

EXIT_HELD = 3


def _swap_pointer(from_slot: Path, to_slot: Path) -> None:
    pointer = (to_slot / ".git").read_text(encoding="utf-8")
    os.chmod(from_slot / ".git", 0o666)
    (from_slot / ".git").unlink()
    (from_slot / ".git").write_text(pointer, encoding="utf-8")


def _two_repositories(tmp_path):
    """Two repositories on one machine, sharing one state home."""
    (tmp_path / "a").mkdir()
    (tmp_path / "b").mkdir()
    a = make_world(tmp_path / "a", "local")
    b = make_world(tmp_path / "b", "local")
    b.home = a.home
    return a, b


def test_a_pointer_into_another_repository_is_held_when_returned_by_path(tmp_path):
    a, b = _two_repositories(tmp_path)
    got_a, got_b = a.get(), b.get()
    head_b = git(Path(got_b["path"]), "rev-parse", "HEAD")
    _swap_pointer(Path(got_a["path"]), Path(got_b["path"]))

    res = a.run("return", got_a["path"], "--lease", got_a["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = a.slot(got_a["slot"])
    assert slot["state"] == "held"
    assert "another repository" in slot["reason"]
    assert git(Path(got_b["path"]), "rev-parse", "HEAD") == head_b
    assert b.slot(got_b["slot"])["state"] == "in-use"


def test_a_slot_name_from_inside_the_slot_uses_the_registered_pool(tmp_path):
    a, b = _two_repositories(tmp_path)
    got_a, got_b = a.get(), b.get()
    _swap_pointer(Path(got_a["path"]), Path(got_b["path"]))

    res = a.run("return", got_a["slot"], "--lease", got_a["lease"], "--json", cwd=Path(got_a["path"]))

    assert res.code == EXIT_HELD, res.out + res.err
    assert "another repository" in a.slot(got_a["slot"])["reason"]
    assert b.slot(got_b["slot"])["state"] == "in-use"
