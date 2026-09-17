"""Round 4, finding 2: nothing released a held slot, so a pool lost slots permanently.

Work is kept safe by refs in the main repository, not by keeping the worktree. Every commit the check
cannot prove landed is pinned under `refs/cc-worktrees/<slot>/`, and a pinned commit is reachable for
ever - `git gc` cannot take it. So a held slot whose unproven work is all pinned can go back in the
pool without losing anything, and `release <slot> --confirm-abandon` is the one command that does it.

The three dead ends round 4 measured are cases (a), (b) and (c) below.
"""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

import gitrun
from conftest import GIT_ENV, commit_file, git

EXIT_HELD = 3
EXIT_USAGE = 2


def _pins(repo: Path, slot: str) -> list[str]:
    return git(repo, "for-each-ref", "--format=%(objectname)", f"refs/cc-worktrees/{slot}/").split()


def _object_exists(repo: Path, commit: str) -> bool:
    return git(repo, "cat-file", "-t", commit, check=False) == "commit"


def _abandon_a_commit(path: Path) -> str:
    commit = commit_file(path, "x.txt", "the work the hold exists for\n", "X")
    git(path, "reset", "-q", "--hard", "HEAD~1")
    return commit


def _held_with_an_abandoned_commit(w) -> tuple[dict, str]:
    got = w.get()
    commit = _abandon_a_commit(Path(got["path"]))
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == EXIT_HELD
    return got, commit


def _outcome(call) -> int:
    try:
        call()
        return 0
    except Exception as ex:
        code = getattr(ex, "exit_code", None)
        assert code is not None, f"escaped {type(ex).__name__}: {ex}"
        return code


# ---------------------------------------------------------------------------------------------------
# It is never implicit
# ---------------------------------------------------------------------------------------------------


def test_release_without_confirm_abandon_is_a_usage_error_and_changes_nothing(local_world):
    w = local_world
    got, commit = _held_with_an_abandoned_commit(w)

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--json")

    assert res.code == EXIT_USAGE, res.out + res.err
    assert res.data["code"] == "usage"
    # The message must be about the missing confirmation, not about an unknown command: a usage exit
    # code alone would pass on a build that has no release command at all.
    assert "--confirm-abandon" in res.data["error"], res.data
    assert w.slot(got["slot"])["state"] == "held"
    assert Path(got["path"]).is_dir()


def test_release_refuses_a_free_slot_and_an_in_use_slot(local_world):
    w = local_world
    got = w.get()

    in_use = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")
    assert in_use.code == 1, in_use.out + in_use.err
    assert in_use.data["code"] == "not-held"

    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == 0
    free = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")
    assert free.code == 1, free.out + free.err
    assert free.data["code"] == "not-held"
    assert Path(got["path"]).is_dir()


# ---------------------------------------------------------------------------------------------------
# It refuses whatever it cannot pin
# ---------------------------------------------------------------------------------------------------


def test_release_refuses_while_an_uncommitted_or_untracked_file_is_in_the_slot(local_world):
    w = local_world
    got, commit = _held_with_an_abandoned_commit(w)
    path = Path(got["path"])
    (path / "notes.txt").write_text("a file no ref can hold\n", encoding="utf-8", newline="\n")

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "notes.txt" in res.data["error"], res.data
    assert path.is_dir() and (path / "notes.txt").exists()
    assert w.slot(got["slot"])["state"] == "held"
    assert git(path, "stash", "list") == "", "release must never stash a file for the user"


def test_release_refuses_while_a_repository_of_its_own_is_inside_the_slot(local_world):
    w = local_world
    got, commit = _held_with_an_abandoned_commit(w)
    path = Path(got["path"])
    git(w.tmp, "clone", "-q", w.remote_url, str(path / "bin" / "vendored"))

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "bin/vendored" in res.data["error"].replace("\\", "/"), res.data
    assert (path / "bin" / "vendored" / ".git").is_dir()


def test_release_refuses_while_a_tracked_file_hides_its_edits_from_git_status(local_world):
    w = local_world
    got, commit = _held_with_an_abandoned_commit(w)
    path = Path(got["path"])
    git(path, "update-index", "--assume-unchanged", "README.md")
    (path / "README.md").write_text("an edit git status cannot see\n", encoding="utf-8", newline="\n")

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "README.md" in res.data["error"], res.data
    assert path.is_dir()


