"""Guard inventory, fix round 2: the reset and removal guards that no earlier test could tell from their
absence. Each case changes the slot at a moment the test controls - after the check, at the lock, at the
write, at the removal - and requires the slot to be held with nothing written, moved or removed."""

from __future__ import annotations

import os
import time
from pathlib import Path

import gitrun
from conftest import commit_file, git

EXIT_HELD = 3
EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"


def _outcome(call) -> int | str:
    try:
        call()
        return 0
    except Exception as ex:  # a ToolError carries its exit code; anything else escaped instead of a hold
        code = getattr(ex, "exit_code", None)
        return code if code is not None else f"escaped {type(ex).__name__}"


def _after_check(monkeypatch, landed, step) -> None:
    original = landed.check

    def check_then_step(*args, **kwargs):
        answer = original(*args, **kwargs)
        step()
        return answer

    monkeypatch.setattr(landed, "check", check_then_step)


def _around_git(monkeypatch, matches, before=None, after=None, fail=False) -> None:
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if not matches(args):
            return original(cwd, *args, **kwargs)
        if before:
            before(cwd)
        if fail:
            raise gitrun.GitError(list(args), 128, "fatal: simulated failure")
        answer = original(cwd, *args, **kwargs)
        if after:
            after(cwd)
        return answer

    monkeypatch.setattr(gitrun, "run", run)


def _git_dir(path: Path) -> Path:
    return Path(git(path, "rev-parse", "--path-format=absolute", "--git-dir"))


def _slot_moving_on(pool, w):
    got = pool.get(str(w.repo), "owner", 4)
    w.push_from_other_clone({"moved-on.txt": "moved\n"}, "the default branch moves on")
    return got, Path(got["path"])


# --- ignored files the default branch now tracks ---------------------------------------------------


def test_an_ignored_file_differing_only_in_case_from_a_newly_tracked_path_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    (path / "bin").mkdir()
    (path / "bin" / "Tool.dll").write_text("local build\n", encoding="utf-8")
    w.push_from_other_clone({"bin/tool.dll": "tracked\n"}, "track a build file", force_add=True)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "ignored" in w.slot(got["slot"])["reason"]
    assert (path / "bin" / "Tool.dll").read_text(encoding="utf-8") == "local build\n"


def test_an_ignored_file_where_the_default_branch_now_tracks_a_directory_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    (path / "bin").mkdir()
    (path / "bin" / "cache").write_text("local cache\n", encoding="utf-8")
    w.push_from_other_clone({"bin/cache/readme.txt": "tracked\n"}, "track a directory", force_add=True)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "ignored" in w.slot(got["slot"])["reason"]
    assert (path / "bin" / "cache").read_text(encoding="utf-8") == "local cache\n"


def test_ignored_files_that_cannot_be_listed_are_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path = _slot_moving_on(pool, w)
    head = git(path, "rev-parse", "HEAD")
    _around_git(monkeypatch, lambda args: args[:1] == ("ls-files",) and "--ignored" in args, fail=True)

    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "cannot list ignored files" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head
    assert not (path / "moved-on.txt").exists()


# --- HEAD.lock ---------------------------------------------------------------------------------------


