"""Guard inventory, fix round 2: the landed-proof and reflog guards that no earlier test could tell from
their absence. Each case makes git's answer say less than the proof needs, or hides a path the proof must
compare, and requires the slot to be held with its commit where it was."""

from __future__ import annotations

import json
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


def _replace_git(monkeypatch, matches, answer) -> None:
    """Every git call `matches` accepts gets `answer(original_result)` instead of git's own result."""
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if matches(args):
            return answer(cwd, args, original)
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)


def _fail(cwd, args, original):
    raise gitrun.GitError(list(args), 128, "fatal: simulated failure")


def test_an_empty_commit_listing_for_a_commit_on_no_remote_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    sha = commit_file(path, "work.txt", "only here\n", "unpushed work")
    _replace_git(monkeypatch, lambda args: args[:1] == ("rev-list",),
                 lambda cwd, args, original: subprocess.CompletedProcess(["git", *args], 0, "", ""))

    code = _return(pool, path, got["lease"])

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "lists no commit to check and is on no remote branch" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == sha


def test_a_root_commit_whose_patch_landed_but_whose_file_changed_again_is_held(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    git(path, "checkout", "-q", "--orphan", "fresh")
    git(path, "rm", "-rfq", ".")
    root = commit_file(path, "fresh.txt", "first\n", "a new history")
    git(path, "checkout", "-q", "--detach")
    git(path, "branch", "-q", "-D", "fresh")
    w.push_from_other_clone({"fresh.txt": "first\n"}, "the same file lands")
    w.push_from_other_clone({"fresh.txt": "second\n"}, "and changes again")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert root[:12] in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == root
    assert (path / "fresh.txt").read_text(encoding="utf-8") == "first\n"


def test_a_patch_whose_path_upstream_refilled_by_a_rename_is_held(local_world):
    w = local_world
    # A rename is only seen when rename detection is on; porcelain git diff turns it on from config.
    git(w.repo, "config", "diff.renames", "true")
    body = "".join(f"line {i} of a file long enough to be recognised when it moves\n" for i in range(40))
    w.push_from_other_clone({"gone.txt": "to be deleted\n", "moves.txt": body}, "two files")
    got = w.get()
    path = Path(got["path"])
    git(path, "rm", "-q", "gone.txt")
    git(path, "commit", "-q", "-m", "delete gone.txt")
    sha = git(path, "rev-parse", "HEAD")
    other = w.second_clone()
    git(other, "rm", "-q", "gone.txt")
    # A different message: the same patch under its own commit id, never the identical commit.
    git(other, "commit", "-q", "-m", "delete gone.txt upstream")
    git(other, "mv", "moves.txt", "gone.txt")
    git(other, "commit", "-q", "-m", "moves.txt takes the deleted name")
    git(other, "push", "-q", "origin", w.default_branch)

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert sha[:12] in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == sha
    assert not (path / "gone.txt").exists()


def test_a_git_failure_inside_the_proof_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    sha = commit_file(path, "work.txt", "only here\n", "unpushed work")
    _replace_git(monkeypatch, lambda args: args[:1] == ("cherry",), _fail)

    code = _return(pool, path, got["lease"])

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert w.slot(got["slot"])["reason"].startswith("cannot verify")
    assert git(path, "rev-parse", "HEAD") == sha


def test_reflog_logging_switched_off_is_held(local_world):
    w = local_world
    got = w.get()
    git(w.repo, "config", "core.logAllRefUpdates", "false")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "core.logAllRefUpdates is off" in w.slot(got["slot"])["reason"]


def test_a_logging_setting_that_cannot_be_read_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    _replace_git(monkeypatch, lambda args: args[:2] == ("config", "--get"),
                 lambda cwd, args, original: subprocess.CompletedProcess(["git", *args], 3, "", "error: simulated"))

    code = _return(pool, Path(got["path"]), got["lease"])

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "cannot read core.logAllRefUpdates" in w.slot(got["slot"])["reason"]


def test_a_reflog_that_cannot_be_read_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    _replace_git(monkeypatch, lambda args: args[:1] == ("reflog",), _fail)

    code = _return(pool, Path(got["path"]), got["lease"])

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "cannot read the HEAD reflog" in w.slot(got["slot"])["reason"]


def test_a_reflog_that_does_not_split_into_whole_entries_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)

    def one_field_too_many(cwd, args, original):
        answer = original(cwd, *args)
        return subprocess.CompletedProcess(answer.args, 0, answer.stdout + "stray\0", answer.stderr)

    _replace_git(monkeypatch, lambda args: args[:1] == ("reflog",), one_field_too_many)

    code = _return(pool, Path(got["path"]), got["lease"])

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "could not be read entry by entry" in w.slot(got["slot"])["reason"]


def test_a_mark_recorded_beyond_the_end_of_the_reflog_is_held(local_world):
    w = local_world
    got = w.get()
    state = w.state_file()
    data = json.loads(state.read_text(encoding="utf-8"))
    data["slots"][got["slot"]]["reflog_position"] += 1000
    state.write_text(json.dumps(data, indent=2) + "\n", encoding="ascii")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "lost the line cc-worktrees wrote" in w.slot(got["slot"])["reason"]
