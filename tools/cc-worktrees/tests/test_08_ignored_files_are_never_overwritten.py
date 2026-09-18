"""Fix round 1, finding 4: a reset never writes over, or deletes, an ignored file.

Ignored build output is kept on purpose. If the default branch now TRACKS a path that is an ignored
file in the slot, the reset would overwrite it; if the default branch stops ignoring it, a clean would
delete it. Either way the slot is held and the file is untouched.
"""

from __future__ import annotations

from pathlib import Path

EXIT_HELD = 3


def test_ignored_file_that_the_default_branch_now_tracks_is_held_not_overwritten(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    build = path / "bin" / "out.txt"
    build.parent.mkdir()
    build.write_text("precious build artifact\n", encoding="utf-8")
    w.push_from_other_clone({"bin/out.txt": "new tracked file\n"}, "track a former build path", force_add=True)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    assert "bin/out.txt" in slot["reason"]
    assert build.read_text(encoding="utf-8") == "precious build artifact\n"


def test_ignored_file_under_a_path_the_default_branch_now_tracks_as_a_file_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    build = path / "bin" / "out.txt"
    build.parent.mkdir()
    build.write_text("precious build artifact\n", encoding="utf-8")
    w.push_from_other_clone({"bin": "now a tracked file\n"}, "track bin as a file", force_add=True)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert build.read_text(encoding="utf-8") == "precious build artifact\n"


def test_ignored_file_the_default_branch_stops_ignoring_is_not_deleted(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    build = path / "bin" / "out.txt"
    build.parent.mkdir()
    build.write_text("precious build artifact\n", encoding="utf-8")
    w.push_from_other_clone({".gitignore": "obj/\n"}, "stop ignoring bin")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert build.read_text(encoding="utf-8") == "precious build artifact\n"
    assert res.code == EXIT_HELD, res.out + res.err
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    assert "no longer ignore" in slot["reason"]