def test_release_refuses_when_a_pin_cannot_be_written(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    commit = _abandon_a_commit(Path(got["path"]))
    assert _outcome(lambda: pool.return_slot(got["path"], got["lease"], None)) == EXIT_HELD
    for ref in git(w.repo, "for-each-ref", "--format=%(refname)", f"refs/cc-worktrees/{got['slot']}/").split():
        git(w.repo, "update-ref", "-d", ref)
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if args[:1] == ("update-ref",) and "-d" not in args:
            raise gitrun.GitError(list(args), 128, "fatal: simulated failure")
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)
    code = _outcome(lambda: pool.release_slot(got["slot"], str(w.repo)))
    monkeypatch.undo()

    assert code == EXIT_HELD
    assert Path(got["path"]).is_dir(), "the slot was dropped although its work could not be pinned"
    assert w.slot(got["slot"])["state"] == "held"
    assert _object_exists(w.repo, commit)


def test_release_refuses_when_a_pin_reports_success_but_is_not_there(in_process, monkeypatch):
    """git answering 0 is not the ref being there. The pins are the whole point of the release, so each
    one is read back and the commit itself asked for by name."""
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    commit = _abandon_a_commit(Path(got["path"]))
    assert _outcome(lambda: pool.return_slot(got["path"], got["lease"], None)) == EXIT_HELD
    for ref in git(w.repo, "for-each-ref", "--format=%(refname)", f"refs/cc-worktrees/{got['slot']}/").split():
        git(w.repo, "update-ref", "-d", ref)
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        if args[:1] == ("update-ref",) and "-d" not in args:
            return subprocess.CompletedProcess(["git", *args], 0, "", "")
        return original(cwd, *args, **kwargs)

    monkeypatch.setattr(gitrun, "run", run)
    code = _outcome(lambda: pool.release_slot(got["slot"], str(w.repo)))
    monkeypatch.undo()

    assert code == EXIT_HELD
    assert Path(got["path"]).is_dir()
    assert "not pinned" in w.slot(got["slot"])["reason"], w.slot(got["slot"])["reason"]
    assert _object_exists(w.repo, commit)


def test_release_holds_head_lock_so_nothing_can_commit_while_it_works(in_process, monkeypatch):
    """The commits are read and the directory removed under git's own HEAD.lock, so a commit made in
    the slot at that moment cannot slip past the pins - git itself refuses it."""
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    _abandon_a_commit(path)
    assert _outcome(lambda: pool.return_slot(got["path"], got["lease"], None)) == EXIT_HELD
    original = landed.release_candidates
    tried = {}

    def candidates_then_try_to_commit(worktree):
        answer = original(worktree)
        attempt = subprocess.run(["git", "commit", "--allow-empty", "-m", "sneaked in while releasing"],
                                 cwd=str(path), capture_output=True, text=True,
                                 env={**os.environ, **GIT_ENV})
        tried["code"], tried["err"] = attempt.returncode, attempt.stderr
        return answer

    monkeypatch.setattr(landed, "release_candidates", candidates_then_try_to_commit)
    code = _outcome(lambda: pool.release_slot(got["slot"], str(w.repo)))
    monkeypatch.undo()

    assert tried["code"] != 0, "a commit went through while the release was reading the slot"
    assert "HEAD.lock" in tried["err"], tried["err"]
    assert code == 0
    assert w.list() == []


# ---------------------------------------------------------------------------------------------------
# (a) work the holder abandoned
# ---------------------------------------------------------------------------------------------------


def test_a_abandoned_work_is_pinned_the_slot_goes_back_and_the_work_survives_gc(local_world):
    w = local_world
    got, commit = _held_with_an_abandoned_commit(w)

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")

    assert res.code == 0, res.out + res.err
    assert res.data["removed"] is True
    assert not Path(got["path"]).exists()
    assert [s["slot"] for s in w.list()] == []
    assert _pins(w.repo, got["slot"]) == [commit]
    assert [p["commit"] for p in res.data["pins"]] == [commit]
    assert res.data["pins"][0]["ref"] == f"refs/cc-worktrees/{got['slot']}/{commit}"
    # The held return had already pinned this commit, so the release kept it rather than writing it:
    # pinned counts every pin the slot has, pinned_now only what this call added.
    assert res.data["pinned"] == 1 and res.data["pinned_now"] == 0, res.data

    git(w.repo, "gc", "-q", "--prune=now")
    assert _object_exists(w.repo, commit), "git gc took work the release said it had kept"
    assert _pins(w.repo, got["slot"]) == [commit]

    after = w.get()
    assert after["slot"] != got["slot"], "a slot name whose pins still stand must not be handed out again"
    assert Path(after["path"]).is_dir()
    assert w.run("return", after["path"], "--lease", after["lease"], "--json").code == 0