def test_a_head_lock_held_by_another_git_command_refuses_the_reset_and_is_left_alone(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    head = git(path, "rev-parse", "HEAD")
    w.push_from_other_clone({"moved-on.txt": "moved\n"}, "the default branch moves on")
    lock = _git_dir(path) / "HEAD.lock"
    lock.write_text("another git command\n", encoding="ascii")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "HEAD.lock exists" in w.slot(got["slot"])["reason"]
    assert lock.read_text(encoding="ascii") == "another git command\n"
    assert git(path, "rev-parse", "HEAD") == head
    assert not (path / "moved-on.txt").exists()


def test_a_head_lock_that_cannot_be_created_refuses_the_reset(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path = _slot_moving_on(pool, w)
    head = git(path, "rev-parse", "HEAD")

    class _Os:
        def __getattr__(self, name):
            return getattr(os, name)

        @staticmethod
        def open(file, flags, mode=0o777):
            if str(file).endswith("HEAD.lock"):
                raise PermissionError(13, "Access is denied", str(file))
            return os.open(file, flags, mode)

    monkeypatch.setattr(landed, "os", _Os())

    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "cannot lock HEAD" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head


# --- the recheck under HEAD.lock ---------------------------------------------------------------------


def test_a_git_pointer_swapped_after_the_check_is_held_with_its_reason(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    other = pool.get(str(w.repo), "other", 4)
    path, other_path = Path(got["path"]), Path(other["path"])
    other_head = git(other_path, "rev-parse", "HEAD")

    def swap():
        pointer = (other_path / ".git").read_text(encoding="utf-8")
        os.chmod(path / ".git", 0o666)
        (path / ".git").unlink()
        (path / ".git").write_text(pointer, encoding="utf-8")

    _after_check(monkeypatch, landed, swap)

    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "points at the git metadata of another worktree" in w.slot(got["slot"])["reason"]
    assert git(other_path, "rev-parse", "HEAD") == other_head


def test_a_commit_abandoned_after_the_check_is_held_and_nothing_is_written(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path = _slot_moving_on(pool, w)
    head = git(path, "rev-parse", "HEAD")
    made: list[str] = []

    def commit_and_abandon():
        made.append(commit_file(path, "abandoned.txt", "abandoned\n", "abandoned after the check"))
        git(path, "reset", "-q", "--hard", head)

    _after_check(monkeypatch, landed, commit_and_abandon)

    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))

    monkeypatch.undo()
    assert made
    assert code == EXIT_HELD
    assert "reflog changed after the check" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head
    assert not (path / "moved-on.txt").exists(), "the reset wrote the default branch's files"


def test_an_untracked_file_written_after_the_check_is_held_and_nothing_is_written(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path = _slot_moving_on(pool, w)
    head = git(path, "rev-parse", "HEAD")
    _after_check(monkeypatch, landed,
                 lambda: (path / "late.txt").write_text("written after the check\n", encoding="utf-8"))

    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "1 untracked file" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head
    assert not (path / "moved-on.txt").exists(), "the reset wrote the default branch's files"


def test_a_tracked_file_edited_after_the_recheck_is_held_before_anything_is_written(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path = _slot_moving_on(pool, w)
    head = git(path, "rev-parse", "HEAD")
    _around_git(monkeypatch, lambda args: args[:1] == ("update-index",),
                before=lambda cwd: (path / "README.md").write_text("edited at the last moment\n", encoding="utf-8"))

    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "a tracked file changed after the check" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head
    assert (path / "README.md").read_text(encoding="utf-8") == "edited at the last moment\n"
    assert not (path / "moved-on.txt").exists(), "the reset wrote the default branch's files"


# --- the write -----------------------------------------------------------------------------------------


def test_a_read_tree_that_fails_leaves_head_where_it_was(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path = _slot_moving_on(pool, w)
    head = git(path, "rev-parse", "HEAD")
    _around_git(monkeypatch, lambda args: args[:1] == ("read-tree",), fail=True)

    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "reset refused or not finished" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head


def test_a_reflog_line_written_around_the_lock_during_the_reset_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path = _slot_moving_on(pool, w)
    head = git(path, "rev-parse", "HEAD")
    unlanded = git(path, "commit-tree", EMPTY_TREE, "-p", head, "-m", "only named by a stray reflog line")
    log = _git_dir(path) / "logs" / "HEAD"

    def a_writer_that_ignores_the_lock(cwd):
        with open(log, "ab") as f:
            f.write(f"{head} {unlanded} someone <someone@example.invalid> {int(time.time())} +0000\tstray\n"
                    .encode("ascii"))

    _around_git(monkeypatch, lambda args: args[:1] == ("read-tree",), after=a_writer_that_ignores_the_lock)

    code = _outcome(lambda: pool.return_slot(str(path), got["lease"], None))

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "not exactly what was checked plus the line cc-worktrees wrote" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head


def test_a_new_slot_whose_head_moved_before_it_was_marked_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    made: list[str] = []

    def move_head_without_a_reflog_line(cwd):
        tip = git(w.repo, "rev-parse", f"origin/{w.default_branch}")
        slot = w.repo.parent / f"{w.repo.name}.worktrees" / "wt01"
        commit = git(slot, "commit-tree", EMPTY_TREE, "-p", tip, "-m", "written into the new slot")
        (_git_dir(slot) / "HEAD").write_text(commit + "\n", encoding="ascii")
        made.append(commit)

    _around_git(monkeypatch, lambda args: args[:2] == ("worktree", "add"), after=move_head_without_a_reflog_line)

    code = _outcome(lambda: pool.get(str(w.repo), "owner", 4))

    monkeypatch.undo()
    assert made
    assert code == 1
    slot = w.slot("wt01")
    assert slot["state"] == "held"
    assert "not the default branch tip it was created at" in slot["reason"]


# --- destroy -----------------------------------------------------------------------------------------


def _free_slot(pool, w):
    got = pool.get(str(w.repo), "owner", 4)
    pool.return_slot(got["path"], got["lease"], None)
    return got, Path(got["path"])


def test_a_commit_made_between_the_destroy_check_and_the_removal_is_never_removed(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path = _free_slot(pool, w)
    made: list[str] = []
    _after_check(monkeypatch, landed,
                 lambda: made.append(commit_file(path, "late.txt", "committed mid-destroy\n", "mid-destroy")))

    code = _outcome(lambda: pool.destroy_slot(str(path), True, False, False, None))

    monkeypatch.undo()
    assert made
    assert code == EXIT_HELD
    assert path.is_dir(), "destroy removed a slot holding a commit made after its check"
    assert git(path, "rev-parse", "HEAD") == made[0]


def test_an_untracked_file_written_at_the_removal_is_kept_and_the_slot_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got, path = _free_slot(pool, w)
    _around_git(monkeypatch, lambda args: args[:2] == ("worktree", "remove"),
                before=lambda cwd: (path / "late.txt").write_text("written at the removal\n", encoding="utf-8"))

    code = _outcome(lambda: pool.destroy_slot(str(path), True, False, False, None))

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert (path / "late.txt").read_text(encoding="utf-8") == "written at the removal\n"
    slot = w.slot(got["slot"])
    assert slot["state"] == "held"
    assert "not removed" in slot["reason"]
