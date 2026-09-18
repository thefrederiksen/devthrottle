"""Guard inventory, fix round 2: the fetch-side guards that no earlier test could tell from their absence.

Each case makes the remote, or git's answer about it, say less than a proof needs. The slot must be held
with that guard's own reason, and left exactly as it was.
"""

from __future__ import annotations

from pathlib import Path

import gitrun
from conftest import commit_file, git

EXIT_HELD = 3
EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"


def _return(pool, path: Path, lease: str) -> int | str:
    """The exit code a return ends with, or the name of an exception that escaped instead of a hold."""
    try:
        pool.return_slot(str(path), lease, None)
        return 0
    except pool.ToolError as ex:
        return ex.exit_code
    except Exception as ex:  # an escaped error is the failure this file looks for, so it is reported
        return f"escaped {type(ex).__name__}"


def _after_git(monkeypatch, command: str, step) -> None:
    """Run `step(cwd)` right after every git call whose first argument is `command`."""
    original = gitrun.run

    def run(cwd, *args, **kwargs):
        answer = original(cwd, *args, **kwargs)
        if args and args[0] == command:
            step(cwd)
        return answer

    monkeypatch.setattr(gitrun, "run", run)


def test_a_remote_that_names_no_default_branch_is_held_and_no_branch_is_assumed(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    head = git(path, "rev-parse", "HEAD")
    remote = Path(w.remote_url)
    # A branch whose name is what a guess would reach, and a remote HEAD that names no branch.
    other = w.second_clone()
    commit_file(other, "guessed.txt", "guessed\n", "not the default branch")
    git(other, "push", "-q", "origin", "HEAD:refs/heads/None")
    git(remote, "update-ref", "--no-deref", "HEAD", git(remote, "rev-parse", w.default_branch))

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert "did not say which branch is its default" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head
    assert not (path / "guessed.txt").exists()


def test_a_fetch_that_fails_is_held_even_though_the_remote_answered_the_listing(local_world):
    w = local_world
    got = w.get()
    path = Path(got["path"])
    sha = commit_file(path, "topic.txt", "topic\n", "topic work")
    git(path, "push", "-q", "origin", "HEAD:refs/heads/topic")
    git(w.repo, "fetch", "-q", "origin")
    git(w.second_clone(), "push", "-q", "origin", "--delete", "topic")
    # git fetch --prune cannot delete a tracking ref whose lock file exists, and exits non-zero; git
    # ls-remote still answers. Without the guard the stale origin/topic would prove the commit.
    (w.repo / ".git" / "refs" / "remotes" / "origin" / "topic.lock").write_text("", encoding="ascii")

    res = w.run("return", got["path"], "--lease", got["lease"], "--json")

    assert res.code == EXIT_HELD, res.out + res.err
    assert w.slot(got["slot"])["reason"].startswith("cannot verify")
    assert git(path, "rev-parse", "HEAD") == sha
    assert (path / "topic.txt").exists()


def test_a_tracking_ref_missing_after_the_fetch_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    head = git(path, "rev-parse", "HEAD")
    _after_git(monkeypatch, "fetch",
               lambda cwd: git(Path(cwd), "update-ref", "-d", f"refs/remotes/origin/{w.default_branch}"))

    code = _return(pool, path, got["lease"])

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "is missing after the fetch" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head


def test_a_tracking_ref_the_remote_did_not_advertise_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    head = git(path, "rev-parse", "HEAD")
    # The slot's HEAD is on a remote branch, so only the tip check stands between it and a reset.
    git(path, "push", "-q", "origin", "HEAD:refs/heads/keep")
    unrelated = git(w.repo, "commit-tree", EMPTY_TREE, "-m", "a commit the remote never advertised")
    _after_git(monkeypatch, "fetch",
               lambda cwd: git(Path(cwd), "update-ref", f"refs/remotes/origin/{w.default_branch}", unrelated))

    code = _return(pool, path, got["lease"])

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "does not match what the remote advertised" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head
    assert (path / "README.md").exists()


def test_a_tracking_ref_that_cannot_be_read_under_the_lock_is_held(in_process, monkeypatch):
    w, pool, landed = in_process
    got = pool.get(str(w.repo), "owner", 4)
    path = Path(got["path"])
    head = git(path, "rev-parse", "HEAD")
    original = landed.fetch_default

    def fetch_then_the_ref_goes(cwd):
        answer = original(cwd)
        git(w.repo, "update-ref", "-d", f"refs/remotes/origin/{w.default_branch}")
        return answer

    monkeypatch.setattr(landed, "fetch_default", fetch_then_the_ref_goes)

    code = _return(pool, path, got["lease"])

    monkeypatch.undo()
    assert code == EXIT_HELD
    assert "cannot be read" in w.slot(got["slot"])["reason"]
    assert git(path, "rev-parse", "HEAD") == head


def test_a_network_timeout_setting_of_zero_is_refused_and_changes_nothing(local_world):
    w = local_world
    got = w.get()

    res = w.run("return", got["path"], "--lease", got["lease"], "--json",
                env_extra={"CC_WORKTREES_NETWORK_TIMEOUT": "0"})

    assert res.code == 1, res.out + res.err
    assert res.data["code"] == "bad-setting"
    assert w.slot(got["slot"])["state"] == "in-use"
