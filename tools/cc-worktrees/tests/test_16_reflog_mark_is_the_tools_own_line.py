"""Fix round 2, finding 2: the reflog mark names a line the tool itself wrote, never "the newest entry".

The reset appends its own reflog line, with a nonce, while it holds HEAD.lock, and the saved mark is that
line. A commit made after the reset but before the state is saved is therefore AFTER the mark, and the next
check sees it. A mark that does not name the tool's own line at its recorded position cannot be vouched for.
"""

from __future__ import annotations

import json
from pathlib import Path

from conftest import commit_file, git

EXIT_HELD = 3


def test_a_commit_between_the_reset_and_the_state_save_is_never_destroyed(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    original_reset = landed.reset
    abandoned: list[str] = []

    def reset_then_commit_in_the_window(*args, **kwargs):
        answer = original_reset(*args, **kwargs)
        # HEAD.lock is released; the state is not saved yet.
        abandoned.append(commit_file(path, "abandoned.txt", "lost unique work\n", "work in the mark window"))
        git(path, "reset", "-q", "--hard", f"origin/{w.default_branch}")
        return answer

    monkeypatch.setattr(landed, "reset", reset_then_commit_in_the_window)
    try:
        pool.return_slot(str(path), got["lease"], None)
        returned_held = False
    except pool.ToolError as ex:
        returned_held = ex.exit_code == EXIT_HELD
    monkeypatch.setattr(landed, "reset", original_reset)
    assert abandoned

    if not returned_held:
        try:
            pool.destroy_slot(str(path), True, False, False, None)
            destroyed = True
        except pool.ToolError as ex:
            assert ex.exit_code == EXIT_HELD, ex.message
            destroyed = False
        assert not destroyed, "destroy removed a slot whose reflog holds an unlanded commit"
    assert path.is_dir()
    assert abandoned[0] in git(path, "reflog", "show", "--format=%H", "HEAD").split()


def test_a_mark_moved_to_the_newest_entry_is_not_trusted(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    sha = commit_file(path, "abandoned.txt", "unique\n", "unique")
    git(path, "reset", "-q", "--hard", f"origin/{w.default_branch}")
    newest = git(path, "reflog", "show", "--format=%H", "HEAD").split()
    state = w.state_file()
    data = json.loads(state.read_text(encoding="utf-8"))
    entry = data["slots"][got["slot"]]
    entry["reflog_position"] = len(newest) - 1
    entry["reflog_commit"] = newest[0]
    entry["reflog_nonce"] = "0123456789abcdef0123456789abcdef"
    state.write_text(json.dumps(data), encoding="utf-8")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert w.slot(got["slot"])["state"] == "held"
    assert sha in git(path, "reflog", "show", "--format=%H", "HEAD").split()


def test_every_handout_leaves_the_tools_own_reflog_line_newest(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    subjects = git(path, "reflog", "show", "--format=%gs", "HEAD").splitlines()
    assert subjects[0].startswith(f"cc-worktrees: reset to {got['commit']} ")
    assert w.run("return", got["path"], "--lease", got["lease"]).code == 0
    again = w.get()
    assert again["slot"] == got["slot"]
    subjects = git(path, "reflog", "show", "--format=%gs", "HEAD").splitlines()
    assert subjects[0].startswith(f"cc-worktrees: reset to {again['commit']} ")


def test_a_commit_made_while_a_new_slot_is_created_holds_that_slot(in_process, monkeypatch):
    w, pool, landed = in_process
    original_bound = landed.require_bound
    abandoned: list[str] = []

    def bound_then_commit(worktree, repo, recorded):
        answer = original_bound(worktree, repo, recorded)
        if not abandoned:
            base = git(worktree, "rev-parse", "HEAD")
            abandoned.append(commit_file(worktree, "early.txt", "early work\n", "work before the mark"))
            git(worktree, "reset", "-q", "--hard", base)
        return answer

    monkeypatch.setattr(landed, "require_bound", bound_then_commit)
    try:
        got = pool.get(str(w.repo), "owner", 4)
        handed_out = got["slot"]
    except pool.ToolError as ex:
        handed_out = None
        assert ex.code == "create-failed", ex.message
    monkeypatch.setattr(landed, "require_bound", original_bound)
    assert abandoned

    if handed_out is not None:
        # The slot was handed out: its return must still see the early commit.
        try:
            pool.return_slot(got["path"], got["lease"], None)
            freed = True
        except pool.ToolError:
            freed = False
        assert not freed, "a commit made before the new slot's mark was never checked"
    assert w.list()[0]["state"] in ("held", "in-use")