def test_the_text_output_lists_every_pinned_ref_and_says_how_to_get_the_work_back(local_world):
    from cc_shared import axi_output

    w = local_world
    got, commit = _held_with_an_abandoned_commit(w)

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon")

    assert res.code == 0, res.out + res.err
    assert res.out.isascii()
    fields, records = axi_output.parse_list(res.out, "pins")
    assert fields == ["ref", "commit"]
    assert [r["commit"] for r in records] == [commit]
    assert "git log" in res.out and "git branch" in res.out


# ---------------------------------------------------------------------------------------------------
# (b) a rewritten HEAD reflog after git gc
# ---------------------------------------------------------------------------------------------------


def test_b_one_git_gc_holds_every_slot_and_release_gives_the_whole_pool_back(local_world):
    w = local_world
    slots = [w.get(pool_size=2) for _ in range(2)]
    for got in slots:
        assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == 0

    git(w.repo, "gc", "-q")

    full = w.run("get", "--repo", str(w.repo), "--holder", "after-gc", "--pool-size", "2", "--json")
    assert full.code == 4, full.out + full.err
    assert all(s["state"] == "held" for s in w.list()), w.list()

    for got in slots:
        res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")
        assert res.code == 0, res.out + res.err
        assert res.data["pinned"] == 0, "nothing in these slots was unproven, so nothing needed pinning"

    assert w.list() == []
    again = w.run("get", "--repo", str(w.repo), "--holder", "after-release", "--pool-size", "2", "--json")
    assert again.code == 0, again.out + again.err
    assert Path(again.data["path"]).is_dir()


# ---------------------------------------------------------------------------------------------------
# (c) the slot directory deleted by hand
# ---------------------------------------------------------------------------------------------------


def test_c_a_slot_whose_directory_was_deleted_by_hand_goes_back_in_the_pool(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    subprocess.run(["cmd", "/c", "rmdir", "/s", "/q", str(path)], capture_output=True)
    shutil.rmtree(path, ignore_errors=True)
    assert not path.exists()
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == EXIT_HELD

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")

    assert res.code == 0, res.out + res.err
    assert w.list() == []
    again = w.get()
    assert Path(again["path"]).is_dir()
    assert w.run("return", again["path"], "--lease", again["lease"], "--json").code == 0


# ---------------------------------------------------------------------------------------------------
# The stash the slot owns (round 4, finding 1)
# ---------------------------------------------------------------------------------------------------


def test_release_pins_the_stash_entries_the_slot_added_before_it_lets_the_slot_go(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    (path / "README.md").write_text("work stashed in the slot\n", encoding="utf-8", newline="\n")
    git(path, "stash", "push", "-m", "work stashed in the slot")
    stash = git(w.repo, "rev-parse", "refs/stash")
    assert w.run("return", got["path"], "--lease", got["lease"], "--json").code == EXIT_HELD

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")

    assert res.code == 0, res.out + res.err
    assert stash in [p["commit"] for p in res.data["pins"]], res.data
    git(w.repo, "stash", "clear")
    git(w.repo, "gc", "-q", "--prune=now")
    assert _object_exists(w.repo, stash), "the stashed work went with the stash the user cleared"
    assert git(w.repo, "show", f"{stash}:README.md") == "work stashed in the slot"


# ---------------------------------------------------------------------------------------------------
# It never deletes
# ---------------------------------------------------------------------------------------------------


def test_release_leaves_the_pins_that_were_already_there(local_world):
    w = local_world
    got, commit = _held_with_an_abandoned_commit(w)
    assert _pins(w.repo, got["slot"]) == [commit]
    second = commit_file(Path(got["path"]), "y.txt", "more\n", "Y")

    res = w.run("release", got["slot"], "--repo", str(w.repo), "--confirm-abandon", "--json")

    assert res.code == 0, res.out + res.err
    assert sorted(_pins(w.repo, got["slot"])) == sorted([commit, second])
    git(w.repo, "gc", "-q", "--prune=now")
    assert _object_exists(w.repo, commit) and _object_exists(w.repo, second)
