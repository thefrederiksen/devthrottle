"""Fix round 2, item C: the reset re-checks the files at the moment it writes them.

The status list gathered before the reset is an observation an editor can outdate. The reset is a two-tree
merge from the checked HEAD, which refuses to write over an untracked file or a local change, path by path,
before it writes anything.
"""

from __future__ import annotations

from pathlib import Path

EXIT_HELD = 3


def test_an_untracked_file_written_onto_a_target_path_just_before_the_reset_is_kept(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    w.push_from_other_clone({"arrives.txt": "from the default branch\n"}, "a new file on the default branch")
    original_overlaps = landed.ignored_overlaps
    written: list[Path] = []

    def overlaps_then_an_editor_writes(worktree, target):
        answer = original_overlaps(worktree, target)
        # After every check under HEAD.lock, before the files are written.
        (path / "arrives.txt").write_text("my unsaved work\n", encoding="utf-8")
        written.append(path / "arrives.txt")
        return answer

    monkeypatch.setattr(landed, "ignored_overlaps", overlaps_then_an_editor_writes)
    try:
        pool.return_slot(str(path), got["lease"], None)
        code = 0
    except pool.ToolError as ex:
        code = ex.exit_code
    monkeypatch.setattr(landed, "ignored_overlaps", original_overlaps)

    assert written, "the seam never ran"
    assert (path / "arrives.txt").read_text(encoding="utf-8") == "my unsaved work\n"
    assert code == EXIT_HELD
    assert w.slot(got["slot"])["state"] == "held"


def test_a_tracked_file_edited_just_before_the_reset_is_kept(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    w.push_from_other_clone({"README.md": "changed on the default branch\n"}, "change the readme")
    original_overlaps = landed.ignored_overlaps

    def overlaps_then_an_editor_saves(worktree, target):
        answer = original_overlaps(worktree, target)
        (path / "README.md").write_text("my unsaved edit\n", encoding="utf-8")
        return answer

    monkeypatch.setattr(landed, "ignored_overlaps", overlaps_then_an_editor_saves)
    try:
        pool.return_slot(str(path), got["lease"], None)
        code = 0
    except pool.ToolError as ex:
        code = ex.exit_code
    monkeypatch.setattr(landed, "ignored_overlaps", original_overlaps)

    assert (path / "README.md").read_text(encoding="utf-8") == "my unsaved edit\n"
    assert code == EXIT_HELD
